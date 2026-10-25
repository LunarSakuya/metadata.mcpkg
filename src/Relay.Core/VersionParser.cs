using System.Globalization;
using System.Text.Json;

namespace Relay.Core;

public sealed record DownloadRef(string Sha1, long Size, string Url);

public sealed record AssetIndexRef(
    string Id,
    string Sha1,
    long Size,
    string Url,
    long? TotalSize);

public sealed record ParsedVersion(
    string Id,
    string Type,
    string ReleaseTime,
    string? MainClass,
    JavaInfo Java,
    string VersionSha1,
    string? ManifestSha1,
    byte[] Bytes,
    JsonElement Root,
    AssetIndexRef? AssetIndex,
    DownloadRef? Client,
    DownloadRef? Server,
    string? LoggingId,
    string? LoggingArgument,
    DownloadRef? LoggingFile);

public sealed record ParsedIndex(
    string Sha1,
    byte[] Bytes,
    JsonElement Root);

/// <summary>
/// Strict parser for one Mojang version document and one asset index. The
/// parser owns all Mojang vocabulary and returns an immutable, validated
/// snapshot; it never returns a JsonElement to the resolver's public output.
/// </summary>
public static class VersionParser
{
    public const int MaxVersionBytes = 64 * 1024 * 1024;
    public const int MaxIndexBytes = 64 * 1024 * 1024;

    public static ParsedVersion Parse(byte[] bytes, string id, ManifestEntry? manifest = null)
    {
        var safeId = SafeVersionId.Parse(id, "$.version.id").Value;
        var versionPath = "$.version";
        using var document = JsonRead.Parse(bytes, versionPath, MaxVersionBytes);
        var root = J.Object(document.RootElement, versionPath);

        var embeddedId = J.OptionalString(root, "id", versionPath);
        if (embeddedId is not null && !string.Equals(embeddedId, safeId, StringComparison.Ordinal))
            throw new RelayException(RelayErrorCode.UnsupportedValue, J.Field(versionPath, "id"), "version id does not match the requested id");

        if (root.TryGetProperty("inheritsFrom", out _))
            throw new RelayException(RelayErrorCode.UnsupportedValue, J.Field(versionPath, "inheritsFrom"), "inheritsFrom is not supported; loader metadata must be merged first");

        var type = J.OptionalString(root, "type", versionPath)
                   ?? manifest?.Type
                   ?? "release";
        var releaseTime = NormalizeTime(
            J.OptionalString(root, "releaseTime", versionPath) ?? manifest?.ReleaseTime,
            J.Field(versionPath, "releaseTime"));

        var mainClass = J.OptionalString(root, "mainClass", versionPath);

        DownloadRef? client = null;
        DownloadRef? server = null;
        var downloads = J.OptionalObject(root, "downloads", versionPath);
        if (downloads is not null)
        {
            var downloadsPath = J.Field(versionPath, "downloads");
            var clientNode = J.OptionalObject(downloads.Value, "client", downloadsPath);
            if (clientNode is not null)
                client = ParseDownload(clientNode.Value, J.Field(downloadsPath, "client"));

            var serverNode = J.OptionalObject(downloads.Value, "server", downloadsPath);
            if (serverNode is not null)
                server = ParseDownload(serverNode.Value, J.Field(downloadsPath, "server"));
        }

        AssetIndexRef? assetIndex = null;
        var assetIndexNode = J.OptionalObject(root, "assetIndex", versionPath);
        if (assetIndexNode is not null)
            assetIndex = ParseAssetIndex(assetIndexNode.Value, J.Field(versionPath, "assetIndex"));

        string? loggingId = null;
        string? loggingArgument = null;
        DownloadRef? loggingFile = null;
        var logging = J.OptionalObject(root, "logging", versionPath);
        if (logging is not null)
        {
            var loggingPath = J.Field(versionPath, "logging");
            var loggingClient = J.OptionalObject(logging.Value, "client", loggingPath);
            if (loggingClient is not null)
            {
                var clientPath = J.Field(loggingPath, "client");
                var file = J.RequiredObject(loggingClient.Value, "file", clientPath);
                loggingId = SafeVersionId.Parse(J.RequiredString(file, "id", J.Field(clientPath, "file")), J.Field(clientPath, "file.id")).Value;
                loggingArgument = J.OptionalString(loggingClient.Value, "argument", clientPath);
                loggingFile = ParseDownload(file, J.Field(clientPath, "file"));
            }
        }

        JavaInfo java;
        var javaVersion = J.OptionalObject(root, "javaVersion", versionPath);
        if (javaVersion is null)
        {
            java = new JavaInfo(8, "jre-legacy", true);
        }
        else
        {
            var javaPath = J.Field(versionPath, "javaVersion");
            var major = J.RequiredInt(javaVersion.Value, "majorVersion", javaPath);
            var component = J.OptionalString(javaVersion.Value, "component", javaPath);
            java = new JavaInfo(major, component ?? "jre-legacy", component is null);
        }

        var clone = root.Clone();
        var versionSha1 = MetadataAudit.Sha1Hex(bytes);
        return new ParsedVersion(
            safeId,
            type,
            releaseTime,
            mainClass,
            java,
            versionSha1,
            manifest?.Sha1,
            bytes,
            clone,
            assetIndex,
            client,
            server,
            loggingId,
            loggingArgument,
            loggingFile);
    }

    public static ParsedIndex ParseIndex(byte[] bytes, string expectedSha1)
    {
        if (bytes.Length > MaxIndexBytes)
            throw new RelayException(RelayErrorCode.TooLarge, "$.index", $"asset index exceeds {MaxIndexBytes} bytes");

        var actual = MetadataAudit.Sha1Hex(bytes);
        if (!string.Equals(actual, expectedSha1, StringComparison.Ordinal))
            throw new RelayException(RelayErrorCode.BadSha1, "$.index", "asset index SHA-1 mismatch");

        using var document = JsonRead.Parse(bytes, "$.index", MaxIndexBytes);
        var root = J.Object(document.RootElement, "$.index");
        var objects = J.Required(root, "objects", "$.index");
        if (objects.ValueKind != JsonValueKind.Object)
            throw new RelayException(RelayErrorCode.WrongType, "$.index.objects", "expected a JSON object");

        var virtualFlag = J.OptionalBool(root, "virtual", "$.index");
        var resourcesFlag = J.OptionalBool(root, "map_to_resources", "$.index");
        if (virtualFlag == true && resourcesFlag == true)
            throw new RelayException(RelayErrorCode.UnsupportedValue, "$.index", "virtual and map_to_resources cannot both be true");

        return new ParsedIndex(expectedSha1, bytes, root.Clone());
    }

    private static DownloadRef ParseDownload(JsonElement element, string path)
    {
        J.Object(element, path);
        var sha1 = J.RequiredSha1(element, "sha1", path);
        var size = J.RequiredSize(element, "size", path);
        var url = J.RequiredUrl(element, "url", path);
        return new DownloadRef(sha1, size, url);
    }

    private static AssetIndexRef ParseAssetIndex(JsonElement element, string path)
    {
        J.Object(element, path);
        var id = SafeVersionId.Parse(J.RequiredString(element, "id", path), J.Field(path, "id")).Value;
        var sha1 = J.RequiredSha1(element, "sha1", path);
        var size = J.RequiredSize(element, "size", path);
        var url = J.RequiredUrl(element, "url", path);
        var totalSize = J.OptionalLong(element, "totalSize", path);
        return new AssetIndexRef(id, sha1, size, url, totalSize);
    }

    private static string NormalizeTime(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) return "1970-01-01T00:00:00Z";

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            throw new RelayException(RelayErrorCode.UnsupportedValue, path, "invalid releaseTime");

        return parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}

