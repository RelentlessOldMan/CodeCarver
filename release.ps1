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
# Exact ref match: `origin/main\b` also matched origin/main-x (and origin/main.bak), so a commit pushed
# only to a sibling branch passed the check (review SC-F2). `git branch -r` lines are "  origin/<name>".
$originRe = '^\s*origin/' + [regex]::Escape($branch) + '\s*$'
$onOrigin = & git branch -r --contains $sha 2>$null | Where-Object { $_ -match $originRe }
if (-not $onOrigin) {
    if ($Push) {
        Write-Host "HEAD not on origin/$branch -- pushing first..." -ForegroundColor Yellow
        RunGit push origin $branch | Out-Null
        RunGit fetch origin $branch | Out-Null
        $onOrigin = & git branch -r --contains $sha 2>$null | Where-Object { $_ -match $originRe }
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
    # Test the configuration that ships (Release); the gate used to test Debug and ship Release.
    Write-Host "== Test gate (Release) ==" -ForegroundColor Cyan
    & (Join-Path $root 'check.ps1') -Configuration Release
    if (-not $?) { throw "tests failed -- not packaging" }
} else { Write-Host "(skipping tests -- -SkipTests)" -ForegroundColor DarkGray }

# --- 4. build + publish the CLI ---
if (-not $Version) { $Version = (Get-Date -Format 'yyyyMMdd') + "-$short" }
$pub = Join-Path $env:TEMP "cc-publish-$short"
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
Write-Host "== Publishing CLI (Release) ==" -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\CodeCarver.Cli\CodeCarver.Cli.csproj') -c Release -o $pub --nologo -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# TreeSitter.DotNet ships ~30 grammars per platform; CodeCarver loads only the core library and the C, C++
# and C# grammars. Ship exactly those, so the release carries no unused native code and THIRD-PARTY-NOTICES.txt
# covers everything in it.
$keepNative = '^(lib)?tree-sitter(-c|-cpp|-c-sharp)?\.(dll|so|dylib)$'
$pruned = 0
Get-ChildItem (Join-Path $pub 'runtimes') -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'native' -and $_.Name -notmatch $keepNative } |
    ForEach-Object { Remove-Item -Force $_.FullName; $pruned++ }
foreach ($rid in @('win-x64', 'linux-x64')) {
    $n = @(Get-ChildItem (Join-Path $pub "runtimes\$rid\native") -File -ErrorAction SilentlyContinue).Count
    if ($n -ne 4) { throw "expected 4 tree-sitter libraries for $rid after pruning, found $n" }
}
Write-Host "  pruned $pruned unused tree-sitter grammar libraries (kept core + c, cpp, c-sharp)" -ForegroundColor DarkGray
# Stamp the exact commit into the artifact so a loose zip is always traceable to a pushed commit.
"CodeCarver $Version`ncommit $sha`nbranch $branch`nbuilt  $(Get-Date -Format o)" |
    Set-Content -Encoding UTF8 (Join-Path $pub 'RELEASE.txt')

# Ship the user-facing docs INSIDE the release, not just in the dev repo. A user who hits a problem needs
# SUPPORT.md (how to run --diag, what the package does/doesn't contain) and USAGE.md at hand; both are also
# surfaced at the zip root so they're impossible to miss. ALLOWLIST, not the whole folder: the internal
# runbooks and review notes (REVIEW-HANDOFF, SHAKEDOWN, WORKREPO, REPRODUCE, TESTING) describe the dev repo's
# own scripts and must not ship (review SC-F2). Add a doc here deliberately when it is user-facing.
$shipDocs = @('USAGE.md', 'SUPPORT.md', 'ADAPTING.md', 'TOOLING.md')
$docsSrc = Join-Path $root 'docs'
New-Item -ItemType Directory -Force (Join-Path $pub 'docs') | Out-Null
foreach ($d in $shipDocs) {
    $p = Join-Path $docsSrc $d
    if (-not (Test-Path $p)) { throw "release doc missing: docs\$d" }
    Copy-Item -Force $p (Join-Path $pub "docs\$d")
}
foreach ($top in @('SUPPORT.md', 'USAGE.md')) { Copy-Item -Force (Join-Path $docsSrc $top) (Join-Path $pub $top) }
Write-Host "  bundled docs/ ($($shipDocs -join ', ')) + SUPPORT.md/USAGE.md at the root" -ForegroundColor DarkGray
# The repo README and LICENSE live at the root. A release without its licence is not redistributable.
foreach ($top in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
    $p = Join-Path $root $top
    if (-not (Test-Path $p)) { throw "release file missing: $top" }
    Copy-Item -Force $p (Join-Path $pub $top)
}
# USAGE.md tells users to run the capture scripts, so they ship too (review SC-C2).
$capture = Join-Path $root 'tools\capture'
if (-not (Test-Path $capture)) { throw "tools\capture missing - USAGE.md tells users to run it" }
New-Item -ItemType Directory -Force (Join-Path $pub 'tools') | Out-Null
Copy-Item -Recurse -Force $capture (Join-Path $pub 'tools\capture')
Write-Host "  bundled README.md, LICENSE, THIRD-PARTY-NOTICES.txt, tools/capture/ into the release" -ForegroundColor DarkGray

# --- 5. zip (only reached AFTER push is confirmed) ---
if (-not $Output) { $Output = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $Output | Out-Null
$zip = Join-Path $Output "codecarver-$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# Not Compress-Archive: on Windows PowerShell 5.1 it writes backslash entry names, which several Linux
# extractors turn into flat files (the native tree-sitter libraries are then not found).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Not ZipFile.CreateFromDirectory either: under Windows PowerShell 5.1 it still writes '\' entry names. Add each
# file with an explicit '/'-separated name instead.
$pubRoot = (Resolve-Path $pub).ProviderPath.TrimEnd('\') + '\'
$zw = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem $pub -Recurse -File) {
        $entry = $f.FullName.Substring($pubRoot.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zw, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zw.Dispose() }
# Verify, never assume: no entry may carry a '\'.
$za = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $bad = @($za.Entries | Where-Object { $_.FullName.Contains('\') } | Select-Object -First 3 | ForEach-Object { $_.FullName }) }
finally { $za.Dispose() }
if ($bad.Count -gt 0) { Remove-Item -Force $zip; throw "zip has backslash entry names (e.g. $($bad -join ', ')) - not packaging" }
Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue

Write-Host "`nPACKAGED: $zip" -ForegroundColor Green
Write-Host "  corresponds to pushed commit $sha on origin/$branch" -ForegroundColor Green
Write-Host "  (RELEASE.txt inside the zip records the same SHA)"
