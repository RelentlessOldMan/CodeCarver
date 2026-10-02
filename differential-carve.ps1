# Differential carve: run the SAME repo from two locations (e.g. local disk vs a network file share)
# and assert the carve DECISIONS are byte-identical. The source location must not change what gets kept
# or dropped; if local and network disagree, that is a real bug -- path normalization (UNC \\server\..
# vs C:\.., backslash/forward-slash, >260-char long paths, case-folding) or nondeterministic ordering.
# Also times both runs so you can see the network I/O cost on a big tree (5k .c + multi-GB headers).
#
#   # two locations, same repo:
#   ./differential-carve.ps1 -RepoA C:\work\firmware -RepoB \\server\share\firmware -Roots main,Reset_Handler
#   # determinism only (run the SAME path twice): omit -RepoB
#   ./differential-carve.ps1 -RepoA C:\work\firmware -Roots main,Reset_Handler -Prune
#
# What must match: everything in the manifest EXCEPT the absolute `root` line (roots, lang, defines,
# stats = node/file counts + bytes, keptFiles, droppedFiles) AND every emitted file, byte-for-byte.
param(
    [Parameter(Mandatory=$true)][string]$RepoA,
    [string]$RepoB = '',
    [Parameter(Mandatory=$true)][string]$Roots,
    [string]$Lang = 'c',
    [string]$Exclude = '',
    [switch]$Prune,
    [switch]$PruneHeaders,
    [string]$BuildLog = '',
    [string]$Defines = '',
    # Parse budget (seconds) forwarded to --parse-timeout. Default 0 = DISABLED for the differential:
    # the budget is a wall-clock decision (a big file kept-whole if its parse blows the budget), so a
    # slower network share could keep-whole a file it carved locally -- a timing difference, not a path
    # bug. Disabling it isolates path-handling/determinism from I/O speed. Set >0 to test with a budget.
    [int]$ParseTimeout = 0
)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release  (missing $cli)" }
if (-not $RepoB) { $RepoB = $RepoA; Write-Host "(determinism mode: carving $RepoA twice)" -ForegroundColor DarkGray }

$work = Join-Path $env:TEMP ("cc-diff-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$oA = Join-Path $work 'A'; $oB = Join-Path $work 'B'
New-Item -ItemType Directory -Force $oA, $oB | Out-Null
# Manifests land under the fixed codecarver/ layout (no stages => directly under the output dir).
$mA = Join-Path $oA 'codecarver\manifest.json'; $mB = Join-Path $oB 'codecarver\manifest.json'

function CarveTo([string]$repo, [string]$out) {
    # Everything goes in a per-call TOML config. The carved tree lands at <out>/carved, the manifest at
    # <out>/codecarver/manifest.json.
    $lines = @()
    $lines += 'outputDirectory = "' + ($out -replace '\\','/') + '"'
    $lines += '[common]'
    $lines += 'entryPoints = ["' + (($Roots -split ',') -join '","') + '"]'
    $lines += 'languages = ["' + $Lang + '"]'
    if ($Exclude)      { $lines += 'excludeDirectories = ["' + (($Exclude -split ',') -join '","') + '"]' }
    if ($Prune)        { $lines += 'carveSourceFileContents = true' }
    if ($PruneHeaders) { $lines += 'carveHeaderFileContents = true' }
    if ($BuildLog -or $Defines) {
        $lines += '[builds.main]'
        if ($BuildLog) { $lines += 'buildLogs = ["' + ($BuildLog -replace '\\','/') + '"]' }
        if ($Defines)  { $lines += 'defines = ["' + (($Defines -split ',') -join '","') + '"]' }
    }
    $lines += '[advanced]'; $lines += "parseTimeout = $ParseTimeout"   # 0 = disabled (isolate path from timing)
    $cfgPath = "$out.toml"
    ($lines -join "`n") | Set-Content -Encoding utf8 $cfgPath
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet $cli carve $repo --config $cfgPath 2>&1 | Select-String 'nodes|files|size|verify|warn|world' | ForEach-Object { Write-Host "    $_" }
    $sw.Stop()
    Remove-Item $cfgPath -Force -ErrorAction SilentlyContinue
    return $sw.Elapsed.TotalSeconds
}

Write-Host "== Carve A: $RepoA ==" -ForegroundColor Cyan
$tA = CarveTo $RepoA $oA
Write-Host "== Carve B: $RepoB ==" -ForegroundColor Cyan
$tB = CarveTo $RepoB $oB
Write-Host ("`nwall-clock:  A = {0:N1}s   B = {1:N1}s   (B/A = {2:N2}x)" -f $tA, $tB, ($tB / [Math]::Max($tA, 0.001)))

# --- 1. manifest decisions (ignore the absolute `root` line) ---
$linesA = (Get-Content $mA) | Where-Object { $_ -notmatch '^\s*"root":' }
$linesB = (Get-Content $mB) | Where-Object { $_ -notmatch '^\s*"root":' }
$mdiff = Compare-Object $linesA $linesB
$fail = 0
if ($mdiff) {
    Write-Host "`n!!! MANIFEST DECISIONS DIFFER (source location changed the carve) -- BUG:" -ForegroundColor Red
    $mdiff | Select-Object -First 40 | ForEach-Object { "    {0} {1}" -f $_.SideIndicator, $_.InputObject }
    $fail++
} else {
    Write-Host "`nOK: manifest decisions identical (roots, defines, stats, kept/dropped file sets)." -ForegroundColor Green
}

# --- 2. emitted trees byte-for-byte ---
function TreeHashes([string]$root) {
    $h = @{}
    Get-ChildItem -Recurse -File $root | Where-Object { $_.Name -ne '.codecarver-output' } | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\','/').Replace('\','/')
        $h[$rel] = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash
    }
    return $h
}
# Compare the carved trees only (the codecarver/ metadata legitimately differs by path: manifest 'root',
# resolved-config outputDirectory).
$hA = TreeHashes (Join-Path $oA 'carved'); $hB = TreeHashes (Join-Path $oB 'carved')
$onlyA = $hA.Keys | Where-Object { -not $hB.ContainsKey($_) }
$onlyB = $hB.Keys | Where-Object { -not $hA.ContainsKey($_) }
$diffContent = $hA.Keys | Where-Object { $hB.ContainsKey($_) -and $hA[$_] -ne $hB[$_] }
if ($onlyA -or $onlyB -or $diffContent) {
    Write-Host "!!! EMITTED TREES DIFFER -- BUG:" -ForegroundColor Red
    $onlyA | ForEach-Object { "    only in A: $_" }
    $onlyB | ForEach-Object { "    only in B: $_" }
    $diffContent | ForEach-Object { "    content differs: $_" }
    $fail++
} else {
    Write-Host ("OK: {0} emitted files byte-identical across both locations." -f $hA.Count) -ForegroundColor Green
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
if ($fail -eq 0) { Write-Host "`nDIFFERENTIAL PASS: the carve is location-independent and deterministic." -ForegroundColor Green }
else { Write-Host "`nDIFFERENTIAL FAIL: $fail category(ies) above -- capture and file (this is a real bug)." -ForegroundColor Red; exit 1 }
