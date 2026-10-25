# Relay Index — SQLite metadata index

> Independent project. Not an official Minecraft product. Not affiliated with or endorsed by
> Mojang or Microsoft. Metadata only. No game files, no authentication bypass, no piracy.
> Read [LEGAL.md](LEGAL.md).

Relay Index turns Mojang's public version metadata into a small set of SQLite databases plus
one JSON manifest. Launchers and downloaders read the manifest, verify the shards, and query
the databases for versions, files, launch arguments and assets.

The generated databases are not stored in `main`.

## Branches

```text
main   source, workflow, site seed, docs
site   generated manifest, three SQLite shards and the static page
```

`site` is a single-commit orphan branch, force-pushed by the Action. GitHub Pages serves it.

## Action

Runs daily at:

```text
02:35 Asia/Shanghai (18:35 UTC)
```

Manual runs are available through `workflow_dispatch`.

```text
checkout main
setup .NET 10
restore metadata cache
update-db.ps1
prepare-db-site.ps1
publish-site.ps1        # force-push site
verify published JSON   # when PAGES_CNAME is set
```

`update-db.ps1` fetches metadata with four workers, five attempts per request, and SHA-1
verification. It builds the databases in a transaction, splits them into three shards, and
verifies the result before publication.

`prepare-db-site.ps1` copies the verified shards into a content-addressed directory and
renders the page timestamp, counts and shard sizes.

`publish-site.ps1` force-pushes the page branch. GitHub Pages builds and deploys it.

When `PAGES_CNAME` is set, the workflow polls `/index.json` for up to 30 attempts at 10-second
intervals (5 minutes) and compares `sourceManifestSha256` and every shard SHA-256 with the
local manifest.

## GitHub Pages

```text
Source: Deploy from a branch
Branch: site
Folder: / (root)
```

Custom domain is optional; set it to the same hostname as `PAGES_CNAME`.

Point DNS directly at GitHub Pages:

```text
data.example.com  CNAME  <owner>.github.io
```

No proxy is required. Enable Enforce HTTPS in the Pages settings.

### Repository variables

```text
PAGES_CNAME        custom domain, for example data.example.com
DISPLAY_TIME_ZONE  IANA zone for the page timestamp, for example Asia/Shanghai
```

If `PAGES_CNAME` is empty, no active CNAME is written and the verification step is skipped.
If `DISPLAY_TIME_ZONE` is empty, the page uses UTC.

### Optional secret

```text
RELAY_PUSH_TOKEN   fine-grained PAT with Contents: Read and write
```

The default `GITHUB_TOKEN` is used when the secret is absent. Set the PAT only if branch
protection blocks the default token from pushing `site`.

## Manifest and shards

`/index.json` lists three shards:

```text
catalog.idx   versions, plans, files, contents, natives, extraction rules
launch.idx    classpath, directories, launch arguments
assets.idx    asset documents, objects, links
```

Each entry has an id, relative path, SHA-256, byte size and database `user_version`.

Shard paths are content-addressed:

```text
/data/<bundle-sha256>/catalog.idx
/data/<bundle-sha256>/launch.idx
/data/<bundle-sha256>/assets.idx
```

A new build writes a new directory. `index.json` is the mutable pointer.

## Client

1. Fetch `/index.json`.
2. Download every `shards[].urls` entry.
3. Verify each file's size and SHA-256.
4. Open `catalog.idx` read-only with SQLite 3.37 or newer.
5. `ATTACH` `launch.idx` and `assets.idx`.

```sql
ATTACH 'launch.idx' AS launch;
ATTACH 'assets.idx' AS assets;
```

See [docs/DB-CLIENT.md](docs/DB-CLIENT.md) for queries and failure handling, and
[docs/DB-SCHEMA.md](docs/DB-SCHEMA.md) for tables and invariants.

## Metadata ceilings

The limits are named parameters or constants:

```text
MaxVersions        1,000,000
MaxAssetIndexes    1,000,000
MaxManifestBytes   256 MiB
MaxVersionBytes    64 MiB
MaxIndexBytes      64 MiB
MaxLibraries       1,000,000
MaxAssetObjects    1,000,000
MaxFiles           1,000,000
```

`update-db.ps1` exposes the download limits as parameters. The C# readers use named constants
in `MetadataAudit`, `VersionParser` and `MojangResolver`.

## SQLite

Used: `STRICT`, `WITHOUT ROWID`, one generated asset path, `application_id`, `user_version`,
`CHECK`, foreign keys and plain views.

Not used: JSON columns, JSON1, `PRAGMA optimize`, `VACUUM`.

The compatibility floor is SQLite 3.37.

There are no cross-shard foreign keys. `Relay.DbVerify` checks cross-shard consistency.

## Page caching

GitHub Pages controls `Cache-Control`. The repository cannot set per-path TTLs, and GitHub
Pages ignores `_headers` and `_redirects`.

Shard paths are content-addressed, so a new build never reuses an old shard path. The manifest
is small and is fetched from the same host.

## Run locally

Requirements: .NET 10 SDK and PowerShell 7.

```powershell
pwsh ./scripts/update-db.ps1 -CacheRoot .cache -OutputRoot out
pwsh ./scripts/prepare-db-site.ps1 -Source out -Site ./site-out
```

`update-db.ps1` downloads only metadata. It does not download game files, libraries, assets or
server jars.

## Project layout

```text
src/Relay.Core/             metadata parser and resolver
src/Relay.Db/               schema, shard split and generator
src/Relay.DbVerify/         read-only verifier
scripts/update-db.ps1       fetch, build, split and verify
scripts/prepare-db-site.ps1 page tree and manifest paths
scripts/publish-site.ps1    force-push the page branch
site/                       page seed with timestamp/count placeholders
docs/                       schema, client protocol, deployment
.github/workflows/relay-db.yml
```

## License and legal

See [LICENSE](LICENSE) and [LEGAL.md](LEGAL.md).
