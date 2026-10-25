# Deployment — sharded SQLite index

## Branch model

```text
main   code, scripts, site seed, docs, legal — no generated database
site   index.json
       data/<bundle-sha256>/catalog.idx
       data/<bundle-sha256>/launch.idx
       data/<bundle-sha256>/assets.idx
       index.html, 404.html, .nojekyll, optional CNAME
```

The Action never commits a database to `main`. It builds outside the repository, then
force-pushes a single-commit orphan `site` branch. GitHub Pages serves that branch.

## GitHub Pages settings

```text
Settings → Pages → Build and deployment
Source: Deploy from a branch
Branch: site
Folder: / (root)
Custom domain: the hostname in PAGES_CNAME
Enforce HTTPS: on
```

`site` must allow force pushes. `main` can stay protected.

## DNS

Pure DNS, no proxy:

```text
data.example.com  CNAME  <owner>.github.io
```

The GitHub Pages custom domain and the DNS CNAME must match. Do not add a proxy in front
unless you intend to change the cache behavior; GitHub Pages serves the origin directly.

## Action schedule

```text
02:35 Asia/Shanghai = 18:35 UTC
```

Public repository schedule plus `workflow_dispatch`. GitHub may delay scheduled runs. A late
run still replaces the page branch after build and verification.

The workflow restores `.cache` with `actions/cache`, so unchanged metadata is reused. Every
network operation has five attempts with exponential backoff and jitter. `update-db.ps1`
takes an atomic cache lock; a stale lock is reclaimed only when its recorded PID is gone.

## Action flow

```text
update-db.ps1
  → fetch metadata, build shards, verify
prepare-db-site.ps1
  → copy seed and shards to a temporary tree
  → render timestamp, counts and shard sizes
  → write index.json with content-addressed shard paths
publish-site.ps1
  → orphan single-commit tree
  → git push --force origin HEAD:site
verify
  → poll https://$PAGES_CNAME/index.json for up to 5 minutes
  → compare sourceManifestSha256 and every shard SHA-256
```

GitHub Pages builds and deploys `site` after the push. The verify step is optional when
`PAGES_CNAME` is empty; without a custom domain, check the repository Pages URL manually.

## Repository variables and secret

Configure under **Settings → Secrets and variables → Actions**.

Variables:

```text
PAGES_CNAME        custom domain, for example data.example.com
DISPLAY_TIME_ZONE  IANA zone for the page timestamp, for example Asia/Shanghai
```

Secret:

```text
RELAY_PUSH_TOKEN   optional fine-grained PAT, Contents: Read and write
```

The workflow reads:

```yaml
${{ vars.PAGES_CNAME }}
${{ vars.DISPLAY_TIME_ZONE }}
${{ secrets.RELAY_PUSH_TOKEN || github.token }}
```

`GITHUB_TOKEN` is automatic. `RELAY_PUSH_TOKEN` is only needed when branch protection or
repository policy blocks the default token's push to `site`. Do not put the PAT in a
variable; it belongs in **Secrets**.

## Page timestamp and statistics

`site/index.html` and `site/404.html` contain placeholders. `prepare-db-site.ps1` replaces
them from the manifest:

- build time in UTC;
- build time in `DISPLAY_TIME_ZONE`;
- version, client plan, server plan and asset document counts;
- catalog, launch and assets shard sizes.

The seed files are placeholders until the first successful Action run. A manual
`workflow_dispatch` run produces the first real page.

## Content-addressed paths

```text
/data/<bundle-sha256>/catalog.idx
/data/<bundle-sha256>/launch.idx
/data/<bundle-sha256>/assets.idx
/index.json
```

A new build gets a new directory. `index.json` is the only mutable pointer.

## Headers and caching

GitHub Pages does not support a `_headers` file and does not let the repository set
`Cache-Control` per path. This repository therefore does not ship `_headers` or `_redirects`.

The TTL is whatever GitHub Pages applies. The manifest is small; shard paths are
content-addressed, so a new build never reuses an old shard path.

## Size and limits

Current shards:

```text
catalog.idx   32,817,152 B
launch.idx    25,436,160 B
assets.idx    10,952,704 B
```

| Limit | Value |
|---|---:|
| Git single file | about 100 MB |
| GitHub Pages site | about 1 GB |

Each shard is below the Git limit. If a shard approaches 100 MB, split at a stable boundary
and extend the manifest `shards[]` list.

## Atomicity and rollback

1. `update-db.ps1` builds a full staging DB, splits it into three shards, verifies all three,
   then swaps the verified `out/` tree.
2. `prepare-db-site.ps1` copies the seed and all verified shards to a temporary directory
   outside main, content-addresses the shard directory, renders the page, and rewrites
   manifest shard paths.
3. `publish-site.ps1` creates an orphan single-commit tree and force-pushes `site`.

A failure before step 3 leaves the previous `site` branch untouched.

The branch is force-pushed, so there is no previous commit on `site`. Keep a local or CI
artifact of the last known-good `out/` if rollback is needed.

## Public origin

The repository and the `site` branch are public. Anyone can read them through GitHub Pages,
`raw.githubusercontent.com`, `git clone` or the GitHub API. GitHub Pages hosting does not
provide access control; it is public static content. Do not put secrets or private notes in
the site branch.

## Authentication

The default `GITHUB_TOKEN` with `permissions: contents: write` can push `site`. If branch
protection blocks it, set `RELAY_PUSH_TOKEN` as a fine-grained PAT with
**Contents: Read and write** on this repository.
