using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relay.Core;

return Run(args);

static int Run(string[] args)
{
    try
    {
        var options = Parse(args);
        var catalogPath = Require(options, "catalog");
        var launchPath = Require(options, "launch");
        var assetsPath = Require(options, "assets");
        var manifestPath = Require(options, "manifest");
        foreach (var path in new[] { catalogPath, launchPath, assetsPath, manifestPath })
            if (!File.Exists(path)) throw new RelayException(RelayErrorCode.MissingField, "$", "missing file: " + path);

        var checks = 0;
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) failures.Add(message);
        }

        var manifest = JsonSerializer.Deserialize<IndexManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new RelayException(RelayErrorCode.WrongType, "$.manifest", "manifest is empty");
        Check(manifest.Schema == 2, $"manifest schema {manifest.Schema} != 2");
        Check(manifest.Format == "sqlite-shards", $"manifest format {manifest.Format} != sqlite-shards");
        Check(manifest.Shards.Count == 3, $"manifest shards {manifest.Shards.Count} != 3");
        var manifestIds = manifest.Shards.Select(shard => shard.Id).ToArray();
        Check(manifestIds.Distinct(StringComparer.Ordinal).Count() == 3, "duplicate shard id in manifest");
        Check(manifestIds.Contains("catalog") && manifestIds.Contains("launch") && manifestIds.Contains("assets"),
            "manifest shard set is not catalog/launch/assets");
        foreach (var shard in manifest.Shards)
        {
            Check(shard.Path == shard.Id + ".idx", $"{shard.Id} path is not canonical");
            Check(System.Text.RegularExpressions.Regex.IsMatch(shard.Path, "^[a-z]+\\.idx$"), $"{shard.Id} path is unsafe");
        }

        var expected = new Dictionary<string, (string Path, long Size, string Sha256)>(StringComparer.Ordinal)
        {
            ["catalog"] = (catalogPath, new FileInfo(catalogPath).Length, FileSha256(catalogPath)),
            ["launch"] = (launchPath, new FileInfo(launchPath).Length, FileSha256(launchPath)),
            ["assets"] = (assetsPath, new FileInfo(assetsPath).Length, FileSha256(assetsPath)),
        };
        foreach (var shard in manifest.Shards)
        {
            Check(expected.TryGetValue(shard.Id, out var actual), $"manifest unknown shard {shard.Id}");
            if (!expected.TryGetValue(shard.Id, out actual)) continue;
            Check(shard.Size == actual.Size, $"{shard.Id} size {shard.Size} != {actual.Size}");
            Check(shard.Sha256 == actual.Sha256, $"{shard.Id} SHA-256 mismatch");
            Check(shard.UserVersion == 1, $"{shard.Id} userVersion != 1");
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF;";
            pragma.ExecuteNonQuery();
        }
        Attach(connection, launchPath, "launch");
        Attach(connection, assetsPath, "assets");
        Check(Scalar(connection, "PRAGMA integrity_check;") == "ok", "catalog integrity_check failed");
        Check(Scalar(connection, "PRAGMA launch.integrity_check;") == "ok", "launch integrity_check failed");
        Check(Scalar(connection, "PRAGMA assets.integrity_check;") == "ok", "assets integrity_check failed");
        Check(Scalar(connection, "PRAGMA user_version;") == "1", "catalog user_version != 1");
        Check(Scalar(connection, "PRAGMA launch.user_version;") == "1", "launch user_version != 1");
        Check(Scalar(connection, "PRAGMA assets.user_version;") == "1", "assets user_version != 1");
        Check(Scalar(connection, "PRAGMA application_id;") == "1296253511", "catalog application_id mismatch");
        Check(Scalar(connection, "PRAGMA launch.application_id;") == "1296253511", "launch application_id mismatch");
        Check(Scalar(connection, "PRAGMA assets.application_id;") == "1296253511", "assets application_id mismatch");
        Check(!HasRows(connection, "PRAGMA foreign_key_check;"), "catalog foreign_key_check failed");
        Check(!HasRows(connection, "PRAGMA launch.foreign_key_check;"), "launch foreign_key_check failed");
        Check(!HasRows(connection, "PRAGMA assets.foreign_key_check;"), "assets foreign_key_check failed");

        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["versions"] = Count(connection, "main.versions"),
            ["plans"] = Count(connection, "main.plans"),
            ["client"] = CountWhere(connection, "main.plans", "kind = 'client'"),
            ["server"] = CountWhere(connection, "main.plans", "kind = 'server'"),
            ["files"] = Count(connection, "main.files"),
            ["contents"] = Count(connection, "main.contents"),
            ["content_urls"] = Count(connection, "main.content_urls"),
            ["file_urls"] = Count(connection, "main.v_file_urls"),
            ["classpath"] = Count(connection, "launch.plan_classpath"),
            ["dirs"] = Count(connection, "launch.plan_directories"),
            ["args"] = Count(connection, "launch.launch_args"),
            ["assets"] = Count(connection, "assets.asset_docs"),
            ["objects"] = Count(connection, "assets.asset_objects"),
            ["links"] = Count(connection, "assets.asset_links"),
            ["asset_urls"] = Count(connection, "assets.v_asset_objects"),
        };
        Check(counts["versions"] == manifest.Counts.Versions, "manifest versions mismatch");
        Check(counts["client"] == manifest.Counts.ClientPlans, "manifest clients mismatch");
        Check(counts["server"] == manifest.Counts.ServerPlans, "manifest servers mismatch");
        Check(counts["assets"] == manifest.Counts.AssetDocs, "manifest assets mismatch");
        Check(counts["plans"] == counts["client"] + counts["server"], "plan count mismatch");
        Check(counts["files"] > 0, "files empty");
        Check(counts["contents"] > 0, "contents empty");
        Check(counts["content_urls"] == counts["contents"], "content URL count mismatch");
        Check(counts["file_urls"] == counts["files"], "file URL row count mismatch");
        Check(counts["classpath"] > 0, "classpath empty");
        Check(counts["objects"] > 0, "asset objects empty");
        Check(counts["asset_urls"] == counts["objects"], "asset URL row count mismatch");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, sha1, size, url FROM main.v_file_urls;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                CheckSafePath(reader.GetString(0), "file", failures, ref checks);
                Check(IsSha1(reader.GetString(1)), "bad file SHA-1");
                Check(reader.GetInt64(2) >= 0, "negative file size");
                CheckUrl(reader.GetString(3), "file URL", failures, ref checks);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, sha1, size, url FROM assets.v_asset_objects;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                CheckSafePath(reader.GetString(0), "asset", failures, ref checks);
                Check(IsSha1(reader.GetString(1)), "bad asset SHA-1");
                Check(reader.GetInt64(2) >= 0, "negative asset size");
                CheckUrl(reader.GetString(3), "asset URL", failures, ref checks);
            }
        }

        Check(CountWhere(connection, "main.plans", "kind = 'client' AND assets_index IS NULL") == 0, "client without assets");
        Check(CountWhere(connection, "main.plans", "kind = 'client' AND native_dir IS NULL") == 0, "client without native");
        Check(CountWhere(connection, "main.plans", "kind = 'server' AND native_dir IS NOT NULL") == 0, "server has native");
        Check(CountWhere(connection, "launch.plan_classpath", "plan_id NOT IN (SELECT id FROM main.plans)") == 0, "classpath plan missing");
        Check(CountWhere(connection, "launch.launch_args", "plan_id NOT IN (SELECT id FROM main.plans)") == 0, "launch plan missing");
        Check(CountWhere(connection, "launch.plan_classpath", "plan_id IN (SELECT id FROM main.plans WHERE kind = 'server')") == 0, "server has classpath");
        Check(CountWhere(connection, "launch.launch_args", "plan_id IN (SELECT id FROM main.plans WHERE kind = 'server')") == 0, "server has launch args");
        Check(CountWhere(connection, "main.plans", "kind = 'client' AND id NOT IN (SELECT plan_id FROM launch.plan_classpath)") == 0, "client missing classpath");
        Check(CountWhere(connection, "main.plans", "assets_index IS NOT NULL AND assets_index NOT IN (SELECT index_id FROM assets.asset_docs)") == 0, "plan references missing asset doc");
        Check(CountWhere(connection, "main.files", "content_id NOT IN (SELECT id FROM main.contents)") == 0, "file references missing content");
        Check(CountGroups(connection, "launch.plan_classpath", "plan_id, path") == 0, "duplicate classpath path");
        Check(CountGroups(connection, "assets.asset_objects", "doc_id, path") == 0, "duplicate asset path");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.id, COUNT(f.path)
                FROM main.plans p LEFT JOIN main.files f ON f.plan_id = p.id
                WHERE p.kind = 'server'
                GROUP BY p.id
                HAVING COUNT(f.path) != 1;
                """;
            Check(!HasRowsCommand(command), "server plan does not have exactly one file");
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT from_path, to_path FROM assets.asset_links;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                CheckSafePath(reader.GetString(0), "asset link from", failures, ref checks);
                CheckSafePath(reader.GetString(1), "asset link to", failures, ref checks);
            }
        }

        Console.WriteLine($"checks={checks} versions={counts["versions"]} clients={counts["client"]} servers={counts["server"]} assets={counts["assets"]} files={counts["files"]} classpath={counts["classpath"]}");
        Console.WriteLine($"shards=catalog:{manifest.Shards.Single(s => s.Id == "catalog").Size},launch:{manifest.Shards.Single(s => s.Id == "launch").Size},assets:{manifest.Shards.Single(s => s.Id == "assets").Size}");
        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"FAILURES={failures.Count}");
            foreach (var failure in failures.Take(50)) Console.Error.WriteLine($"- {failure}");
            return 1;
        }
        Console.WriteLine("all invariants hold");
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

static void Attach(SqliteConnection connection, string path, string alias)
{
    using var command = connection.CreateCommand();
    command.CommandText = $"ATTACH DATABASE '{path.Replace("'", "''")}' AS {alias};";
    command.ExecuteNonQuery();
}

static void CheckSafePath(string path, string label, List<string> failures, ref int checks)
{
    checks++;
    try { SafeRelativePath.Parse(path, "$." + label); }
    catch (RelayException error) { failures.Add($"{label} unsafe path {Text.Sanitize(path)}: {error.Message}"); }
}

static void CheckUrl(string url, string label, List<string> failures, ref int checks)
{
    checks++;
    try { OfficialHttpsUrl.Parse(url, "$." + label); }
    catch (RelayException error) { failures.Add($"{label} invalid {Text.Sanitize(url)}: {error.Message}"); }
}

static bool IsSha1(string value) => value.Length == 40 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

static bool HasRows(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    return reader.Read();
}

static bool HasRowsCommand(SqliteCommand command)
{
    using var reader = command.ExecuteReader();
    return reader.Read();
}

static long Count(SqliteConnection connection, string table)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
    return Convert.ToInt64(command.ExecuteScalar());
}

static long CountWhere(SqliteConnection connection, string table, string predicate)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE " + predicate + ";";
    return Convert.ToInt64(command.ExecuteScalar());
}

static long CountGroups(SqliteConnection connection, string table, string columns)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM (SELECT 1 FROM " + table + " GROUP BY " + columns + " HAVING COUNT(*) > 1);";
    return Convert.ToInt64(command.ExecuteScalar());
}

static string Scalar(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
}

static string FileSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
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

file sealed record ShardInfo(string Id, string Path, string Sha256, long Size, int UserVersion);
file sealed record IndexCounts(int Versions, int ClientPlans, int ServerPlans, int AssetDocs);
file sealed record IndexManifest(int Schema, string Format, string GeneratedAt, string LatestRelease,
    string LatestSnapshot, IReadOnlyList<ShardInfo> Shards, IndexCounts Counts, string SourceManifestSha256);



