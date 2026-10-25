param(
    [string]$Site = 'site',
    [string]$Branch = 'site',
    [string]$Remote = 'origin'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$site = if ([IO.Path]::IsPathRooted($Site)) { $Site } else { Join-Path $repo $Site }
if (-not (Test-Path -LiteralPath $site -PathType Container)) {
    throw "site directory missing: $site"
}
if ($Branch -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$' -or $Branch.Contains('..')) {
    throw "unsafe branch name: $Branch"
}

$gitRoot = (& git -C $repo rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) { throw "not inside a git repository: $repo" }
$gitRoot = $gitRoot.Trim()
$siteFull = [IO.Path]::GetFullPath($site).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$gitFull = [IO.Path]::GetFullPath($gitRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$seedFull = [IO.Path]::GetFullPath((Join-Path $repo 'site')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($siteFull -eq $gitFull -or $siteFull -eq $seedFull -or $siteFull -eq [IO.Path]::GetPathRoot($siteFull).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
    throw "refusing to publish repository root, seed or filesystem root: $site"
}
$gitDir = (& git -C $gitRoot rev-parse --absolute-git-dir 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitDir)) { throw "cannot resolve git directory: $gitRoot" }
$gitDir = $gitDir.Trim()
$lockDir = Join-Path $gitDir 'relay-publish.lock'
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
        if (-not $stale) { throw "another page publish is already running: $Branch" }
        Remove-Item -LiteralPath $lockDir -Recurse -Force
    }
}
if (-not $locked) { throw "could not acquire publish lock: $Branch" }
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

$work = Join-Path ([IO.Path]::GetTempPath()) ('relay-pages-' + [guid]::NewGuid().ToString('N'))
$localBranch = 'relay-pages-' + [guid]::NewGuid().ToString('N')
$env:GIT_TERMINAL_PROMPT = '0'

try {
    & git -C $gitRoot worktree add --detach $work
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed with exit $LASTEXITCODE" }

    & git -C $work checkout --orphan $localBranch
    if ($LASTEXITCODE -ne 0) { throw "git checkout --orphan failed with exit $LASTEXITCODE" }

    & git -C $work rm -rf . --ignore-unmatch
    if ($LASTEXITCODE -ne 0) { throw "git rm failed with exit $LASTEXITCODE" }

    Get-ChildItem -LiteralPath $site -Force | Where-Object { $_.Name -ne '.git' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $work -Recurse -Force
    }

    & git -C $work add -A
    if ($LASTEXITCODE -ne 0) { throw "git add failed with exit $LASTEXITCODE" }

    & git -C $work -c user.name='relay-bot' -c user.email='relay-bot@users.noreply.github.com' commit -m 'chore: publish relay index'
    if ($LASTEXITCODE -ne 0) { throw "git commit failed with exit $LASTEXITCODE" }

    & git -C $work push --force $Remote ("HEAD:refs/heads/" + $Branch)
    if ($LASTEXITCODE -ne 0) { throw "git push failed with exit $LASTEXITCODE" }

    Write-Host "published $site to $Remote/$Branch"
}
finally {
    if (Test-Path -LiteralPath $work) {
        & git -C $gitRoot worktree remove --force $work 2>$null
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $lockDir) { Remove-Item -LiteralPath $lockDir -Recurse -Force -ErrorAction SilentlyContinue }
}








