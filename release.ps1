# CodeCarver release packager. Produces a versioned zip of the built CLI -- but ONLY for a commit that
# is already on origin. This makes the classic "we shipped a zip whose commits were never pushed" bug
# structurally impossible: the artifact is stamped with the commit SHA, and the script refuses to zip
# unless (1) the working tree is clean and (2) HEAD is present on origin/<branch>. Zip happens AFTER the
# push is confirmed, never before.
#
#   ./release.ps1                 # verify clean + pushed, test, build, zip  (fails if not pushed)
#   ./release.ps1 -Push           # auto-push HEAD first, then package
#   ./release.ps1 -Version 1.2.0  # label the artifact (default: date+shortsha)
#   ./release.ps1 -SkipTests      # skip the test gate (not recommended for a real release)
param(
    [string]$Version = '',
    [switch]$Push,
    [switch]$SkipTests,
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# NOTE: no 2>&1 -- merging git's normal stderr progress into the success stream would, under
# $ErrorActionPreference='Stop', turn it into a terminating error even on exit 0. Let stderr print;
# gate purely on $LASTEXITCODE.
function RunGit { param([Parameter(ValueFromRemainingArguments=$true)][string[]]$a) $o = & git @a; if ($LASTEXITCODE -ne 0) { throw "git $($a -join ' ') failed" }; return $o }

# --- 1. clean working tree (else the zip wouldn't correspond to ANY commit) ---
$dirty = & git status --porcelain
if ($dirty) {
    Write-Host "REFUSING: working tree has uncommitted changes -- commit or stash first:" -ForegroundColor Red
    $dirty | ForEach-Object { "    $_" }
    exit 1
}

$branch = (RunGit rev-parse --abbrev-ref HEAD).Trim()
$sha    = (RunGit rev-parse HEAD).Trim()
$short  = $sha.Substring(0,9)

# --- 2. ensure HEAD is on origin (the whole point) ---
RunGit fetch origin $branch | Out-Null
$onOrigin = & git branch -r --contains $sha 2>$null | Where-Object { $_ -match "origin/$([regex]::Escape($branch))\b" }
if (-not $onOrigin) {
    if ($Push) {
        Write-Host "HEAD not on origin/$branch -- pushing first..." -ForegroundColor Yellow
        RunGit push origin $branch | Out-Null
        RunGit fetch origin $branch | Out-Null
        $onOrigin = & git branch -r --contains $sha 2>$null | Where-Object { $_ -match "origin/$([regex]::Escape($branch))\b" }
    }
    if (-not $onOrigin) {
        Write-Host "REFUSING: HEAD ($short) is NOT on origin/$branch -- its commits are unpushed." -ForegroundColor Red
        Write-Host "  A zip of this build would ship code that isn't in the remote. Push it first:" -ForegroundColor Red
        Write-Host "    git push origin $branch      (or re-run: ./release.ps1 -Push)" -ForegroundColor Red
        exit 1
    }
}
Write-Host "OK: $short is on origin/$branch -- safe to package." -ForegroundColor Green

# --- 3. test gate (a release should be green) ---
if (-not $SkipTests) {
    Write-Host "== Test gate ==" -ForegroundColor Cyan
    & (Join-Path $root 'check.ps1')
    if ($LASTEXITCODE -ne 0) { throw "tests failed -- not packaging" }
} else { Write-Host "(skipping tests -- -SkipTests)" -ForegroundColor DarkGray }

# --- 4. build + publish the CLI ---
if (-not $Version) { $Version = (Get-Date -Format 'yyyyMMdd') + "-$short" }
$pub = Join-Path $env:TEMP "cc-publish-$short"
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
Write-Host "== Publishing CLI (Release) ==" -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\CodeCarver.Cli\CodeCarver.Cli.csproj') -c Release -o $pub --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# Stamp the exact commit into the artifact so a loose zip is always traceable to a pushed commit.
"CodeCarver $Version`ncommit $sha`nbranch $branch`nbuilt  $(Get-Date -Format o)" |
    Set-Content -Encoding UTF8 (Join-Path $pub 'RELEASE.txt')

# --- 5. zip (only reached AFTER push is confirmed) ---
if (-not $Output) { $Output = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $Output | Out-Null
$zip = Join-Path $Output "codecarver-$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip
Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue

Write-Host "`nPACKAGED: $zip" -ForegroundColor Green
Write-Host "  corresponds to pushed commit $sha on origin/$branch" -ForegroundColor Green
Write-Host "  (RELEASE.txt inside the zip records the same SHA)"
