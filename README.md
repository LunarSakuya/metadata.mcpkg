# Relay Index — SQLite index

> **Independent project. Not an official Minecraft product. Not affiliated with or
> endorsed by Mojang or Microsoft. Metadata only. No game files. No authentication
> bypass. No piracy. Read [LEGAL.md](LEGAL.md).**

This repository contains the generator, verifier and Action for a sharded SQLite Minecraft
metadata index. **The generated databases are not stored in the main branch.**

## Branch model

```text
main   code, scripts, site seed, docs, legal — no database
site   generated page tree, force-pushed by the Action; GitHub Pages serves it:
       index.json
       data/<bundle-sha256>/catalog.idx
       data/<bundle-sha256>/launch.idx
       data/<bundle-sha256>/assets.idx
       index.html / 404.html / .nojekyll / optional CNAME
```

Every run replaces the `site` branch with a single-commit orphan tree. The main branch and
the page branch do not accumulate generated database history.

## Action schedule

```text
02:35 Asia/Shanghai (18:35 UTC)
```

The workflow is public-repository scheduled and also supports manual dispatch. If GitHub
delays the schedule, the next run simply replaces the page branch.

## What the Action does

```text
checkout main
setup .NET 10.0.x
scripts/update-db.ps1
  → fetch official metadata with 4 workers, 5 attempts with jitter and SHA-1 validation
  → build one full SQLite DB in a transaction
  → close/flush, then split into catalog.idx, launch.idx, assets.idx
  → hash each shard, verify all three read-only
scripts/prepare-db-site.ps1
  → copy site seed and shards to a temporary directory outside main
  → content-address the shard directory by a combined SHA-256
  → rewrite manifest shard paths
scripts/publish-site.ps1
  → orphan single-commit page tree
  → git push --force origin HEAD:site
GitHub Pages
  → build and deploy the site branch
  → optional verify of /index.json when PAGES_CNAME is set
```

No prebuilt database is committed to this release folder or to main. The update script takes
an atomic cache lock and cleans a stale lock only when its recorded PID is gone.

GitHub Pages uses **Source: Deploy from a branch**, branch `site`, folder `/`. The workflow
only force-pushes that branch; GitHub Pages builds and deploys it. The optional verify step
polls `/index.json` for up to 5 minutes when `PAGES_CNAME` is set.

Repository variables:

```text
PAGES_CNAME        custom domain; empty means no active CNAME
DISPLAY_TIME_ZONE  local zone for the page timestamp; empty means UTC
```

Optional secret:

```text
RELAY_PUSH_TOKEN   fine-grained PAT, Contents: Read and write
```

The generated page shows the build time in UTC and in the configured local zone, plus
version/plan/asset counts and shard sizes.

## Current generated shape

When built from the current metadata cache:

```text
versions        920
client plans    6,440
server plans    853
asset documents 58
catalog.idx     32,817,152 bytes
launch.idx      25,436,160 bytes
assets.idx      10,952,704 bytes
verify checks   1,934,426
```

Each shard is under 100 MB. Shard hashes are deterministic for identical metadata; the
manifest `generatedAt` may change.

## Client

1. Fetch `/index.json`.
2. Read `shards[]`: `catalog`, `launch`, `assets`.
3. Download each `urls` entry (content-addressed path).
4. Verify each shard's `size` and SHA-256.
5. Open `catalog.idx` read-only with SQLite 3.37+.
6. `ATTACH` `launch.idx` and `assets.idx`.
7. Query `catalog.versions`, `catalog.plans`, `catalog.v_file_urls`,
   `assets.v_asset_objects`, `assets.asset_links`, `launch.plan_classpath`,
   `launch.plan_directories`, `launch.launch_args`.

See [docs/DB-CLIENT.md](docs/DB-CLIENT.md) and [docs/DB-SCHEMA.md](docs/DB-SCHEMA.md).

## SQLite policy

Used: `STRICT`, `WITHOUT ROWID`, one generated asset path, `application_id`,
`user_version`, `CHECK`, foreign keys, plain views.

Not used: JSON columns, JSON1, `PRAGMA optimize`, `VACUUM`.

Compatibility floor: SQLite 3.37 (2021). Older 3.x versions are not a target.

## Metadata ceilings

The ceilings are named parameters/constants, not scattered magic numbers:

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

`update-db.ps1` exposes the download limits as parameters; the C# readers use named
`MetadataAudit`, `VersionParser` and `MojangResolver` constants. Manifest bytes are capped
by `MaxManifestBytes`.

No cross-shard foreign keys. Cross-shard consistency is checked by `Relay.DbVerify`.

## Page caching

GitHub Pages controls `Cache-Control`; the repository cannot set per-path TTLs. GitHub Pages
ignores `_headers` and `_redirects`, so this repository does not include them.

Shard paths are content-addressed. A new build gets a new path, so the manifest never points
at a reused shard. The manifest itself is small and is fetched from the same host.

## Fork-friendly

There is no hardcoded owner, repository, domain, branch, or path. The page branch is
`site`, the domain comes from the `PAGES_CNAME` repository variable, and git operations use
the repository that contains the workflow. A fork can run the same workflow unchanged.

## Files

```text
src/Relay.Core/           metadata parser and resolver
src/Relay.Db/             full schema, shard schemas, generator
src/Relay.DbVerify/       cross-shard verifier
scripts/update-db.ps1     fetch, build, split, verify, atomic swap
scripts/prepare-db-site.ps1
scripts/publish-site.ps1
site/                     page seed, no database
docs/                     schema, client, deployment
.github/workflows/relay-db.yml
LEGAL.md  SECURITY.md  LICENSE  global.json
```

No `legal/` directory, no mirrored EULA text, no `bin/obj/out`, no generated database.









