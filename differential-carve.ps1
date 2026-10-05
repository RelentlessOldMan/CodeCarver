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
. (Join-Path $PSScriptRoot 'tools\CarverScriptLib.ps1')
$cli = Resolve-CarverDll -RepoRoot $PSScriptRoot
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
    $lines += 'outputDirectory = ' + (ConvertTo-TomlPath $out)
    $lines += '[common]'
    $lines += 'entryPoints = ' + (ConvertTo-TomlArray $Roots)
    $lines += 'languages = ' + (ConvertTo-TomlArray $Lang)
    if ($Exclude)      { $lines += 'excludeDirectories = ' + (ConvertTo-TomlArray $Exclude) }
    if ($Prune)        { $lines += 'carveSourceFileContents = true' }
    if ($PruneHeaders) { $lines += 'carveHeaderFileContents = true' }
    if ($BuildLog -or $Defines) {
        $lines += '[builds.main]'
        if ($BuildLog) { $lines += 'buildLogs = [' + (ConvertTo-TomlPath $BuildLog) + ']' }
        if ($Defines)  { $lines += 'defines = ' + (ConvertTo-TomlArray $Defines) }
    }
    $lines += '[advanced]'; $lines += "parseTimeout = $ParseTimeout"   # 0 = disabled (isolate path from timing)
    $cfgPath = "$out.toml"
    ($lines -join "`n") | Set-Content -Encoding utf8 $cfgPath
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # stderr carries progress/warnings: under 'Stop', PowerShell 5.1 turns a native stderr line merged by
    # 2>&1 into a terminating error even on exit 0 (review SC-D2). Continue for the call; gate on exit code.
    $ErrorActionPreference = 'Continue'
    & dotnet $cli carve $repo --config $cfgPath 2>&1 | Select-String 'nodes|files|size|verify|warn|world|error' | ForEach-Object { Write-Host "    $_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $sw.Stop()
    Remove-Item $cfgPath -Force -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "carve of $repo failed (exit $code)" }
    return $sw.Elapsed.TotalSeconds
}

Write-Host "== Carve A: $RepoA ==" -ForegroundColor Cyan
$tA = CarveTo $RepoA $oA
Write-Host "== Carve B: $RepoB ==" -ForegroundColor Cyan
$tB = CarveTo $RepoB $oB
Write-Host ("`nwall-clock:  A = {0:N1}s   B = {1:N1}s   (B/A = {2:N2}x)" -f $tA, $tB, ($tB / [Math]::Max($tA, 0.001)))

# --- 1. manifest decisions (ignore the absolute `root` line) ---
# ORDER matters (review SC-D8): Compare-Object treats the lines as a set, so a reordered keptFiles list -
# exactly the nondeterminism this script exists to catch - compared equal. Hash the ordered text instead.
if (-not (Test-Path $mA) -or -not (Test-Path $mB)) { throw "carve produced no manifest ($mA / $mB)" }
$linesA = @((Get-Content $mA) | Where-Object { $_ -notmatch '^\s*"root":' })
$linesB = @((Get-Content $mB) | Where-Object { $_ -notmatch '^\s*"root":' })
function TextHash([string[]]$lines) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes))) -replace '-', ''
}
$fail = 0
if ((TextHash $linesA) -ne (TextHash $linesB)) {
    Write-Host "`n!!! MANIFEST DECISIONS DIFFER (source location or run order changed the carve) -- BUG:" -ForegroundColor Red
    $n = [Math]::Max($linesA.Count, $linesB.Count); $shown = 0
    for ($i = 0; $i -lt $n -and $shown -lt 20; $i++) {
        $a = if ($i -lt $linesA.Count) { $linesA[$i] } else { '<end>' }
        $b = if ($i -lt $linesB.Count) { $linesB[$i] } else { '<end>' }
        if ($a -cne $b) { Write-Host ("    line {0}:`n      A: {1}`n      B: {2}" -f ($i + 1), $a, $b); $shown++ }
    }
    $fail++
} else {
    Write-Host "`nOK: manifest decisions identical, in the same order (roots, defines, stats, kept/dropped file lists)." -ForegroundColor Green
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
