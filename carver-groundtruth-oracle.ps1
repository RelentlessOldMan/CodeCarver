# carver-groundtruth-oracle.ps1
# Ground-truth SOUNDNESS + PRECISION oracle for CodeCarver, driven by the synthetic firmware corpus's
# manifest (make-firmware-corpus.ps1 emits <out>-manifest.json: every func_i's def site + the sites that
# call it). Because the generator KNOWS the true call graph, a carve becomes a pass/fail assertion with no
# compiler: carve to a chosen root, and CodeCarver MUST keep every function transitively reachable from it
# (soundness); anything extra is measured (precision). This is the strongest correctness test we can run
# without a real build, and it works at 100 GB scale (giant register headers carry no call edges, so we
# skip them via -MaxParseBytes to bound memory - correctness of the func chain is unaffected).
#
# Usage:
#   ./carver-groundtruth-oracle.ps1 -Corpus C:\Playground\CodeCompass\.corpus\_death
#   ./carver-groundtruth-oracle.ps1 -Corpus <tree> -Manifest <tree>-manifest.json -Root func_100
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Corpus,
  [string]$Manifest,
  [string]$Root,                     # default: a symbol at the middle of the chain (tests precision, not just soundness)
  [long]$MaxParseBytes = 50000,      # skip big/giant headers (no call edges) so a 100 GB tree fits in memory
  [string]$CliDll
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $CliDll) {
  $CliDll = Join-Path $repoRoot 'src\CodeCarver.Cli\bin\Debug\net8.0\CodeCarver.Cli.dll'
  if (-not (Test-Path $CliDll)) { $CliDll = Join-Path $repoRoot 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll' }
}
if (-not (Test-Path $CliDll)) { throw "CLI not built: dotnet build -c Debug" }
if (-not (Test-Path $Corpus)) { throw "corpus not found: $Corpus" }
if (-not $Manifest) {
  $Manifest = Join-Path (Split-Path (Resolve-Path $Corpus)) ((Split-Path (Resolve-Path $Corpus) -Leaf) + '-manifest.json')
}
if (-not (Test-Path $Manifest)) { throw "manifest not found: $Manifest (generate with make-firmware-corpus.ps1 -Manifest)" }

Write-Host "== parsing ground-truth manifest ==" -ForegroundColor Cyan
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
# Build: symbol -> def-file basename, and the CALL graph (caller -> callees). A ref site of symbol S lives
# in the file of the function that CALLS S; that caller is whichever symbol is DEFINED in that file.
$defBase = @{}          # symbol -> def file basename (e.g. src_42.c)
$defFileToSym = @{}     # normalized def file path -> symbol
foreach ($p in $m.PSObject.Properties) {
  $defFile = ($p.Value.def -replace ':\d+$','')
  $defBase[$p.Name] = [System.IO.Path]::GetFileName($defFile)
  $defFileToSym[$defFile.ToLowerInvariant()] = $p.Name
}
$calls = @{}            # caller symbol -> list of callee symbols
foreach ($p in $m.PSObject.Properties) {
  $callee = $p.Name
  foreach ($r in @($p.Value.refs)) {
    if (-not $r) { continue }
    $rf = ($r -replace ':\d+$','').ToLowerInvariant()
    $caller = $defFileToSym[$rf]
    if ($caller) {
      if (-not $calls.ContainsKey($caller)) { $calls[$caller] = New-Object System.Collections.Generic.List[string] }
      $calls[$caller].Add($callee)
    }
  }
}

# Default root: the middle of the func_ chain (so the expected set is a strict subset -> precision matters).
if (-not $Root) {
  $idxs = @($m.PSObject.Properties.Name | Where-Object { $_ -match '^func_(\d+)$' } | ForEach-Object { [int]($_ -replace 'func_','') })
  $mid = [int](($idxs | Measure-Object -Maximum).Maximum / 2)
  $Root = "func_$mid"
}
if (-not $defBase.ContainsKey($Root)) { throw "root '$Root' not in manifest" }

# Expected reachable set = BFS over the call graph from the root.
$reach = @{}; $q = New-Object System.Collections.Queue; [void]$q.Enqueue($Root); $reach[$Root] = $true
while ($q.Count -gt 0) {
  $c = $q.Dequeue()
  if ($calls.ContainsKey($c)) { foreach ($n in $calls[$c]) { if (-not $reach.ContainsKey($n)) { $reach[$n] = $true; [void]$q.Enqueue($n) } } }
}
$expectedFiles = @{}; foreach ($s in $reach.Keys) { $expectedFiles[$defBase[$s]] = $true }
Write-Host ("root {0} => {1} functions transitively reachable (expect their {2} def-files kept)" -f $Root, $reach.Count, $expectedFiles.Count)

# Run the carve (analysis + CodeCarver manifest listing kept files). No --out: correctness only, no copy.
$ccManifest = Join-Path $env:TEMP ("cc-gt-" + [Guid]::NewGuid().ToString('N').Substring(0,8) + ".json")
Write-Host "== carving (this may take minutes on a 100 GB tree) ==" -ForegroundColor Cyan
# CodeCarver writes progress ('scanning:', 'warn:') to stderr; under -ErrorActionPreference Stop a native
# exe's stderr is turned into a terminating NativeCommandError even on a clean exit. Switch to Continue for
# the invocation and gate on the exit code instead (a known PS 5.1 hazard).
$ErrorActionPreference = 'Continue'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet $CliDll carve $Corpus --roots $Root --lang c --max-parse-bytes $MaxParseBytes --manifest $ccManifest 2>&1 |
  Select-String 'nodes|files|size|scanning|warn' | ForEach-Object { "  " + $_.Line }
$carveExit = $LASTEXITCODE
$sw.Stop()
if ($carveExit -ne 0) { Write-Host "  (carve exit $carveExit)" -ForegroundColor Yellow }
if (-not (Test-Path $ccManifest)) { throw "carve produced no manifest ($ccManifest)" }

$cc = Get-Content $ccManifest -Raw | ConvertFrom-Json
$keptBase = @{}; foreach ($f in $cc.keptFiles) { $keptBase[[System.IO.Path]::GetFileName($f)] = $true }

# SOUNDNESS: every reachable function's def-file must be kept.
$missing = @($expectedFiles.Keys | Where-Object { -not $keptBase.ContainsKey($_) })
# PRECISION: kept src_*.c whose function is NOT reachable = over-keep (headers/blobs ignored - not func nodes).
$overkeep = @($cc.keptFiles | ForEach-Object { [System.IO.Path]::GetFileName($_) } |
              Where-Object { $_ -match '^src_\d+\.c$' -and -not $expectedFiles.ContainsKey($_) })

Write-Host ''
Write-Host '================ GROUND-TRUTH ORACLE ================' -ForegroundColor Cyan
Write-Host ("corpus     : {0}" -f (Resolve-Path $Corpus))
Write-Host ("root       : {0}  ({1} reachable functions)" -f $Root, $reach.Count)
Write-Host ("carve time : {0:N0}s" -f $sw.Elapsed.TotalSeconds)
Write-Host ("kept files : {0}" -f $cc.keptFiles.Count)
if ($missing.Count -eq 0) {
  Write-Host ("SOUNDNESS  : PASS - all {0} reachable def-files kept" -f $expectedFiles.Count) -ForegroundColor Green
} else {
  Write-Host ("SOUNDNESS  : FAIL - {0} reachable def-file(s) DROPPED (unsound carve!):" -f $missing.Count) -ForegroundColor Red
  $missing | Select-Object -First 20 | ForEach-Object { Write-Host "             $_" -ForegroundColor Red }
}
$precisionPct = if (($expectedFiles.Count + $overkeep.Count) -gt 0) { [math]::Round(100.0 * $expectedFiles.Count / ($expectedFiles.Count + $overkeep.Count), 1) } else { 100 }
$precColor = if ($overkeep.Count -eq 0) { 'Green' } else { 'Yellow' }
Write-Host ("PRECISION  : {0} src over-kept beyond the reachable set  (precision {1}%)" -f $overkeep.Count, $precisionPct) -ForegroundColor $precColor
Remove-Item $ccManifest -Force -ErrorAction SilentlyContinue
if ($missing.Count -ne 0) { exit 1 }
