# SQLite Index Schema (schema 2, database user_version 1)

The index is **three SQLite shards plus one small manifest**. There are no per-version JSON
files and no JSON columns.

```text
index.json
  shards[]:
    catalog.idx   32,817,152 B
    launch.idx    25,436,160 B
    assets.idx    10,952,704 B
```

## Shard split

### `catalog.idx`

```text
meta
platforms
versions
plans
contents
content_urls
files
natives
extract_exclude
views: v_plan_files, v_file_urls
```

This is the version/plan/file/content catalog. It contains no asset object rows and no
classpath/launch rows.

### `launch.idx`

```text
meta
plan_classpath
plan_directories
launch_args
```

`plan_id` values are the same integer plan ids used by `catalog.plans`. There is no
cross-shard foreign key; the verifier checks the relationship.

### `assets.idx`

```text
meta
asset_docs
asset_objects
asset_links
view: v_asset_objects
```

Asset objects are content-addressed; path is a generated virtual column and URL is derived.

## SQLite features

Used:

- `STRICT` tables;
- `WITHOUT ROWID` link tables;
- one generated asset path;
- `application_id = 1296253511`;
- `user_version = 1`;
- `CHECK`, primary keys, foreign keys within a shard;
- plain views.

Not used:

- JSON columns or JSON1;
- `PRAGMA optimize`;
- `VACUUM`.

Compatibility floor: **SQLite 3.37 (2021)**, because `STRICT` and generated columns are used.
Older 3.x readers are not a target. There is no absolute guarantee for 2004-era 3.0 readers.

## Tables

### `meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)`

Build metadata: `schema`, `format`, `database_schema`, `resolver`, `latest_release`,
`latest_snapshot`, `source_manifest_sha256`, `count_*`, and a `shard` id. No build timestamp
is stored, so identical metadata produces identical shard bytes; the manifest carries
`generatedAt`.

### `platforms(ordinal INTEGER PRIMARY KEY, token TEXT NOT NULL UNIQUE)`

Platform tokens.

### `versions`

```text
id, key, type, release_time, java_major, java_defaulted,
assets_index, server_available, revision, source_manifest_sha1, schema_version
```

### `plans`

```text
id, version_id, kind, platform, revision, total_bytes, main_class,
assets_index, assets_mode, assets_root, assets_document, assets_sha1, assets_size,
logging_argument, logging_path, native_dir
UNIQUE(version_id, kind, platform)
```

Client rows have assets/native; server rows have NULL assets/native and `platform = ''`.

### `contents` / `content_urls`

```sql
contents(id INTEGER PRIMARY KEY, sha1 TEXT NOT NULL UNIQUE, size INTEGER NOT NULL)
content_urls(content_id, ordinal, url, PRIMARY KEY(content_id, ordinal))
```

Content is deduplicated by SHA-1.

### `files`

```sql
files(plan_id, role, path, content_id, PRIMARY KEY(plan_id, path)) WITHOUT ROWID
```

Many logical paths can reference one content row.

### `plan_classpath` / `plan_directories` / `launch_args`

```sql
plan_classpath(plan_id, ordinal, path, PRIMARY KEY(plan_id, ordinal))
plan_directories(plan_id, ordinal, path, PRIMARY KEY(plan_id, ordinal))
launch_args(plan_id, phase, ordinal, value, PRIMARY KEY(plan_id, phase, ordinal))
```

`phase` is `jvm`, `jvm_defaults`, or `game`. Order is explicit in `ordinal`. No JSON.

### `natives` / `extract_exclude`

```sql
natives(plan_id PRIMARY KEY, dir)
extract_exclude(plan_id, path, PRIMARY KEY(plan_id, path)) WITHOUT ROWID
```

### `asset_docs`

```sql
asset_docs(index_id TEXT PRIMARY KEY, mode, root, sha1, total_bytes, revision, schema_version)
```

### `asset_objects`

```sql
asset_objects(doc_id, sha1, size,
              path GENERATED ALWAYS AS (...) VIRTUAL,
              PRIMARY KEY(doc_id, sha1)) WITHOUT ROWID
```

Canonical URL:

```text
https://resources.download.minecraft.net/<sha1[0:2]>/<sha1>
```

### `asset_links`

```sql
asset_links(doc_id, ordinal, from_path, to_path, PRIMARY KEY(doc_id, ordinal))
```

Copy/hardlink operations, not downloads.

## Cross-shard consistency

- Every `launch.plan_classpath.plan_id` exists in `catalog.plans.id`.
- Every `catalog.plans.assets_index` exists in `assets.asset_docs.index_id`.
- Client plans have assets/native; server plans do not.
- Client plans have classpath rows; server plans do not have classpath or launch args.
- Server plans have exactly one file.
- Every file references content; every content has at least one URL.
- No duplicate classpath path per plan; no duplicate asset path per document.
- All paths, SHA-1 values, sizes, and URLs are validated.
- Each shard passes `integrity_check` and `foreign_key_check`.
- Each shard's SHA-256/size matches the manifest.

## Client queries

```sql
ATTACH 'launch.idx' AS launch;
ATTACH 'assets.idx' AS assets;

-- catalog
SELECT id, key, type, release_time, java_major, server_available
FROM versions ORDER BY release_time DESC;

SELECT id, revision, total_bytes, main_class, assets_index, native_dir
FROM plans WHERE version_id = ? AND kind = 'client' AND platform = ?;

SELECT path, sha1, size, ordinal, url
FROM v_file_urls WHERE plan_id = ? ORDER BY path, ordinal;

-- launch
SELECT path FROM launch.plan_classpath WHERE plan_id = ? ORDER BY ordinal;
SELECT path FROM launch.plan_directories WHERE plan_id = ? ORDER BY ordinal;
SELECT phase, ordinal, value FROM launch.launch_args WHERE plan_id = ? ORDER BY phase, ordinal;

-- assets
SELECT path, sha1, size, url FROM assets.v_asset_objects WHERE index_id = ?;
SELECT from_path, to_path FROM assets.asset_links WHERE doc_id = ? ORDER BY ordinal;
```

Use parameterized statements. Never concatenate user input into SQL.

## Build and size

The builder creates one full DB in a transaction, closes/flushes it, then copies stable table
groups into three `STRICT` shards. Each shard is closed, flushed, hashed, and only then is the
manifest written. The verifier opens all three read-only with `PRAGMA temp_store=MEMORY` and
`PRAGMA trusted_schema=OFF`.

No shard exceeds 100 MB. No compression is used. If future growth exceeds 100 MB per shard,
split at a stable boundary (for example, files vs plans) and extend the manifest.
