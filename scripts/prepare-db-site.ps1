param(
    [string]$Source = 'out',
    [string]$Site = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = if ([IO.Path]::IsPathRooted($Source)) { $Source } else { Join-Path $repo $Source }
$site = if ([string]::IsNullOrWhiteSpace($Site)) {
    Join-Path ([IO.Path]::GetTempPath()) ('relay-site-' + [guid]::NewGuid().ToString('N'))
} elseif ([IO.Path]::IsPathRooted($Site)) { $Site } else { Join-Path $repo $Site }

$siteFull = [IO.Path]::GetFullPath($site).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$sourceFull = [IO.Path]::GetFullPath($source).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$repoFull = [IO.Path]::GetFullPath($repo).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$seedFull = [IO.Path]::GetFullPath((Join-Path $repo 'site')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($siteFull -eq $sourceFull -or $siteFull -eq $repoFull -or $siteFull -eq $seedFull -or $siteFull -eq [IO.Path]::GetPathRoot($siteFull).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
    throw "refusing to use repository, seed or filesystem root as site output: $site"
}

function Flush-File([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    try { $stream.Flush($true) } finally { $stream.Dispose() }
}

$manifestPath = Join-Path $source 'index.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "manifest missing: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.format -ne 'sqlite-shards') { throw "unexpected manifest format: $($manifest.format)" }
if (-not $manifest.shards -or @($manifest.shards).Count -ne 3) { throw 'manifest must contain exactly three shards' }

$bundleInput = (@($manifest.shards) | ForEach-Object { [string]$_.sha256 }) -join '|'
$bundleSha = [BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($bundleInput))).Replace('-', '').ToLowerInvariant()

if (Test-Path -LiteralPath $site) { Remove-Item -LiteralPath $site -Recurse -Force }
New-Item -ItemType Directory -Force -Path $site | Out-Null

$seed = Join-Path $repo 'site'
if (-not (Test-Path -LiteralPath $seed -PathType Container)) { throw "site seed missing: $seed" }
Get-ChildItem -LiteralPath $seed -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $site -Recurse -Force
}

$seenShards = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($shard in @($manifest.shards)) {
    $id = [string]$shard.id
    $path = [string]$shard.path
    if ($id -notin @('catalog', 'launch', 'assets')) { throw "unknown shard id: $id" }
    if (-not $seenShards.Add($id)) { throw "duplicate shard id: $id" }
    if ($path -ne ($id + '.idx')) { throw "non-canonical shard path: $path" }
    $sourcePath = Join-Path $source $path
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "shard missing: $sourcePath" }
    if ((Get-Item -LiteralPath $sourcePath).Length -ne [long]$shard.size) { throw "$id size does not match manifest" }
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne [string]$shard.sha256) { throw "$id SHA-256 does not match manifest" }

    $relative = 'data/' + $bundleSha + '/' + $id + '.idx'
    $destination = Join-Path $site $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destination -Force
    if ((Get-Item -LiteralPath $destination).Length -ne [long]$shard.size) { throw "$id copied size mismatch" }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne [string]$shard.sha256) { throw "$id copied SHA-256 mismatch" }
    Flush-File $destination
    $shard.path = $relative
    $shard | Add-Member -NotePropertyName urls -NotePropertyValue @($relative) -Force
}
if ($seenShards.Count -ne 3) { throw 'manifest does not contain exactly catalog/launch/assets' }

$displayTimeZone = $env:DISPLAY_TIME_ZONE
if ([string]::IsNullOrWhiteSpace($displayTimeZone)) { $displayTimeZone = 'UTC' }
try { $timeZone = [TimeZoneInfo]::FindSystemTimeZoneById($displayTimeZone) }
catch { throw "unknown DISPLAY_TIME_ZONE: $displayTimeZone" }
$generatedAt = [DateTimeOffset]::Parse([string]$manifest.generatedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
$utcTime = $generatedAt.ToUniversalTime()
$localTime = [TimeZoneInfo]::ConvertTime($utcTime, $timeZone)
$utcText = $utcTime.ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
$localText = $localTime.ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
$isoText = $utcTime.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
$shardById = @{}
foreach ($shard in @($manifest.shards)) { $shardById[[string]$shard.id] = $shard }
function Format-Bytes([long]$bytes) {
    if ($bytes -ge 1MB) { return ($bytes / 1MB).ToString('N1', [Globalization.CultureInfo]::InvariantCulture) + ' MiB' }
    if ($bytes -ge 1KB) { return ($bytes / 1KB).ToString('N1', [Globalization.CultureInfo]::InvariantCulture) + ' KiB' }
    return $bytes.ToString([Globalization.CultureInfo]::InvariantCulture) + ' B'
}
$htmlValues = @{
    '{{UPDATED_ISO}}' = $isoText
    '{{UPDATED_UTC}}' = $utcText
    '{{UPDATED_LOCAL}}' = $localText
    '{{DISPLAY_TIME_ZONE}}' = $displayTimeZone
    '{{BUNDLE_SHA}}' = $bundleSha
    '{{COUNT_VERSIONS}}' = ([int]$manifest.counts.versions).ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{COUNT_CLIENT_PLANS}}' = ([int]$manifest.counts.clientPlans).ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{COUNT_SERVER_PLANS}}' = ([int]$manifest.counts.serverPlans).ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{COUNT_ASSET_DOCS}}' = ([int]$manifest.counts.assetDocs).ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{SIZE_CATALOG}}' = Format-Bytes ([long]$shardById['catalog'].size)
    '{{SIZE_LAUNCH}}' = Format-Bytes ([long]$shardById['launch'].size)
    '{{SIZE_ASSETS}}' = Format-Bytes ([long]$shardById['assets'].size)
}
foreach ($name in @('index.html', '404.html')) {
    $path = Join-Path $site $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "page file missing: $name" }
    $text = Get-Content -LiteralPath $path -Raw
    foreach ($pair in $htmlValues.GetEnumerator()) { $text = $text.Replace($pair.Key, [string]$pair.Value) }
    if ($text -match '\{\{[A-Z0-9_]+\}\}') { throw "unresolved placeholder in $name" }
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}

$indexPath = Join-Path $site 'index.json'
Set-Content -LiteralPath $indexPath -Encoding utf8 -Value (($manifest | ConvertTo-Json -Depth 12) + "`n")
Flush-File $indexPath

$noJekyll = Join-Path $site '.nojekyll'
if (-not (Test-Path -LiteralPath $noJekyll)) { New-Item -ItemType File -Path $noJekyll | Out-Null }
$cname = Join-Path $site 'CNAME'
if (-not [string]::IsNullOrWhiteSpace($env:PAGES_CNAME)) {
    Set-Content -LiteralPath $cname -Encoding ascii -Value $env:PAGES_CNAME.Trim()
}
elseif (Test-Path -LiteralPath $cname) {
    Remove-Item -LiteralPath $cname -Force
}

$files = (Get-ChildItem -LiteralPath $site -Recurse -File -Force | Measure-Object).Count
Write-Host "prepared site=$site bundle=$bundleSha files=$files"


