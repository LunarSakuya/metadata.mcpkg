param(
    [string]$CacheRoot = '.cache',
    [string]$OutputRoot = 'out',
    [string]$ManifestUrl = 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json',
    [string]$Platforms = 'windows-x64,windows-x86,windows-arm64,linux-x64,linux-arm64,osx-x64,osx-arm64',
    [switch]$SkipFetch,
    [int]$FetchConcurrency = 4,
    [int]$MaxVersions = 1000000,
    [int]$MaxAssetIndexes = 1000000,
    [long]$MaxManifestBytes = 256MB,
    [long]$MaxVersionBytes = 64MB,
    [long]$MaxIndexBytes = 64MB
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($FetchConcurrency -lt 1 -or $FetchConcurrency -gt 16) { throw "FetchConcurrency must be between 1 and 16" }
if ($MaxVersions -lt 1 -or $MaxAssetIndexes -lt 1 -or $MaxManifestBytes -lt 1 -or $MaxVersionBytes -lt 1 -or $MaxIndexBytes -lt 1) {
    throw "metadata limits must be positive"
}
$repo = Split-Path -Parent $PSScriptRoot
$cache = if ([IO.Path]::IsPathRooted($CacheRoot)) { $CacheRoot } else { Join-Path $repo $CacheRoot }
$out = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repo $OutputRoot }
$repoFull = [IO.Path]::GetFullPath($repo).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$outFull = [IO.Path]::GetFullPath($out).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($outFull -eq $repoFull -or $outFull -eq [IO.Path]::GetPathRoot($outFull).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
    throw "refusing to use repository or filesystem root as output: $out"
}
$meta = Join-Path $cache 'meta'
$indexes = Join-Path $cache 'indexes'
$cacheOut = Join-Path $cache 'out'
$manifestPath = Join-Path $cacheOut 'version_manifest_v2.json'
New-Item -ItemType Directory -Force -Path $meta, $indexes, $cacheOut | Out-Null
# Atomic directory create is the lock. A crashed process leaves a stale directory;
# it is reclaimed only when its recorded PID is gone, or after two hours if the
# owner file was never written. The scheduled job has a 45-minute timeout.
$lockDir = Join-Path $cache '.relay-update.lock'
$locked = $false
for ($lockAttempt = 1; $lockAttempt -le 2; $lockAttempt++) {
    try {
        New-Item -ItemType Directory -Path $lockDir -ErrorAction Stop | Out-Null
        $locked = $true
        break
    }
    catch {
        $info = Get-Item -LiteralPath $lockDir -ErrorAction SilentlyContinue
        if ($null -eq $info) { continue }
        $stale = $false
        $ownerPath = Join-Path $lockDir 'owner.json'
        if (Test-Path -LiteralPath $ownerPath -PathType Leaf) {
            try {
                $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
                $process = Get-Process -Id ([int]$owner.pid) -ErrorAction SilentlyContinue
                if ($null -eq $process) { $stale = $true }
            }
            catch { $stale = $true }
        }
        $age = [DateTime]::UtcNow - $info.LastWriteTimeUtc
        if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf) -and $age -ge [TimeSpan]::FromHours(2)) { $stale = $true }
        if (-not $stale) { throw "another relay update is already running: $lockDir" }
        Remove-Item -LiteralPath $lockDir -Recurse -Force
    }
}
if (-not $locked) { throw "could not acquire update lock: $lockDir" }
try {
    Set-Content -LiteralPath (Join-Path $lockDir 'owner.json') -Encoding utf8 -Value (ConvertTo-Json @{
        pid = $PID
        startedUtc = [DateTime]::UtcNow.ToString('O')
        host = [Environment]::MachineName
        runId = $env:GITHUB_RUN_ID
    })
}
catch {
    Remove-Item -LiteralPath $lockDir -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

$allowedHosts = @(
    'piston-meta.mojang.com',
    'piston-data.mojang.com',
    'launchermeta.mojang.com',
    'libraries.minecraft.net',
    'resources.download.minecraft.net'
)

function Assert-OfficialUrl([string]$url) {
    $uri = [uri]$url
    if ($uri.Scheme -ne 'https') { throw "not HTTPS: $url" }
    if (-not $uri.IsDefaultPort) { throw "non-default port: $url" }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) { throw "userinfo in URL: $url" }
    if (-not [string]::IsNullOrEmpty($uri.Query)) { throw "query in URL: $url" }
    if (-not [string]::IsNullOrEmpty($uri.Fragment)) { throw "fragment in URL: $url" }
    if ($uri.Host -notin $allowedHosts) { throw "non-official host: $($uri.Host)" }
}

function Assert-SafeId([string]$id) {
    if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._()+\-]{0,127}$') { throw "unsafe version id: $id" }
    if ($id.Trim() -ne $id -or $id.EndsWith('.')) { throw "unsafe version id: $id" }
    $base = (($id -split '\.')[0]).TrimEnd(' ', '.').ToUpperInvariant()
    if ($base -in @('CON', 'PRN', 'AUX', 'NUL', 'CLOCK$', 'CONIN$', 'CONOUT$')) { throw "reserved version id: $id" }
    if ($base -match '^(COM|LPT)[1-9]$') { throw "reserved version id: $id" }
}

try {
    $parent = Split-Path -Parent $out
    $leaf = Split-Path -Leaf $out
    if (-not (Test-Path -LiteralPath $out) -and (Test-Path -LiteralPath $parent -PathType Container)) {
        $recover = Get-ChildItem -LiteralPath $parent -Directory -Filter ($leaf + '.old-*') -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($recover) {
            Move-Item -LiteralPath $recover.FullName -Destination $out
            Write-Host "recovered previous output from $($recover.Name)"
        }
    }

    if (-not $SkipFetch) {
        Assert-OfficialUrl $ManifestUrl
        $manifestTemporary = $manifestPath + '.tmp-' + $PID + '-' + [guid]::NewGuid().ToString('N')
        $manifestError = $null
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try {
                Invoke-WebRequest -Uri $ManifestUrl -OutFile $manifestTemporary -MaximumRedirection 0 -TimeoutSec 60 -ErrorAction Stop
                if ((Get-Item -LiteralPath $manifestTemporary).Length -gt $MaxManifestBytes) { throw "manifest is larger than $MaxManifestBytes bytes" }
                $candidate = Get-Content -LiteralPath $manifestTemporary -Raw | ConvertFrom-Json
                $candidateEntries = @($candidate.versions)
                if ($candidateEntries.Count -eq 0) { throw 'manifest has no versions array' }
                if ($candidateEntries.Count -gt $MaxVersions) { throw "manifest has too many versions: $($candidateEntries.Count)" }
                Move-Item -LiteralPath $manifestTemporary -Destination $manifestPath -Force
                $manifestError = $null
                break
            }
            catch {
                $manifestError = $_.Exception.Message
                if (Test-Path -LiteralPath $manifestTemporary) { Remove-Item -LiteralPath $manifestTemporary -Force -ErrorAction SilentlyContinue }
                if ($attempt -lt 5) { Start-Sleep -Seconds ([Math]::Min(16, [Math]::Pow(2, $attempt - 1))) }
            }
        }
        if ($manifestError) { throw "manifest download failed after 5 attempts: $manifestError" }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifestEntries = @($manifest.versions)
        if ($manifestEntries.Count -eq 0) { throw 'manifest has no versions array' }
        if ($manifestEntries.Count -gt $MaxVersions) { throw "manifest has too many versions: $($manifestEntries.Count)" }
        Write-Host ("manifest sha256=" + (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())
        Write-Host ("versions=" + $manifestEntries.Count)

        $versionItems = [System.Collections.Generic.List[object]]::new()
        $seenIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $manifestEntries) {
            $id = [string]$entry.id
            Assert-SafeId $id
            if (-not $seenIds.Add($id)) { throw "duplicate version id in manifest: $id" }
            $versionUrl = [string]$entry.url
            Assert-OfficialUrl $versionUrl
            $sha1 = [string]$entry.sha1
            if ($sha1 -notmatch '^[0-9a-f]{40}$') { throw "invalid version SHA-1 for $id" }
            $versionItems.Add([pscustomobject]@{
                Id = $id
                Url = $versionUrl
                Sha1 = $sha1
                Path = Join-Path $meta ($id + '.json')
            })
        }

        $versionResults = $versionItems | ForEach-Object -Parallel {
            $item = $_
            $versionLimit = $using:MaxVersionBytes
            if (Test-Path -LiteralPath $item.Path -PathType Leaf) {
                if ((Get-Item -LiteralPath $item.Path).Length -le $versionLimit -and
                    (Get-FileHash -LiteralPath $item.Path -Algorithm SHA1).Hash.ToLowerInvariant() -eq $item.Sha1) {
                    return [pscustomobject]@{ Id = $item.Id; Ok = $true; Error = $null }
                }
            }
            $lastError = $null
            for ($attempt = 1; $attempt -le 5; $attempt++) {
                try {
                    $temporary = $item.Path + '.tmp-' + $PID + '-' + [guid]::NewGuid().ToString('N')
                    try {
                        Invoke-WebRequest -Uri $item.Url -OutFile $temporary -MaximumRedirection 0 -TimeoutSec 60 -ErrorAction Stop
                        if ((Get-Item -LiteralPath $temporary).Length -gt $versionLimit) { throw 'version document too large' }
                        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA1).Hash.ToLowerInvariant() -ne $item.Sha1) { throw 'SHA-1 mismatch' }
                        Move-Item -LiteralPath $temporary -Destination $item.Path -Force
                        return [pscustomobject]@{ Id = $item.Id; Ok = $true; Error = $null }
                    }
                    finally {
                        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
                    }
                }
                catch {
                    $lastError = $_.Exception.Message
                    if ($attempt -lt 5) {
                        Start-Sleep -Milliseconds ([Math]::Min(16000, 250 * [Math]::Pow(2, $attempt - 1)) + (Get-Random -Minimum 0 -Maximum 250))
                    }
                }
            }
            return [pscustomobject]@{ Id = $item.Id; Ok = $false; Error = $lastError }
        } -ThrottleLimit $FetchConcurrency

        $badVersions = @($versionResults | Where-Object { -not $_.Ok })
        if ($badVersions.Count -gt 0) {
            $badVersions | Select-Object -First 10 | Format-Table -AutoSize
            throw "$($badVersions.Count) version downloads failed after 5 attempts"
        }
        Write-Host "versions fetched/verified=$(@($versionResults).Count)"

        $indexItems = @{}
        foreach ($item in $versionItems) {
            $document = Get-Content -LiteralPath $item.Path -Raw | ConvertFrom-Json
            if ($null -eq $document.assetIndex) { continue }
            $indexSha1 = [string]$document.assetIndex.sha1
            $indexUrl = [string]$document.assetIndex.url
            Assert-OfficialUrl $indexUrl
            if ($indexSha1 -notmatch '^[0-9a-f]{40}$') { throw "invalid asset index SHA-1 for $($item.Id)" }
            $indexItems[$indexSha1] = [pscustomobject]@{
                Sha1 = $indexSha1
                Url = $indexUrl
                Path = Join-Path $indexes ($indexSha1 + '.json')
            }
        }

        if ($indexItems.Count -gt $MaxAssetIndexes) { throw "too many distinct asset indexes: $($indexItems.Count)" }
        $indexResults = $indexItems.Values | ForEach-Object -Parallel {
            $item = $_
            $indexLimit = $using:MaxIndexBytes
            if (Test-Path -LiteralPath $item.Path -PathType Leaf) {
                if ((Get-Item -LiteralPath $item.Path).Length -le $indexLimit -and
                    (Get-FileHash -LiteralPath $item.Path -Algorithm SHA1).Hash.ToLowerInvariant() -eq $item.Sha1) {
                    return [pscustomobject]@{ Sha1 = $item.Sha1; Ok = $true; Error = $null }
                }
            }
            $lastError = $null
            for ($attempt = 1; $attempt -le 5; $attempt++) {
                try {
                    $temporary = $item.Path + '.tmp-' + $PID + '-' + [guid]::NewGuid().ToString('N')
                    try {
                        Invoke-WebRequest -Uri $item.Url -OutFile $temporary -MaximumRedirection 0 -TimeoutSec 60 -ErrorAction Stop
                        if ((Get-Item -LiteralPath $temporary).Length -gt $indexLimit) { throw 'asset index too large' }
                        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA1).Hash.ToLowerInvariant() -ne $item.Sha1) { throw 'SHA-1 mismatch' }
                        Move-Item -LiteralPath $temporary -Destination $item.Path -Force
                        return [pscustomobject]@{ Sha1 = $item.Sha1; Ok = $true; Error = $null }
                    }
                    finally {
                        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
                    }
                }
                catch {
                    $lastError = $_.Exception.Message
                    if ($attempt -lt 5) {
                        Start-Sleep -Milliseconds ([Math]::Min(16000, 250 * [Math]::Pow(2, $attempt - 1)) + (Get-Random -Minimum 0 -Maximum 250))
                    }
                }
            }
            return [pscustomobject]@{ Sha1 = $item.Sha1; Ok = $false; Error = $lastError }
        } -ThrottleLimit $FetchConcurrency

        $badIndexes = @($indexResults | Where-Object { -not $_.Ok })
        if ($badIndexes.Count -gt 0) {
            $badIndexes | Select-Object -First 10 | Format-Table -AutoSize
            throw "$($badIndexes.Count) asset index downloads failed after 5 attempts"
        }
        Write-Host "asset indexes fetched/verified=$(@($indexResults).Count)"
    }
    else {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "manifest missing and -SkipFetch was set: $manifestPath" }
        Write-Host 'fetch skipped; using existing cache'
    }

    $builder = Join-Path $repo 'src/Relay.Db/Relay.Db.csproj'
    $verifier = Join-Path $repo 'src/Relay.DbVerify/Relay.DbVerify.csproj'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Write-Host 'building tools'
    & dotnet build $builder -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Relay.Db build failed with exit $LASTEXITCODE" }
    & dotnet build $verifier -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Relay.DbVerify build failed with exit $LASTEXITCODE" }

    $staging = $out + '.staging-' + $PID + '-' + [guid]::NewGuid().ToString('N')
    if (Test-Path -LiteralPath $staging) { throw "staging path already exists: $staging" }
    Write-Host 'generating SQLite shards'
    & dotnet run --project $builder -c Release --no-build -- `
        --meta $meta --indexes $indexes --manifest $manifestPath --out $staging --platforms $Platforms
    if ($LASTEXITCODE -ne 0) { throw "index generation failed with exit $LASTEXITCODE" }

    Write-Host 'verifying SQLite shards'
    & dotnet run --project $verifier -c Release --no-build -- `
        --catalog (Join-Path $staging 'catalog.idx') `
        --launch (Join-Path $staging 'launch.idx') `
        --assets (Join-Path $staging 'assets.idx') `
        --manifest (Join-Path $staging 'index.json')
    if ($LASTEXITCODE -ne 0) { throw "index verification failed with exit $LASTEXITCODE" }

    Start-Sleep -Seconds 2
    $backup = $out + '.old-' + $PID + '-' + [guid]::NewGuid().ToString('N')
    if (Test-Path -LiteralPath $out) { Move-Item -LiteralPath $out -Destination $backup }
    try {
        Move-Item -LiteralPath $staging -Destination $out
    }
    catch {
        if (-not (Test-Path -LiteralPath $out) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $out
        }
        throw
    }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    Write-Host "published $out"
    Write-Host 'done'
}
finally {
    if (Test-Path -LiteralPath $lockDir) { Remove-Item -LiteralPath $lockDir -Recurse -Force -ErrorAction SilentlyContinue }
}









