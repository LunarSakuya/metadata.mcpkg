using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relay.Core;

return Run(args);

static int Run(string[] args)
{
    try
    {
        var options = Parse(args);
        var meta = Require(options, "meta");
        var indexes = Require(options, "indexes");
        var output = Require(options, "out");
        var manifestPath = Require(options, "manifest");
        var platformTokens = (options.GetValueOrDefault("platforms") ?? string.Join(',', Platform.All))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var platforms = platformTokens.Select(Platform.Parse).DistinctBy(p => p.Token).ToList();
        if (platforms.Count == 0)
            throw new RelayException(RelayErrorCode.UnsupportedValue, "$.platforms", "no platforms selected");

        var outputDir = Path.GetFullPath(output);
        if (Directory.Exists(outputDir) || File.Exists(outputDir))
            throw new RelayException(RelayErrorCode.UnsafePath, "$.out", "output must not already exist");
        Directory.CreateDirectory(outputDir);

        var manifestEntries = MetadataAudit.LoadManifest(manifestPath);
        var allIds = Directory.GetFiles(meta, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        foreach (var id in allIds)
            SafeVersionId.Parse(id, "$.meta.id");
        var keys = BuildKeys(allIds);

        var selectedIds = options.GetValueOrDefault("versions") is { Length: > 0 } selection && selection != "all"
            ? selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : allIds;
        foreach (var id in selectedIds)
            if (!keys.ContainsKey(id))
                throw new RelayException(RelayErrorCode.MissingField, "$.versions", $"unknown version id: {Text.Sanitize(id)}");

        var dbTemp = Path.Combine(outputDir, "relay-index.full." + Guid.NewGuid().ToString("N") + ".idx");
        DeleteFileIfExists(dbTemp);
        DeleteFileIfExists(dbTemp + "-journal");

        var assetsById = new Dictionary<string, AssetsDocument>(StringComparer.Ordinal);
        var indexCache = new Dictionary<string, ParsedIndex>(StringComparer.Ordinal);
        var versions = new List<VersionRow>();
        var generatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var manifestSha256 = FileSha256(manifestPath);
        var clientPlans = 0;
        var serverPlans = 0;
        var warnings = 0;
        var planId = 0;
        var contentId = 0;
        var contentIds = new Dictionary<string, int>(StringComparer.Ordinal);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbTemp,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            Execute(connection, "PRAGMA page_size = 4096;");
            Execute(connection, "PRAGMA auto_vacuum = FULL;");
            Execute(connection, "PRAGMA journal_mode = DELETE;");
            Execute(connection, "PRAGMA synchronous = FULL;");
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "PRAGMA busy_timeout = 10000;");
            Execute(connection, "PRAGMA application_id = 1296253511;");
            Execute(connection, "PRAGMA user_version = 1;");
            Execute(connection, ReadSchema("schema.sql"));

            using var transaction = connection.BeginTransaction();

            var insertVersion = Command(connection, transaction, """
                INSERT INTO versions
                    (id, key, type, release_time, java_major, java_defaulted, assets_index,
                     server_available, revision, source_manifest_sha1, schema_version)
                VALUES ($id, $key, $type, $release, $java, $defaulted, $assets,
                        $server, $revision, $manifest, 2);
                """);
            var insertPlan = Command(connection, transaction, """
                INSERT INTO plans
                    (id, version_id, kind, platform, revision, total_bytes, main_class,
                     assets_index, assets_mode, assets_root, assets_document, assets_sha1,
                     assets_size, logging_argument, logging_path, native_dir)
                VALUES ($id, $version, $kind, $platform, $revision, $total, $main,
                        $assets, $mode, $root, $document, $sha, $size,
                        $logArg, $logPath, $native);
                """);
            var insertContent = Command(connection, transaction, """
                INSERT INTO contents (id, sha1, size) VALUES ($id, $sha1, $size);
                """);
            var insertContentUrl = Command(connection, transaction, """
                INSERT INTO content_urls (content_id, ordinal, url) VALUES ($content, $ordinal, $url);
                """);
            var insertFile = Command(connection, transaction, """
                INSERT INTO files (plan_id, role, path, content_id)
                VALUES ($plan, $role, $path, $content);
                """);
            var insertClasspath = Command(connection, transaction, """
                INSERT INTO plan_classpath (plan_id, ordinal, path) VALUES ($plan, $ordinal, $path);
                """);
            var insertDirectory = Command(connection, transaction, """
                INSERT INTO plan_directories (plan_id, ordinal, path) VALUES ($plan, $ordinal, $path);
                """);
            var insertArg = Command(connection, transaction, """
                INSERT INTO launch_args (plan_id, phase, ordinal, value) VALUES ($plan, $phase, $ordinal, $value);
                """);
            var insertNative = Command(connection, transaction, """
                INSERT INTO natives (plan_id, dir) VALUES ($plan, $dir);
                """);
            var insertExclude = Command(connection, transaction, """
                INSERT OR IGNORE INTO extract_exclude (plan_id, path) VALUES ($plan, $path);
                """);
            var insertAssetDoc = Command(connection, transaction, """
                INSERT INTO asset_docs (index_id, mode, root, sha1, total_bytes, revision, schema_version)
                VALUES ($id, $mode, $root, $sha1, $total, $revision, 2);
                """);
            var insertAssetObject = Command(connection, transaction, """
                INSERT OR IGNORE INTO asset_objects (doc_id, sha1, size) VALUES ($doc, $sha1, $size);
                """);
            var insertAssetLink = Command(connection, transaction, """
                INSERT INTO asset_links (doc_id, ordinal, from_path, to_path) VALUES ($doc, $ordinal, $from, $to);
                """);

            foreach (var id in selectedIds)
            {
                if (!manifestEntries.TryGetValue(id, out var manifestEntry))
                    throw new RelayException(RelayErrorCode.MissingField, "$.manifest.versions", $"no manifest entry for version {Text.Sanitize(id)}");

                var versionPath = Path.Combine(meta, id + ".json");
                var versionBytes = ReadFileLimited(versionPath, VersionParser.MaxVersionBytes, "$.version");
                MetadataAudit.VerifyBytes(versionBytes, manifestEntry.Sha1, "$.version.sha1");
                var parsed = VersionParser.Parse(versionBytes, id, manifestEntry);
                var key = keys[id];

                AssetsDocument? assets = null;
                ParsedIndex? index = null;
                if (parsed.Client is not null && parsed.AssetIndex is not null)
                {
                    index = LoadIndex(indexes, parsed.AssetIndex.Sha1, indexCache);
                    assets = MojangResolver.BuildAssets(index, parsed.AssetIndex.Id);
                    if (assetsById.TryGetValue(assets.Index, out var existing))
                    {
                        if (!string.Equals(existing.Sha1, assets.Sha1, StringComparison.Ordinal))
                            throw new RelayException(RelayErrorCode.IndexMismatch, "$.assetIndex.id",
                                "the same asset index id has different contents");
                    }
                    else
                    {
                        assetsById.Add(assets.Index, assets);
                        Bind(insertAssetDoc,
                            ("$id", assets.Index), ("$mode", assets.Mode), ("$root", assets.Root),
                            ("$sha1", assets.Sha1), ("$total", assets.TotalBytes),
                            ("$revision", HashText($"asset|{assets.Index}|{assets.Sha1}|{assets.Mode}|{assets.TotalBytes}")));
                        insertAssetDoc.ExecuteNonQuery();
                        foreach (var obj in assets.Objects)
                        {
                            Bind(insertAssetObject, ("$doc", assets.Index), ("$sha1", obj.Sha1), ("$size", obj.Size));
                            insertAssetObject.ExecuteNonQuery();
                        }
                        var linkOrdinal = 0;
                        foreach (var link in assets.Links)
                        {
                            Bind(insertAssetLink, ("$doc", assets.Index), ("$ordinal", linkOrdinal++),
                                ("$from", link.From), ("$to", link.To));
                            insertAssetLink.ExecuteNonQuery();
                        }
                    }
                }
                else if (parsed.Client is not null)
                {
                    throw new RelayException(RelayErrorCode.MissingField, "$.version.assetIndex", "client exists without an asset index");
                }

                var versionRevision = HashText($"version|{parsed.Id}|{parsed.VersionSha1}|{parsed.ManifestSha1}");
                Bind(insertVersion,
                    ("$id", parsed.Id), ("$key", key), ("$type", parsed.Type), ("$release", parsed.ReleaseTime),
                    ("$java", parsed.Java.Major), ("$defaulted", parsed.Java.Defaulted ? 1 : 0),
                    ("$assets", (object?)parsed.AssetIndex?.Id ?? DBNull.Value),
                    ("$server", parsed.Server is not null ? 1 : 0), ("$revision", versionRevision),
                    ("$manifest", (object?)parsed.ManifestSha1 ?? manifestEntry.Sha1));
                insertVersion.ExecuteNonQuery();

                var versionPlatforms = new List<string>();
                if (assets is not null && index is not null)
                {
                    foreach (var platform in platforms)
                    {
                        var result = MojangResolver.ResolveClient(parsed, index, platform, key, assets);
                        InsertClientPlan(insertPlan, ref planId, insertFile, insertContent, insertContentUrl,
                            insertExclude, insertNative, insertClasspath, insertDirectory, insertArg,
                            contentIds, ref contentId, result.Plan, assets.Sha1);
                        clientPlans++;
                        versionPlatforms.Add(platform.Token);
                        foreach (var warning in result.Warnings)
                        {
                            warnings++;
                            Console.Error.WriteLine($"warn {Text.Sanitize(id)}/{platform.Token}: {Text.Sanitize(warning)}");
                        }
                    }
                }

                if (parsed.Server is not null)
                {
                    var plan = MojangResolver.ResolveServer(parsed, key);
                    InsertServerPlan(insertPlan, ref planId, insertFile, insertContent, insertContentUrl,
                        insertExclude, insertNative, insertClasspath, insertDirectory, insertArg,
                        contentIds, ref contentId, plan);
                    serverPlans++;
                }

                if (parsed.Client is null && parsed.Server is null)
                    throw new RelayException(RelayErrorCode.MissingField, "$.version.downloads", "version has neither client nor server download");

                versions.Add(new VersionRow(parsed.Id, key, parsed.Type, parsed.ReleaseTime, parsed.Java.Major,
                    parsed.Java.Defaulted, parsed.AssetIndex?.Id, parsed.Server is not null, versionRevision,
                    versionPlatforms));
            }

            var sorted = versions
                .OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList();
            var latestRelease = sorted.FirstOrDefault(v => v.Type == "release")?.Id
                                ?? sorted.FirstOrDefault()?.Id
                                ?? string.Empty;
            var latestSnapshot = sorted.FirstOrDefault(v => v.Type == "snapshot")?.Id ?? string.Empty;

            var metaRows = new (string Key, string Value)[]
            {
                ("schema", "2"), ("format", "sqlite"), ("database_schema", "1"),
                ("resolver", "1"),
                ("latest_release", latestRelease), ("latest_snapshot", latestSnapshot),
                ("source_manifest_sha256", manifestSha256),
                ("count_versions", versions.Count.ToString()), ("count_client_plans", clientPlans.ToString()),
                ("count_server_plans", serverPlans.ToString()), ("count_asset_docs", assetsById.Count.ToString()),
                ("count_warnings", warnings.ToString()),
            };
            var insertMeta = Command(connection, transaction, "INSERT INTO meta (key, value) VALUES ($key, $value);");
            foreach (var row in metaRows)
            {
                Bind(insertMeta, ("$key", row.Key), ("$value", row.Value));
                insertMeta.ExecuteNonQuery();
            }
            var insertPlatform = Command(connection, transaction, "INSERT INTO platforms (ordinal, token) VALUES ($ordinal, $token);");
            for (var i = 0; i < platforms.Count; i++)
            {
                Bind(insertPlatform, ("$ordinal", i), ("$token", platforms[i].Token));
                insertPlatform.ExecuteNonQuery();
            }

            transaction.Commit();
            foreach (var command in new[]
                     {
                         insertVersion, insertPlan, insertContent, insertContentUrl, insertFile, insertNative,
                         insertExclude, insertClasspath, insertDirectory, insertArg, insertAssetDoc, insertAssetObject, insertAssetLink, insertMeta, insertPlatform,
                     })
            {
                command.Dispose();
            }
        }

        SqliteConnection.ClearAllPools();
        FlushFile(dbTemp);
        var shards = SplitDatabase(dbTemp, outputDir);
        File.Delete(dbTemp);
        FlushDirectory(outputDir);

        var manifest = new IndexManifest(
            Schema: 2,
            Format: "sqlite-shards",
            GeneratedAt: generatedAt,
            LatestRelease: versions.OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal).FirstOrDefault(v => v.Type == "release")?.Id ?? string.Empty,
            LatestSnapshot: versions.OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal).FirstOrDefault(v => v.Type == "snapshot")?.Id ?? string.Empty,
            Shards: shards,
            Counts: new IndexCounts(versions.Count, clientPlans, serverPlans, assetsById.Count),
            SourceManifestSha256: manifestSha256);
        var manifestText = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        var indexPath = Path.Combine(outputDir, "index.json");
        File.WriteAllText(indexPath, manifestText + "\n", new UTF8Encoding(false));
        FlushFile(indexPath);

        Console.WriteLine($"versions={versions.Count} clientPlans={clientPlans} serverPlans={serverPlans} assets={assetsById.Count} warnings={warnings}");
        foreach (var shard in shards)
            Console.WriteLine($"shard={shard.Id} path={shard.Path} bytes={shard.Size} sha256={shard.Sha256}");
        Console.WriteLine($"output={outputDir}");
        return 0;
    }
    catch (RelayException error)
    {
        Console.Error.WriteLine($"ERROR {error.Code} at {Text.Sanitize(error.Path)}: {Text.Sanitize(error.Message)}");
        return 1;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"ERROR: {Text.Sanitize(error.Message)}");
        return 1;
    }
}

static void InsertClientPlan(SqliteCommand insertPlan, ref int planId,
    SqliteCommand insertFile, SqliteCommand insertContent, SqliteCommand insertContentUrl,
    SqliteCommand insertExclude, SqliteCommand insertNative,
    SqliteCommand insertClasspath, SqliteCommand insertDirectory, SqliteCommand insertArg,
    Dictionary<string, int> contentIds, ref int contentId, ClientPlan plan, string assetsSha1)
{
    planId++;
    Bind(insertPlan,
        ("$id", planId), ("$version", plan.Id), ("$kind", plan.Kind), ("$platform", plan.Platform),
        ("$revision", plan.Revision), ("$total", plan.TotalBytes), ("$main", plan.Launch.MainClass),
        ("$assets", plan.Assets.Index), ("$mode", plan.Assets.Mode), ("$root", plan.Assets.Root),
        ("$document", plan.Assets.Document), ("$sha", assetsSha1), ("$size", plan.Assets.TotalBytes),
        ("$logArg", plan.Launch.Logging?.Argument ?? (object?)DBNull.Value),
        ("$logPath", plan.Launch.Logging?.Path ?? (object?)DBNull.Value),
        ("$native", plan.Natives.Dir));
    insertPlan.ExecuteNonQuery();
    InsertFiles(planId, insertFile, insertContent, insertContentUrl, insertExclude, contentIds, ref contentId, plan.Files);
    InsertOrderedStrings(insertClasspath, planId, plan.Classpath, "$path");
    InsertOrderedStrings(insertDirectory, planId, plan.Directories, "$path");
    InsertArgs(insertArg, planId, "jvm", plan.Launch.Jvm);
    InsertArgs(insertArg, planId, "jvm_defaults", plan.Launch.JvmDefaults);
    InsertArgs(insertArg, planId, "game", plan.Launch.Game);
    Bind(insertNative, ("$plan", planId), ("$dir", plan.Natives.Dir));
    insertNative.ExecuteNonQuery();
}

static void InsertServerPlan(SqliteCommand insertPlan, ref int planId,
    SqliteCommand insertFile, SqliteCommand insertContent, SqliteCommand insertContentUrl,
    SqliteCommand insertExclude, SqliteCommand insertNative,
    SqliteCommand insertClasspath, SqliteCommand insertDirectory, SqliteCommand insertArg,
    Dictionary<string, int> contentIds, ref int contentId, ServerPlan plan)
{
    planId++;
    Bind(insertPlan,
        ("$id", planId), ("$version", plan.Id), ("$kind", plan.Kind), ("$platform", ""),
        ("$revision", plan.Revision), ("$total", plan.TotalBytes), ("$main", DBNull.Value),
        ("$assets", DBNull.Value), ("$mode", DBNull.Value), ("$root", DBNull.Value), ("$document", DBNull.Value),
        ("$sha", DBNull.Value), ("$size", DBNull.Value), ("$logArg", DBNull.Value), ("$logPath", DBNull.Value),
        ("$native", DBNull.Value));
    insertPlan.ExecuteNonQuery();
    InsertFiles(planId, insertFile, insertContent, insertContentUrl, insertExclude, contentIds, ref contentId, plan.Files);
    InsertOrderedStrings(insertDirectory, planId, plan.Directories, "$path");
}

static void InsertOrderedStrings(SqliteCommand command, int planId, IReadOnlyList<string> values, string valueParameter)
{
    for (var i = 0; i < values.Count; i++)
    {
        Bind(command, ("$plan", planId), ("$ordinal", i), (valueParameter, values[i]));
        command.ExecuteNonQuery();
    }
}

static void InsertArgs(SqliteCommand command, int planId, string phase, IReadOnlyList<string> values)
{
    for (var i = 0; i < values.Count; i++)
    {
        Bind(command, ("$plan", planId), ("$phase", phase), ("$ordinal", i), ("$value", values[i]));
        command.ExecuteNonQuery();
    }
}

static void InsertFiles(int planId, SqliteCommand insertFile, SqliteCommand insertContent,
    SqliteCommand insertContentUrl, SqliteCommand insertExclude, Dictionary<string, int> contentIds,
    ref int contentId, IReadOnlyList<PlanFile> files)
{
    foreach (var file in files)
    {
        if (!contentIds.TryGetValue(file.Sha1, out var cid))
        {
            cid = ++contentId;
            Bind(insertContent, ("$id", cid), ("$sha1", file.Sha1), ("$size", file.Size));
            insertContent.ExecuteNonQuery();
            for (var i = 0; i < file.Urls.Count; i++)
            {
                Bind(insertContentUrl, ("$content", cid), ("$ordinal", i), ("$url", file.Urls[i]));
                insertContentUrl.ExecuteNonQuery();
            }
            contentIds.Add(file.Sha1, cid);
        }
        Bind(insertFile, ("$plan", planId), ("$role", file.Role), ("$path", file.Path), ("$content", cid));
        insertFile.ExecuteNonQuery();
        if (file.Extract is not null)
        {
            foreach (var exclude in file.Extract.Exclude)
            {
                Bind(insertExclude, ("$plan", planId), ("$path", exclude));
                insertExclude.ExecuteNonQuery();
            }
        }
    }
}

static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
{
    var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    return command;
}

static void Bind(SqliteCommand command, params (string Name, object? Value)[] values)
{
    command.Parameters.Clear();
    foreach (var (name, value) in values)
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static string ReadSchema(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Relay.Db." + name)
                       ?? throw new InvalidOperationException(name + " not embedded");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static List<ShardInfo> SplitDatabase(string fullPath, string outputDir)
{
    var specs = new (string Id, string Schema, string[] Tables)[]
    {
        ("catalog", "schema-catalog.sql", new[]
        {
            "meta", "platforms", "versions", "plans", "contents", "content_urls",
            "files", "natives", "extract_exclude",
        }),
        ("launch", "schema-launch.sql", new[]
        {
            "meta", "plan_classpath", "plan_directories", "launch_args",
        }),
        ("assets", "schema-assets.sql", new[]
        {
            "meta", "asset_docs", "asset_objects", "asset_links",
        }),
    };

    var result = new List<ShardInfo>();
    var escapedFull = fullPath.Replace("'", "''");
    foreach (var spec in specs)
    {
        var finalPath = Path.Combine(outputDir, spec.Id + ".idx");
        var tempPath = finalPath + ".building";
        DeleteFileIfExists(tempPath);
        DeleteFileIfExists(tempPath + "-journal");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = tempPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            Execute(connection, "PRAGMA page_size = 4096;");
            Execute(connection, "PRAGMA auto_vacuum = FULL;");
            Execute(connection, "PRAGMA journal_mode = DELETE;");
            Execute(connection, "PRAGMA synchronous = FULL;");
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "PRAGMA busy_timeout = 10000;");
            Execute(connection, "PRAGMA application_id = 1296253511;");
            Execute(connection, "PRAGMA user_version = 1;");
            Execute(connection, ReadSchema(spec.Schema));
            Execute(connection, $"ATTACH DATABASE '{escapedFull}' AS full;");

            using var transaction = connection.BeginTransaction();
            foreach (var table in spec.Tables)
            {
                using var copy = connection.CreateCommand();
                copy.Transaction = transaction;
                copy.CommandText = table == "asset_objects"
                    ? "INSERT INTO main.asset_objects (doc_id, sha1, size) SELECT doc_id, sha1, size FROM full.asset_objects;"
                    : $"INSERT INTO main.{table} SELECT * FROM full.{table};";
                copy.ExecuteNonQuery();
            }
            using (var shard = connection.CreateCommand())
            {
                shard.Transaction = transaction;
                shard.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('shard', $shard);";
                shard.Parameters.AddWithValue("$shard", spec.Id);
                shard.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        SqliteConnection.ClearAllPools();
        FlushFile(tempPath);
        var size = new FileInfo(tempPath).Length;
        var sha256 = FileSha256(tempPath);
        File.Move(tempPath, finalPath, false);
        result.Add(new ShardInfo(spec.Id, spec.Id + ".idx", sha256, size, 1));
    }
    return result;
}



static byte[] ReadFileLimited(string path, int maxBytes, string label)
{
    var file = new FileInfo(path);
    if (!file.Exists)
        throw new RelayException(RelayErrorCode.MissingField, label, "file does not exist");
    if (file.Length > maxBytes)
        throw new RelayException(RelayErrorCode.TooLarge, label, $"file exceeds {maxBytes} bytes");
    return File.ReadAllBytes(path);
}

static ParsedIndex LoadIndex(string indexes, string sha1, Dictionary<string, ParsedIndex> cache)
{
    if (cache.TryGetValue(sha1, out var cached)) return cached;
    var path = Path.Combine(indexes, sha1 + ".json");
    var bytes = ReadFileLimited(path, VersionParser.MaxIndexBytes, "$.index");
    var parsed = VersionParser.ParseIndex(bytes, sha1);
    cache.Add(sha1, parsed);
    return parsed;
}

static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static string FileSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static void FlushFile(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
    stream.Flush(true);
}

static void FlushDirectory(string path)
{
    try
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
        RandomAccess.FlushToDisk(handle);
    }
    catch
    {
        // Directory fsync is platform-dependent; SQLite's synchronous=FULL and the
        // file flush above are the portable guarantees.
    }
}

static void DeleteFileIfExists(string path)
{
    if (File.Exists(path)) File.Delete(path);
}

static Dictionary<string, string> BuildKeys(IReadOnlyList<string> ids)
{
    var keys = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var id in ids) keys[id] = KeyFor(id);
    foreach (var group in keys.GroupBy(pair => pair.Value, StringComparer.Ordinal).Where(group => group.Count() > 1))
    {
        foreach (var pair in group)
        {
            var suffix = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(pair.Key)))[..8];
            keys[pair.Key] = pair.Value + "-" + suffix;
        }
    }
    var duplicate = keys.GroupBy(pair => pair.Value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
    if (duplicate is not null)
        throw new RelayException(RelayErrorCode.DuplicateKey, "$.versions.key", "version key collision after suffixing");
    return keys;
}

static string KeyFor(string id)
{
    var buffer = new char[id.Length];
    for (var i = 0; i < id.Length; i++)
        buffer[i] = char.IsAsciiLetterOrDigit(id[i]) || id[i] is '.' or '_' or '-' ? id[i] : '_';
    var key = new string(buffer);
    if (key.Length == 0 || key is "." or "..") key = "v";
    return key;
}

static Dictionary<string, string?> Parse(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            throw new RelayException(RelayErrorCode.UnsupportedValue, "$.arguments", "unexpected argument");
        var key = arg[2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            result[key] = args[++i];
        else
            result[key] = null;
    }
    return result;
}

static string Require(Dictionary<string, string?> options, string name)
{
    if (!options.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
        throw new RelayException(RelayErrorCode.MissingField, "$." + name, "required option is missing");
    return value;
}

file sealed record VersionRow(string Id, string Key, string Type, string ReleaseTime, int JavaMajor,
    bool JavaDefaulted, string? AssetsIndex, bool Server, string Revision, IReadOnlyList<string> Platforms);

file sealed record ShardInfo(string Id, string Path, string Sha256, long Size, int UserVersion);
file sealed record IndexCounts(int Versions, int ClientPlans, int ServerPlans, int AssetDocs);
file sealed record IndexManifest(int Schema, string Format, string GeneratedAt, string LatestRelease,
    string LatestSnapshot, IReadOnlyList<ShardInfo> Shards, IndexCounts Counts, string SourceManifestSha256);













