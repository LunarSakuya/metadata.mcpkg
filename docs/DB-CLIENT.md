# SQLite Index Client Protocol

## 1. Read the manifest

```http
GET /index.json
```

```json
{
  "schema": 2,
  "format": "sqlite-shards",
  "generatedAt": "2026-10-25T03:02:39Z",
  "shards": [
    {
      "id": "catalog",
      "path": "data/<bundle-sha256>/catalog.idx",
      "sha256": "...",
      "size": 32817152,
      "userVersion": 1,
      "urls": ["data/<bundle-sha256>/catalog.idx"]
    },
    {
      "id": "launch",
      "path": "data/<bundle-sha256>/launch.idx",
      "sha256": "...",
      "size": 25436160,
      "userVersion": 1,
      "urls": ["data/<bundle-sha256>/launch.idx"]
    },
    {
      "id": "assets",
      "path": "data/<bundle-sha256>/assets.idx",
      "sha256": "...",
      "size": 10952704,
      "userVersion": 1,
      "urls": ["data/<bundle-sha256>/assets.idx"]
    }
  ],
  "counts": {
    "versions": 920,
    "clientPlans": 6440,
    "serverPlans": 853,
    "assetDocs": 58
  }
}
```

Fetch `/index.json` directly. Do not serve it from a long-lived cache; the manifest is the
mutable pointer.

## 2. Download and verify all three shards

For each shard:

1. Download one `urls` entry to a temporary file.
2. Verify the file size equals `size`.
3. Verify SHA-256 equals `sha256`.
4. Only then rename it to `<id>.idx`.

Do not open a partially downloaded shard. Keep the previous shard set until all three new
shards are verified. If any shard fails, discard the whole new set.

The hosting layer controls cache lifetime. The shard directory is content-addressed, so a
new build gets a new directory; the manifest is the only mutable pointer.

## 3. Open and attach

SQLite 3.37+:

```python
import sqlite3
con = sqlite3.connect("file:catalog.idx?mode=ro", uri=True)
con.execute("PRAGMA foreign_keys=ON")
con.execute("PRAGMA temp_store=MEMORY")
con.execute("PRAGMA trusted_schema=OFF")
con.execute("ATTACH 'launch.idx' AS launch")
con.execute("ATTACH 'assets.idx' AS assets")
```

C#:

```csharp
var csb = new SqliteConnectionStringBuilder
{
    DataSource = "catalog.idx",
    Mode = SqliteOpenMode.ReadOnly,
    Cache = SqliteCacheMode.Private
};
using var connection = new SqliteConnection(csb.ToString());
connection.Open();
using (var pragma = connection.CreateCommand())
{
    pragma.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF;";
    pragma.ExecuteNonQuery();
}
// ATTACH launch.idx and assets.idx with escaped paths.
```

Do not write to any shard. Do not run migrations. Do not execute SQL from untrusted content.

## 4. Catalog queries

```sql
SELECT id, key, type, release_time, java_major, server_available
FROM versions
ORDER BY release_time DESC;

SELECT id, revision, total_bytes, main_class, assets_index, native_dir
FROM plans
WHERE version_id = ? AND kind = 'client' AND platform = ?;

SELECT path, sha1, size, ordinal, url
FROM v_file_urls
WHERE plan_id = ?
ORDER BY path, ordinal;
```

Server plan:

```sql
SELECT id, revision, total_bytes
FROM plans
WHERE version_id = ? AND kind = 'server';
```

Use `key` for URLs/display keys and `id` for the official version id.

## 5. Download files

- `path` is relative to the game root.
- Use `url` by `ordinal` as fallback order.
- Verify SHA-1 and size after download.
- Create parent directories.
- Cache by SHA-1; catalog content is deduplicated.

## 6. Launch data

```sql
SELECT path FROM launch.plan_classpath WHERE plan_id = ? ORDER BY ordinal;
SELECT path FROM launch.plan_directories WHERE plan_id = ? ORDER BY ordinal;
SELECT phase, ordinal, value FROM launch.launch_args WHERE plan_id = ? ORDER BY phase, ordinal;
```

`phase` is `jvm`, `jvm_defaults`, or `game`. `natives.dir` is in `catalog.plans`; extraction
exclusions are in `catalog.extract_exclude`.

## 7. Assets

```sql
SELECT path, sha1, size, url
FROM assets.v_asset_objects
WHERE index_id = ?;
```

```sql
SELECT from_path, to_path
FROM assets.asset_links
WHERE doc_id = ?;
```

Assets are content-addressed. A link is a copy/hardlink, not a download.

## 8. Failure handling

- Verify every shard's size and SHA-256 before opening.
- Retry transient `429`, `5xx`, timeouts with bounded exponential backoff.
- Do not loop forever.
- Keep the previous shard set until the new set is verified.
- Do not execute files that fail hash verification.

## 9. No game-file hosting

The shards contain metadata and official URLs. They do not contain game files. The host
serving `index.json` or the shards is not a game CDN; game downloads go to the official URLs
stored in the catalog and assets shards.

