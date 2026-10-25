# Security

> **Independent project. Not an official Minecraft product. Not affiliated with or
> endorsed by Mojang or Microsoft. See [LEGAL.md](LEGAL.md); it is not legal advice.**

Relay Index is a build-time metadata parser and static index generator. It does not execute
downloaded files, does not receive Microsoft/Mojang tokens, and does not expose a dynamic
write API. The generated index is **public data** served by GitHub Pages.

## Trust boundaries

| Input | Trust anchor |
|---|---|
| v2 version manifest | HTTPS from the official host; CI fetches it over TLS |
| version document | `sha1` in the v2 manifest, verified before parsing |
| asset index | `assetIndex.sha1` in the version document, verified before parsing |
| client/library/native/log/server file | `sha1` + `size` in the parsed metadata |
| generated SQLite shards | GitHub Pages content plus per-shard SHA-256 in `index.json` |

The generator parses exactly the bytes whose SHA-1 it verified. It does not re-read the
file between verification and parsing.

## Implemented controls

- Official host allowlist: `piston-meta.mojang.com`, `piston-data.mojang.com`,
  `launchermeta.mojang.com`, `libraries.minecraft.net`, `resources.download.minecraft.net`.
  `launchermeta.mojang.com` is accepted only for fetching legacy metadata;
  `launchermeta` package URLs and `launcher.mojang.com` object URLs are normalized to the
  canonical `piston-meta`/`piston-data` hosts in output.
- HTTPS, default port, no userinfo, no query/fragment.
- Strict relative-path rules: no `..`, absolute paths, backslashes, `:`, control/bidi
  characters, Windows device names (including `COM¹`/`LPT²`/`CLOCK$`), trailing dots/spaces,
  or overlong components.
- Strict version ids: ASCII, bounded, no path/device/placeholder syntax.
- Checked byte totals, file/object counts, JSON sizes and per-file limits.
- Duplicate path/hash/link conflicts fail closed.
- Client plans contain no Mojang `rules`/`downloads`/`libraries`/`inheritsFrom` fields.
- No `unsafe`, no shell execution, no dynamic code loading in the generator.
- Build output is staged and verified before publication; a failed build keeps the previous
  `site` branch unchanged.
- The generator/scripts never write game tokens or user credentials to the index.

## GitHub Pages reality

GitHub Pages is public static hosting. It **cannot** reject browser requests, set custom
cache/TTL headers, or run a `_worker.js` gate. The index therefore has no access-control
security boundary: anyone can fetch `/index.json` and `/data/*`. Its security properties are
integrity and supply-chain provenance, not confidentiality or browser exclusion.

- Content is public; do not put secrets, tokens, internal URLs, or private notes in the
  index or in the `site` branch.
- A public branch remains readable through GitHub Pages, `raw.githubusercontent.com`,
  `git clone` and the GitHub API. The custom hostname is not an access-control boundary.
- GitHub Pages ignores `_headers` and `_redirects`; this repository does not ship them.
- The `site` branch is force-pushed from the generated tree; its history is intentionally
  single-commit and not an audit log of every daily refresh.
- `.nojekyll`, `404.html`, `index.html` and optional `CNAME` are static seed files.

## Deployment requirements

- GitHub Pages source: branch `site`, folder `/`.
- Workflow permissions: `contents: write` to push `site`.
  - Default `GITHUB_TOKEN` is sufficient when branch protection allows it.
  - A fine-grained PAT (`RELAY_PUSH_TOKEN`) needs **Contents: Read and write** on this
    repository only. Classic PAT: `repo`. Do not grant `pages`, `admin`, or unrelated scopes.
- `PAGES_CNAME` is an optional repository variable; only then is an active CNAME created.
- `DISPLAY_TIME_ZONE` is an optional repository variable for the local time shown on the
  generated page. Without it, the page uses UTC.
- Pin GitHub Action versions to commit SHAs and keep `site`/`main` branch protection
  compatible with the bot.
- The client must verify each shard size and SHA-256 from `index.json` before opening it.
  When the static host is not trusted, the client can verify an optional Ed25519 signature
  over `index.json` and the shard hashes.

## Reporting

Do not open a public issue containing exploit details. Use the repository's private
vulnerability reporting if enabled; otherwise contact the maintainer through a private
channel. Include the affected endpoint/field, a minimal reproduction, and impact. Do not
include tokens or personal data.






