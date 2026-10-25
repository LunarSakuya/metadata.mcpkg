using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relay.Core;

public sealed record ClientResult(
    ClientPlan Plan,
    AssetsDocument Assets,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pure-ish resolver: all inputs are already verified and parsed. No file or
/// network I/O happens here. One version document + one asset index + one
/// platform produces one complete client plan; the same version document can
/// also produce a server plan.
/// </summary>
public static class MojangResolver
{
    public const long MaxTotalBytes = 64L * 1024 * 1024 * 1024;
    public const int MaxLibraries = 1_000_000;
    public const int MaxAssetObjects = 1_000_000;
    public const int MaxFiles = 1_000_000;

    public static ClientResult ResolveClient(
        ParsedVersion version,
        ParsedIndex index,
        Platform platform,
        string key,
        AssetsDocument? assets = null)
    {
        var warnings = new List<string>();
        if (version.Client is null)
            throw new RelayException(RelayErrorCode.MissingField, "$.version.downloads.client", "client download is missing");
        if (version.AssetIndex is null)
            throw new RelayException(RelayErrorCode.MissingField, "$.version.assetIndex", "asset index is missing");
        if (string.IsNullOrWhiteSpace(version.MainClass))
            throw new RelayException(RelayErrorCode.MissingField, "$.version.mainClass", "mainClass is missing");
        EnsureNoControl(version.MainClass, "$.version.mainClass");

        assets ??= BuildAssets(index, version.AssetIndex.Id);
        // Mojang's assetIndex.totalSize counts duplicate hash entries under
        // different logical names; the downloaded object set is unique. Do not
        // emit a warning for this benign difference.

        var files = new List<PlanFile>();
        var byPath = new Dictionary<string, PlanFile>(StringComparer.Ordinal);

        void Add(string role, string path, string sha1, long size, IReadOnlyList<string> urls, ExtractRule? extract = null)
        {
            var safePath = SafeRelativePath.Parse(path, $"{role}.path").Value;
            if (byPath.TryGetValue(safePath, out var existing))
            {
                if (!string.Equals(existing.Sha1, sha1, StringComparison.Ordinal) || existing.Size != size)
                    throw new RelayException(RelayErrorCode.PathConflict, safePath, "two different artifacts map to the same path");
                return;
            }

            if (files.Count >= MaxFiles)
                throw new RelayException(RelayErrorCode.TooLarge, safePath, "too many files in plan");

            var file = new PlanFile(role, safePath, sha1, size, urls, extract);
            byPath.Add(safePath, file);
            files.Add(file);
        }

        // Client jar path is fixed. A metadata-provided "path" must not move it.
        var clientPath = $"versions/{version.Id}/{version.Id}.jar";
        Add("client", clientPath, version.Client.Sha1, version.Client.Size, [version.Client.Url]);

        // Log configuration.
        LoggingInfo? logging = null;
        if (version.LoggingFile is not null)
        {
            if (version.LoggingId is null)
                throw new RelayException(RelayErrorCode.MissingField, "$.version.logging.client.file.id", "log id is missing");
            var logPath = $"assets/log_configs/{version.LoggingId}";
            Add("log", logPath, version.LoggingFile.Sha1, version.LoggingFile.Size, [version.LoggingFile.Url]);
            if (version.LoggingArgument is not null)
            {
                var argument = version.LoggingArgument.Replace("${path}", "${logConfig}", StringComparison.Ordinal);
                logging = new LoggingInfo(ApplyToken(argument, version, platform, "$.version.logging.client.argument"), logPath);
            }
        }

        // Asset index itself.
        var indexPath = $"assets/indexes/{version.AssetIndex.Id}.json";
        Add("assetIndex", indexPath, version.AssetIndex.Sha1, version.AssetIndex.Size, [version.AssetIndex.Url]);

        // Libraries and natives.
        var libraries = J.OptionalArray(version.Root, "libraries", "$.version");
        if (libraries is not null)
        {
            var context = new RuleContext { Platform = platform };
            var libraryIndex = 0;
            foreach (var library in libraries.Value.EnumerateArray())
            {
                var libraryPath = J.Index("$.version.libraries", libraryIndex++);
                if (libraryIndex > MaxLibraries)
                    throw new RelayException(RelayErrorCode.TooLarge, "$.version.libraries", "too many libraries");

                J.Object(library, libraryPath);
                if (!Rules.Evaluate(library, context, libraryPath)) continue;

                var name = J.OptionalString(library, "name", libraryPath);
                if (string.IsNullOrWhiteSpace(name))
                    throw new RelayException(RelayErrorCode.MissingField, J.Field(libraryPath, "name"), "library name is missing");

                var downloads = J.OptionalObject(library, "downloads", libraryPath);
                var natives = J.OptionalObject(library, "natives", libraryPath);

                if (natives is not null)
                {
                    var nativesPath = J.Field(libraryPath, "natives");
                    var classifier = J.OptionalString(natives.Value, platform.Os, nativesPath);
                    if (classifier is null) continue; // no native for this OS: not an error

                    classifier = classifier.Replace("${arch}", platform.Bits.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                    if (downloads is null)
                        throw new RelayException(RelayErrorCode.MissingField, J.Field(libraryPath, "downloads"), "native library has no downloads");
                    var downloadsPath = J.Field(libraryPath, "downloads");
                    var classifiers = J.OptionalObject(downloads.Value, "classifiers", downloadsPath);
                    if (classifiers is null)
                        throw new RelayException(RelayErrorCode.MissingField, J.Field(downloadsPath, "classifiers"), "native library has no classifiers");
                    var nativeArtifact = J.OptionalObject(classifiers.Value, classifier, J.Field(downloadsPath, "classifiers"));
                    if (nativeArtifact is null)
                        throw new RelayException(RelayErrorCode.MissingField, J.Field(downloadsPath, "classifiers"), "native classifier is missing");

                    var nativeDownload = ParseDownloadRef(nativeArtifact.Value, J.Field(downloadsPath, "classifiers"));
                    Add("native", LibraryPath(name, classifier), nativeDownload.Sha1, nativeDownload.Size,
                        [nativeDownload.Url], ExtractOf(library, libraryPath));

                    var baseArtifact = J.OptionalObject(downloads.Value, "artifact", downloadsPath);
                    if (baseArtifact is not null)
                    {
                        var baseDownload = ParseDownloadRef(baseArtifact.Value, J.Field(downloadsPath, "artifact"));
                        var basePath = J.OptionalString(baseArtifact.Value, "path", J.Field(downloadsPath, "artifact"))
                                       ?? LibraryPath(name);
                        if (!basePath.StartsWith("libraries/", StringComparison.Ordinal))
                            basePath = "libraries/" + basePath;
                        Add("library", basePath, baseDownload.Sha1, baseDownload.Size, [baseDownload.Url]);
                    }

                    continue;
                }

                if (downloads is null)
                    throw new RelayException(RelayErrorCode.MissingField, J.Field(libraryPath, "downloads"), "library has no downloads");
                var downloadsObject = J.Field(libraryPath, "downloads");
                var artifact = J.OptionalObject(downloads.Value, "artifact", downloadsObject);
                if (artifact is null)
                    throw new RelayException(RelayErrorCode.MissingField, J.Field(downloadsObject, "artifact"), "library has no artifact");

                var artifactPath = J.Field(downloadsObject, "artifact");
                var downloadRef = ParseDownloadRef(artifact.Value, artifactPath);
                var path = J.OptionalString(artifact.Value, "path", artifactPath) ?? LibraryPath(name);
                if (!path.StartsWith("libraries/", StringComparison.Ordinal))
                    path = "libraries/" + path;
                Add("library", path, downloadRef.Sha1, downloadRef.Size, [downloadRef.Url]);
            }
        }

        var hasNatives = files.Any(file => file.Role == "native");

        // Launch arguments.
        var jvm = new List<string>();
        var jvmDefaults = new List<string>();
        var game = new List<string>();
        var contextForArguments = new RuleContext { Platform = platform };
        var arguments = J.OptionalObject(version.Root, "arguments", "$.version");
        if (arguments is not null)
        {
            var argumentsPath = J.Field("$.version", "arguments");
            var jvmNode = J.OptionalArray(arguments.Value, "jvm", argumentsPath);
            if (jvmNode is not null) jvm.AddRange(Arguments.Evaluate(jvmNode.Value, contextForArguments, J.Field(argumentsPath, "jvm")));
            var defaultsNode = J.OptionalArray(arguments.Value, "default-user-jvm", argumentsPath);
            if (defaultsNode is not null) jvmDefaults.AddRange(Arguments.Evaluate(defaultsNode.Value, contextForArguments, J.Field(argumentsPath, "default-user-jvm")));
            var gameNode = J.OptionalArray(arguments.Value, "game", argumentsPath);
            if (gameNode is not null) game.AddRange(Arguments.Evaluate(gameNode.Value, contextForArguments, J.Field(argumentsPath, "game")));
        }
        else
        {
            var legacy = J.OptionalString(version.Root, "minecraftArguments", "$.version");
            if (legacy is not null)
                game.AddRange(CommandLine.Split(legacy, "$.version.minecraftArguments"));
        }

        if (jvm.Count == 0)
        {
            if (hasNatives) jvm.Add("-Djava.library.path=${nativesDir}");
            jvm.Add("-cp");
            jvm.Add("${classpath}");
        }

        jvm = jvm.Select(token => ApplyToken(token, version, platform, "$.version.arguments.jvm")).ToList();
        jvmDefaults = jvmDefaults.Select(token => ApplyToken(token, version, platform, "$.version.arguments.default-user-jvm")).ToList();
        game = game.Select(token => ApplyToken(token, version, platform, "$.version.arguments.game")).ToList();

        var classpath = new List<string> { clientPath };
        foreach (var file in files)
            if (file.Role == "library") classpath.Add(file.Path);

        var total = new TotalBytes();
        foreach (var file in files) total.Add(file.Size);
        total.Add(assets.TotalBytes);

        var revision = Revision(version.Bytes, platform.Token, index.Sha1, "client");
        var plan = new ClientPlan(
            RelaySchema.Current,
            "client",
            version.Id,
            key,
            version.Type,
            version.ReleaseTime,
            platform.Token,
            revision,
            new SourceInfo(version.VersionSha1, version.ManifestSha1, index.Sha1, RelaySchema.ResolverVersion),
            version.Java,
            new AssetsRef(version.AssetIndex.Id, assets.Mode, assets.Root, $"{RelaySchema.Prefix}/assets/{version.AssetIndex.Id}.json", assets.TotalBytes),
            total.Value,
            new LaunchInfo(version.MainClass!, jvm, jvmDefaults, game, logging),
            classpath,
            files,
            new NativesInfo("natives"),
            ["natives"]);

        return new ClientResult(plan, assets, warnings);
    }

    public static ServerPlan ResolveServer(ParsedVersion version, string key)
    {
        if (version.Server is null)
            throw new RelayException(RelayErrorCode.MissingField, "$.version.downloads.server", "server download is missing");

        var path = $"versions/{version.Id}/server.jar";
        var file = new PlanFile("server", SafeRelativePath.Parse(path, "server.path").Value,
            version.Server.Sha1, version.Server.Size, [version.Server.Url]);
        var total = new TotalBytes();
        total.Add(file.Size);

        var revision = Revision(version.Bytes, "server", null, "server");
        return new ServerPlan(
            RelaySchema.Current,
            "server",
            version.Id,
            key,
            version.Type,
            version.ReleaseTime,
            revision,
            new SourceInfo(version.VersionSha1, version.ManifestSha1, null, RelaySchema.ResolverVersion),
            version.Java,
            total.Value,
            [file],
            []);
    }

    public static AssetsDocument BuildAssets(ParsedIndex index, string indexId)
    {
        var root = J.Object(index.Root, "$.index");
        var objectsNode = J.Required(root, "objects", "$.index");
        if (objectsNode.ValueKind != JsonValueKind.Object)
            throw new RelayException(RelayErrorCode.WrongType, "$.index.objects", "expected a JSON object");

        var virtualMode = J.OptionalBool(root, "virtual", "$.index") == true;
        var resourcesMode = J.OptionalBool(root, "map_to_resources", "$.index") == true;
        if (virtualMode && resourcesMode)
            throw new RelayException(RelayErrorCode.UnsupportedValue, "$.index", "virtual and map_to_resources cannot both be true");

        var mode = resourcesMode ? "resources" : virtualMode ? "virtual" : "objects";
        var assetRoot = resourcesMode ? "resources" : virtualMode ? $"assets/virtual/{indexId}" : "assets";

        var objects = new List<AssetObject>();
        var links = new List<AssetLink>();
        var objectByPath = new Dictionary<string, AssetObject>(StringComparer.Ordinal);
        var linkByTo = new Dictionary<string, AssetLink>(StringComparer.Ordinal);
        var total = new TotalBytes();
        var count = 0;

        foreach (var property in objectsNode.EnumerateObject())
        {
            if (++count > MaxAssetObjects)
                throw new RelayException(RelayErrorCode.TooLarge, "$.index.objects", "too many asset objects");

            var name = property.Name;
            var objectPath = "$.index.objects." + name;
            J.Object(property.Value, objectPath);
            var hash = J.RequiredSha1(property.Value, "hash", objectPath);
            var size = J.RequiredSize(property.Value, "size", objectPath);

            var path = $"assets/objects/{hash[..2]}/{hash}";
            var urls = new[] { $"https://resources.download.minecraft.net/{hash[..2]}/{hash}" };
            var asset = new AssetObject(path, hash, size, urls);

            if (objectByPath.TryGetValue(path, out var existing))
            {
                if (!string.Equals(existing.Sha1, hash, StringComparison.Ordinal) || existing.Size != size)
                    throw new RelayException(RelayErrorCode.HashSizeConflict, path, "asset objects conflict for the same path");
            }
            else
            {
                objectByPath.Add(path, asset);
                objects.Add(asset);
                total.Add(size);
            }

            if (mode != "objects")
            {
                var safeName = SafeRelativePath.Parse(name, objectPath).Value;
                var target = SafeRelativePath.Parse($"{assetRoot}/{safeName}", objectPath).Value;
                if (linkByTo.TryGetValue(target, out var existingLink))
                {
                    if (!string.Equals(existingLink.From, path, StringComparison.Ordinal))
                        throw new RelayException(RelayErrorCode.LinkConflict, target, "two asset objects map to the same link target");
                }
                else
                {
                    var link = new AssetLink(path, target);
                    linkByTo.Add(target, link);
                    links.Add(link);
                }
            }
        }

        objects.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        links.Sort((left, right) => string.CompareOrdinal(left.To, right.To));

        return new AssetsDocument(
            RelaySchema.Current,
            indexId,
            index.Sha1,
            mode,
            assetRoot,
            total.Value,
            objects,
            links);
    }

    private static DownloadRef ParseDownloadRef(JsonElement element, string path)
    {
        J.Object(element, path);
        var sha1 = J.RequiredSha1(element, "sha1", path);
        var size = J.RequiredSize(element, "size", path);
        var url = J.RequiredUrl(element, "url", path);
        return new DownloadRef(sha1, size, url);
    }

    private static string LibraryPath(string name, string? classifier = null)
    {
        var parts = name.Split(':');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            throw new RelayException(RelayErrorCode.UnsupportedValue, "$.version.libraries.name", "invalid Maven coordinate");

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var actualClassifier = classifier ?? (parts.Length > 3 ? parts[3] : null);
        var file = actualClassifier is null
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{actualClassifier}.jar";
        return $"libraries/{group}/{artifact}/{version}/{file}";
    }

    private static ExtractRule? ExtractOf(JsonElement library, string path)
    {
        var extract = J.OptionalObject(library, "extract", path);
        if (extract is null) return null;
        var extractPath = J.Field(path, "extract");
        var excludeNode = J.OptionalArray(extract.Value, "exclude", extractPath);
        if (excludeNode is null) return new ExtractRule([]);

        var excludes = new List<string>();
        var index = 0;
        foreach (var entry in excludeNode.Value.EnumerateArray())
        {
            var entryPath = J.Index(J.Field(extractPath, "exclude"), index++);
            if (entry.ValueKind != JsonValueKind.String)
                throw new RelayException(RelayErrorCode.WrongType, entryPath, "extract.exclude must contain strings");
            var value = entry.GetString()!;
            if (string.IsNullOrEmpty(value) || value[0] is '/' or '\\' || value.Contains('\\'))
                throw new RelayException(RelayErrorCode.UnsafePath, entryPath, "unsafe extract exclude");
            if (value.Split('/').Any(segment => segment == ".."))
                throw new RelayException(RelayErrorCode.UnsafePath, entryPath, "unsafe extract exclude");
            excludes.Add(value);
        }

        return new ExtractRule(excludes);
    }

    private static string ApplyToken(string token, ParsedVersion version, Platform platform, string path)
    {
        var result = Placeholders.Apply(token, version.Id, version.Type, version.AssetIndex?.Id ?? "legacy", platform);
        var unknown = Placeholders.FindUnknown(result);
        if (unknown.Count > 0)
            throw new RelayException(RelayErrorCode.UnsupportedValue, path, "unknown launch placeholder");
        return result;
    }

    private static void EnsureNoControl(string value, string path)
    {
        if (value.Any(c => char.IsControl(c) || char.IsSurrogate(c)))
            throw new RelayException(RelayErrorCode.UnsupportedValue, path, "value contains a control character");
    }

    private static string Revision(byte[] versionBytes, string target, string? indexSha1, string kind)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(versionBytes);
        hash.AppendData(Encoding.UTF8.GetBytes($"|{target}|{indexSha1 ?? "-"}|{kind}|resolver={RelaySchema.ResolverVersion}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal struct TotalBytes
{
    private long _value;

    public readonly long Value => _value;

    public void Add(long size)
    {
        try
        {
            _value = checked(_value + size);
        }
        catch (OverflowException)
        {
            throw new RelayException(RelayErrorCode.Overflow, "$.totalBytes", "total byte count overflow");
        }

        if (_value > MojangResolver.MaxTotalBytes)
            throw new RelayException(RelayErrorCode.TooLarge, "$.totalBytes", "plan exceeds the total byte limit");
    }
}

