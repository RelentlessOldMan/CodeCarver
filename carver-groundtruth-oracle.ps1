# carver-groundtruth-oracle.ps1
# Ground-truth SOUNDNESS + PRECISION oracle for CodeCarver, driven by a CodeSpawner corpus's v1 manifest
# (the vendored tools\codespawner\codespawner.exe emits <out>-manifest.json: _meta + a `symbols` map whose
# `edges` are the true call graph). Because the generator KNOWS the call graph, a carve becomes a pass/fail
# assertion with no compiler: carve to a chosen root, and CodeCarver MUST keep every function transitively
# reachable from it (soundness); anything extra is measured (precision). This is the strongest correctness
# test we can run without a real build, and it works at 100 GB scale (giant register headers carry no call
# edges, so we skip them via -MaxParseBytes to bound memory - correctness of the func chain is unaffected).
#
# Generate a corpus first (see docs/USAGE.md in the CodeSpawner repo):
#   tools\codespawner\codespawner.exe gen --out <tree> --scale 0.01 --giant-headers 0 --cfiles 30
# Then:
#   ./carver-groundtruth-oracle.ps1 -Corpus <tree>
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

# Assert the contract version BEFORE trusting the manifest. v1 (CodeSpawner) nests symbols under `symbols`,
# carries `_meta`, and emits an EXPLICIT call graph as `edges` (symbol -> [symbols it calls]) - so we no
# longer infer caller from a ref site's file. See tools\codespawner\manifest-schema.md.
# This oracle scopes to the func_i CALL CHAIN (a reachability property). CodeSpawner's later additive v1
# fields are for other consumers and are intentionally IGNORED here: `dupGroups`/`_meta.populations`
# (indexer dedup / shape), and `expectedMiss` symbols (token-paste `handler_##id` names an indexer is
# expected to miss). Confirmed on CodeSpawner v1.0.3: expectedMiss symbols are ISOLATED in the call graph
# (no `edges` in OR out), so they are genuinely unreachable and a carver correctly drops them as dead code --
# a symbol-EXTRACTION concern, not a carve-reachability one, nothing for this oracle to assert. Revisit only
# if a future preset makes them reachable (a paste-CALL from the chain) -- then it'd be a LinkPaste-soundness case.
$ver = $m._meta.manifestVersion
if ($ver -ne 1) { throw "manifest version $ver != 1 - this oracle speaks v1 (regenerate with a v1 CodeSpawner)" }
$syms = $m.symbols
Write-Host ("manifest v{0}, seed {1}, generator {2}" -f $ver, $m._meta.seed, $m._meta.generatorVersion)

# symbol -> def-file basename (the seed-stable identity), and the call graph straight from `edges`.
$defBase  = @{}          # symbol -> def file basename (e.g. src_42.c)
$calls    = @{}          # caller symbol -> list of callee symbols (direct edges)
$indirect = @{}          # symbol -> list of indirectEdge objects {target, via, dispatched, resolved} (v1 corpus)
foreach ($p in $syms.PSObject.Properties) {
  $defFile = ($p.Value.def -replace ':\d+$','')
  $defBase[$p.Name] = [System.IO.Path]::GetFileName($defFile)
  # @($null).Count is 1 for an absent property, so filter to real edge names before counting.
  $edges = @($p.Value.edges) | Where-Object { $_ }
  if ($edges.Count -gt 0) {
    $lst = New-Object System.Collections.Generic.List[string]
    foreach ($e in $edges) { $lst.Add([string]$e) }
    $calls[$p.Name] = $lst
  }
  # v1 corpus (additive, agreed 2026-09-30): per-symbol indirectEdges = fnptr/vector-table/init_array targets.
  # Absent on today's linear-chain manifest, so $indirect stays empty and the closure below is call-graph-only
  # exactly as before -- this parse is forward-compat, not a behavior change until Spawner ships the field.
  $ie = @($p.Value.indirectEdges) | Where-Object { $_ }
  if ($ie.Count -gt 0) {
    $ilst = New-Object System.Collections.Generic.List[object]
    foreach ($e in $ie) { $ilst.Add($e) }
    $indirect[$p.Name] = $ilst
  }
}

# Roots: explicit -Root wins; else the corpus's DECLARED _meta.roots (v1 dial); else the chain-middle default
# (legacy behavior for today's linear manifest, so existing runs stay byte-identical). A root that isn't a
# declared symbol is a HARD error, never a silent skip (agreed 2026-09-30 -- a phantom root shrinks the
# reachable set and desyncs expectedMiss).
$rootList = @()
if ($Root) {
  $rootList = @($Root)
} elseif ($m._meta.roots) {
  $rootList = @($m._meta.roots)
  Write-Host ("using _meta.roots ({0}): {1}" -f $rootList.Count, ($rootList -join ', ')) -ForegroundColor DarkCyan
} else {
  $idxs = @($syms.PSObject.Properties.Name | Where-Object { $_ -match '^func_(\d+)$' } | ForEach-Object { [int]($_ -replace 'func_','') })
  $mid = [int](($idxs | Measure-Object -Maximum).Maximum / 2)
  $rootList = @("func_$mid")
}
foreach ($r in $rootList) { if (-not $defBase.ContainsKey($r)) { throw "root '$r' not in manifest (phantom root)" } }
$Root = $rootList[0]   # single-root label for the summary output below

# Expected reachable set = closure over (edges UNION indirectEdges) from the roots. The union is load-bearing:
# a SOUND carve keeps indirect targets, so a call-graph-only closure would UNDERSTATE the expected set and the
# soundness check could spuriously pass. $indirect is empty on today's manifest -> pure call-graph closure.
$reach = @{}; $q = New-Object System.Collections.Queue
foreach ($r in $rootList) { if (-not $reach.ContainsKey($r)) { $reach[$r] = $true; [void]$q.Enqueue($r) } }
$taxTargets = @{}   # reachable target reached via an indirect edge with dispatched:false = the over-keep tax
while ($q.Count -gt 0) {
  $c = $q.Dequeue()
  if ($calls.ContainsKey($c)) { foreach ($n in $calls[$c]) { if (-not $reach.ContainsKey($n)) { $reach[$n] = $true; [void]$q.Enqueue($n) } } }
  if ($indirect.ContainsKey($c)) {
    foreach ($e in $indirect[$c]) {
      if ($e.resolved -eq $false) { continue }              # external/unresolved = terminal leaf (no outgoing edges)
      $t = [string]$e.target
      if (-not $reach.ContainsKey($t)) { $reach[$t] = $true; [void]$q.Enqueue($t) }
      if ($e.dispatched -eq $false) { $taxTargets[$t] = $true }
    }
  }
}
$expectedFiles = @{}; foreach ($s in $reach.Keys) { $expectedFiles[$defBase[$s]] = $true }
Write-Host ("roots {0} => {1} functions transitively reachable (direct+indirect); expect {2} def-files kept" -f ($rootList -join ','), $reach.Count, $expectedFiles.Count)

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

# ===== v1 CORPUS additions -- INERT until the manifest gains the fields (agreed 3-way 2026-09-30) ==========
# These activate automatically once Spawner ships indirectEdges / _meta.roots / indirectTruthSha; on today's
# linear-chain manifest every guard below is false, so behavior is unchanged. See memory: corpus-carve-oracle-v1-spec.

# (a) SOUNDNESS already covers indirect targets: $reach above is the (edges UNION indirectEdges) closure, and
#     the existing $missing check asserts every reachable def-file is kept -> a sound carve that dropped a
#     fnptr/vector-table/init_array target FAILS here automatically once indirectEdges are present.

# (b) INDIRECTION TAX: reachable + dispatched:false targets = legitimate over-keep a SOUND carve pays (NOT a
#     failure) -- the set a future trace/tightness tier would drop. Reported as a number.
if ($taxTargets.Count -gt 0) {
  Write-Host ("TAX        : {0} reachable address-taken-but-never-dispatched target(s) -- sound carve keeps them (over-keep, not a bug)" -f $taxTargets.Count) -ForegroundColor Cyan
}

# (c) indirectTruthSha -- read-side drift check. Canonical form AGREED (must match the exe's digest-selftest
#     vector byte-for-byte): per edge line `source US target US via US dispatched US resolved` (US=0x1F,
#     bools as 0/1); dedup + ordinal-sort each population; RS(0x1E) between records; sections
#     `<sorted indirectEdges> GS(0x1D) <sorted roots(dedup,ordinal-sort,RS-joined)>`, both always emitted;
#     sha256 hex of that byte string.
if ($m._meta.indirectTruthSha) {
  # TODO(wire when corpus ships): recompute the canonical digest here and assert -eq $m._meta.indirectTruthSha,
  # exit 1 on mismatch. Cross-check the recompute against the exe's shipped selftest golden vector FIRST so
  # the two implementations are proven identical (same gate that locked prevTruthSha 7de5e47).
  Write-Host ("indirectTruthSha present ({0}) -- read-side verify STUB (not yet recomputed)" -f $m._meta.indirectTruthSha) -ForegroundColor DarkYellow
}

# (d) GRADED reduction -- if the corpus declares its intended reachable fraction (the --oracle-reachable-frac
#     dial), report observed vs declared. Field name TBD with Spawner; guarded so it's inert until it exists.
$declaredFrac = $m._meta.reachableFraction   # placeholder name -- confirm with Spawner when the dial ships
if ($null -ne $declaredFrac) {
  $total = @($syms.PSObject.Properties.Name).Count
  $actualFrac = if ($total -gt 0) { [double]$reach.Count / $total } else { 0 }
  Write-Host ("GRADED     : declared reachable-fraction {0:P0}, observed {1:P0}" -f [double]$declaredFrac, $actualFrac) -ForegroundColor Cyan
  # TODO(wire): assert [math]::Abs($actualFrac - $declaredFrac) within an agreed tolerance; exit 1 if outside.
}
# =========================================================================================================

Remove-Item $ccManifest -Force -ErrorAction SilentlyContinue
if ($missing.Count -ne 0) { exit 1 }
