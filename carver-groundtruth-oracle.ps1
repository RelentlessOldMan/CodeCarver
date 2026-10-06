# carver-groundtruth-oracle.ps1
# Ground-truth SOUNDNESS + PRECISION + GRADED-reduction oracle for CodeCarver, driven by a CodeSpawner corpus's
# v1 manifest (<out>-manifest.json: _meta + a `symbols` map whose `edges` are the true call graph, plus the
# v1 additions `_meta.roots`, per-symbol `indirectEdges`, and `_meta.indirectTruthSha`). Because the generator
# KNOWS the call graph, a carve becomes a pass/fail assertion with no compiler: carve to the declared root(s),
# and CodeCarver MUST keep every function transitively reachable over (edges UNION indirectEdges) -- SOUNDNESS;
# anything extra is measured -- PRECISION; and the reachable fraction must land on the --oracle-reachable-frac
# dial -- GRADED. This is the strongest correctness test we can run without a real build, and it scales to
# 100 GB (giant register headers carry no call edges, so we skip them via -MaxParseBytes to bound memory).
#
# The correctness corpora live in TestHole (small, ~200 KB each), reproducible from their recipes there.
# -Corpus may be a full path, or a name resolved under $env:CODECARVER_TESTHOLE (local dir or share):
#   ./carver-groundtruth-oracle.ps1 -Corpus carve_r25                      # $env:CODECARVER_TESTHOLE\carve_r25
#   ./carver-groundtruth-oracle.ps1 -Corpus C:\path\to\TestHole\carve_r75
#   ./carver-groundtruth-oracle.ps1 -Corpus <tree> -Root func_0,func_7 -ExpectedReachableFrac 0.5
# NOTE: these graded corpora need CodeSpawner >= 1.0.9 to (re)generate (the --oracle-* knobs, indirectEdges,
# reachable-frac dial, indirectTruthSha). The exe vendored in this repo (tools\codespawner) is 1.0.9; the
# indirectTruthSha read-side verify self-tests against the shipped golden vector before trusting a recompute.
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Corpus,
  [string]$Manifest,
  [string]$Root,                     # comma list; default: the corpus's _meta.roots; else chain-middle (legacy)
  [long]$MaxParseBytes = 50000,      # skip big/giant headers (no call edges) so a 100 GB tree fits in memory
  [double]$ExpectedReachableFrac = -1, # graded-dial target; <0 = auto-derive from a carve_r<NN> corpus name
  [double]$GradedTolerance = 0.10,   # |observed - expected| allowed (dead subgraphs quantize the dial ~+/-0.06)
  [string]$CliDll,
  # -TraceBuild: a REAL clean gcc build (WSL) of every reachable file plus -CompileUnreachableFrac of the others,
  # captured with tools/capture; its build log + trace (pathMap'd from the WSL path) go into the carve. The rest of
  # the tree is never compiled, like another target's files. Asserts the usual soundness/precision plus: no file
  # the build never compiled is kept.
  [switch]$TraceBuild,
  [double]$CompileUnreachableFrac = 0.5,
  [string[]]$Advanced = @()            # extra [advanced] lines, e.g. 'placeholderFiles = false' (proves a check has teeth)
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

. (Join-Path $repoRoot 'tools\CarverScriptLib.ps1')
# No -CliDll: build Release incrementally and refuse a DLL not stamped from HEAD (review SC-D1).
if (-not $CliDll) { $CliDll = Resolve-CarverDll -RepoRoot $repoRoot }
if (-not (Test-Path $CliDll)) { throw "CLI dll not found: $CliDll" }
if (-not (Test-Path $Corpus) -and $env:CODECARVER_TESTHOLE -and -not [IO.Path]::IsPathRooted($Corpus)) {
  $Corpus = Join-Path $env:CODECARVER_TESTHOLE $Corpus
}
if (-not (Test-Path $Corpus)) { throw "corpus not found: $Corpus (pass a path, or a name under `$env:CODECARVER_TESTHOLE)" }
$corpusFull = (Resolve-Path $Corpus).ProviderPath.TrimEnd('\', '/')   # ProviderPath: no provider prefix on UNC

# Comparison key for a corpus file (review SC-D6): the path RELATIVE to the corpus root, '/'-separated,
# case-folded - never the basename, which collides across directories (block1/src_42.c vs block2/src_42.c).
function RelKey([string]$p) {
  $q = $p -replace '\\', '/'
  if ([IO.Path]::IsPathRooted($p)) {
    $full = ([IO.Path]::GetFullPath($p)) -replace '\\', '/'
    $base = ($corpusFull -replace '\\', '/').TrimEnd('/') + '/'
    if ($full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { $q = $full.Substring($base.Length) } else { $q = $full }
  }
  return ($q -replace '^\./', '').ToLowerInvariant()
}
if (-not $Manifest) {
  $Manifest = Join-Path (Split-Path $corpusFull) ((Split-Path $corpusFull -Leaf) + '-manifest.json')
}
if (-not (Test-Path $Manifest)) { throw "manifest not found: $Manifest (CodeSpawner writes <corpus>-manifest.json beside the corpus: tools\codespawner\codespawner.exe gen)" }

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

# symbol -> def-file relative path (the seed-stable identity), and the call graph straight from `edges`.
$defBase  = @{}          # symbol -> def file key, relative to the corpus root (e.g. block3/sub0/src_42.c)
$calls    = @{}          # caller symbol -> list of callee symbols (direct edges)
$indirect = @{}          # symbol -> list of indirectEdge objects {target, via, dispatched, resolved} (v1 corpus)
foreach ($p in $syms.PSObject.Properties) {
  $defFile = ($p.Value.def -replace ':\d+$','')
  $defBase[$p.Name] = RelKey $defFile
  # @($null).Count is 1 for an absent property, so filter to real edge names before counting.
  $edges = @($p.Value.edges | Where-Object { $_ })   # wrap the PIPELINE OUTPUT: `@($x)|?{}` unwraps a lone hit
  if ($edges.Count -gt 0) {                          # back to a scalar (null .Count) -> the edge silently drops.
    $lst = New-Object System.Collections.Generic.List[string]
    foreach ($e in $edges) { $lst.Add([string]$e) }
    $calls[$p.Name] = $lst
  }
  # v1 corpus (additive, agreed 2026-09-30): per-symbol indirectEdges = fnptr/vector-table/init_array targets.
  # Absent on today's linear-chain manifest, so $indirect stays empty and the closure below is call-graph-only
  # exactly as before -- this parse is forward-compat, not a behavior change until Spawner ships the field.
  $ie = @($p.Value.indirectEdges | Where-Object { $_ })   # same @() gotcha: every symbol here has exactly ONE
  if ($ie.Count -gt 0) {                                   # indirect edge, so the lone-hit unwrap dropped ALL of them.
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
  $rootList = @($Root -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
} elseif ($m._meta.roots) {
  $rootList = @($m._meta.roots)
  Write-Host ("using _meta.roots ({0}): {1}" -f $rootList.Count, ($rootList -join ', ')) -ForegroundColor DarkCyan
} else {
  $idxs = @($syms.PSObject.Properties.Name | Where-Object { $_ -match '^func_(\d+)$' } | ForEach-Object { [int]($_ -replace 'func_','') })
  $mid = [int](($idxs | Measure-Object -Maximum).Maximum / 2)
  $rootList = @("func_$mid")
}
foreach ($r in $rootList) { if (-not $defBase.ContainsKey($r)) { throw "root '$r' not in manifest (phantom root)" } }
$Root = $rootList -join ','   # label for the summary output below; the carve gets EVERY root (review SC-D5)

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
# Direct-only closure = the reachable CORE the --oracle-reachable-frac dial targets. Per manifest-schema.md the
# dial fraction is |BFS(roots) over EDGES| / |symbols| -- indirect targets are a separate SOUNDNESS concern, not
# part of the dial. (Spawner's cited dial values 0.246/0.478/0.696 are these direct-only fractions.) Kept apart
# from $reach so GRADED asserts the dial while SOUNDNESS/TAX use the wider edges-UNION-indirect set above.
$reachDirect = @{}; $qd = New-Object System.Collections.Queue
foreach ($r in $rootList) { if (-not $reachDirect.ContainsKey($r)) { $reachDirect[$r] = $true; [void]$qd.Enqueue($r) } }
while ($qd.Count -gt 0) { $c = $qd.Dequeue(); if ($calls.ContainsKey($c)) { foreach ($n in $calls[$c]) { if (-not $reachDirect.ContainsKey($n)) { $reachDirect[$n] = $true; [void]$qd.Enqueue($n) } } } }

$expectedFiles = @{}; foreach ($s in $reach.Keys) { $expectedFiles[$defBase[$s]] = $true }
Write-Host ("roots {0} => {1} functions transitively reachable (direct+indirect); expect {2} def-files kept" -f ($rootList -join ','), $reach.Count, $expectedFiles.Count)

$traceExtra = @()
$compiled = @{}
if ($TraceBuild) {
  function ToWsl([string]$p) { $f = [IO.Path]::GetFullPath($p); '/mnt/' + $f.Substring(0, 1).ToLowerInvariant() + ($f.Substring(2) -replace '\\', '/') }
  # Every .c: the reachable ones are compiled; of the rest, a fixed (seeded) fraction is compiled (built but never
  # called, which static reachability must still drop) and the others never are.
  $allC = @(Get-ChildItem $corpusFull -Recurse -File -Filter *.c | ForEach-Object { $_.FullName.Substring($corpusFull.Length + 1) -replace '\\', '/' } | Sort-Object)
  # A real build links what it compiles, so the compiled set is closed over calls: seed it with the reachable files
  # plus a seeded fraction of the others, then add the file of every function a compiled file's functions call.
  $rng = New-Object System.Random 1337
  foreach ($rel in $allC) {
    $k = $rel.ToLowerInvariant()
    if ($expectedFiles.ContainsKey($k) -or $rng.NextDouble() -lt $CompileUnreachableFrac) { $compiled[$k] = $true }
  }
  $symsByFile = @{}
  foreach ($s in $defBase.Keys) { $f = $defBase[$s]; if (-not $symsByFile.ContainsKey($f)) { $symsByFile[$f] = New-Object System.Collections.Generic.List[string] }; $symsByFile[$f].Add($s) }
  $work = New-Object System.Collections.Queue
  foreach ($k in @($compiled.Keys)) { [void]$work.Enqueue($k) }
  while ($work.Count -gt 0) {
    $f = $work.Dequeue()
    if (-not $symsByFile.ContainsKey($f)) { continue }
    foreach ($s in $symsByFile[$f]) {
      $callees = @(); if ($calls.ContainsKey($s)) { $callees += $calls[$s] }
      if ($indirect.ContainsKey($s)) { $callees += @($indirect[$s] | Where-Object { $_.resolved -ne $false } | ForEach-Object { [string]$_.target }) }
      foreach ($t in $callees) {
        $tf = $defBase[$t]
        if ($tf -and -not $compiled.ContainsKey($tf)) { $compiled[$tf] = $true; [void]$work.Enqueue($tf) }
      }
    }
  }
  $list = @($allC | Where-Object { $compiled.ContainsKey($_.ToLowerInvariant()) })
  $traceDir = Join-Path $env:TEMP ("cc-tb-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Force $traceDir | Out-Null
  [IO.File]::WriteAllText((Join-Path $traceDir 'list.txt'), (($list -join "`n") + "`n"))
  Write-Host ("== clean build (WSL gcc, traced): {0} of {1} .c files ==" -f @($list).Count, $allC.Count) -ForegroundColor Cyan
  $corpusWsl = ToWsl $corpusFull
  & wsl -d Ubuntu -- bash (ToWsl (Join-Path $repoRoot 'tools\trace-oracle\wsl-trace-build.sh')) trace $corpusWsl (ToWsl (Join-Path $traceDir 'list.txt')) (ToWsl $traceDir) (ToWsl (Join-Path $repoRoot 'tools\capture\capture-file-trace.sh'))
  if ($LASTEXITCODE -ne 0) { throw "traced build failed (exit $LASTEXITCODE) - see $traceDir\build.log" }
  foreach ($bad in @(Get-Content (Join-Path $traceDir 'failed.txt') | Where-Object { $_ })) {
    $k = $bad.Trim().ToLowerInvariant()
    if ($expectedFiles.ContainsKey($k)) { throw "reachable file does not compile, so no build could contain it: $bad" }
    $compiled.Remove($k)
    Write-Host "  not compilable (left out of the build): $bad" -ForegroundColor DarkGray
  }
  $traceExtra = @(
    '[builds.main]'
    "buildLogs = [$(ConvertTo-TomlPath (Join-Path $traceDir 'build.log'))]"
    "buildTraceFiles = [$(ConvertTo-TomlPath (Join-Path $traceDir 'build.trace'))]")
  $pathMapLine = "pathMap = [{ from = `"$corpusWsl`", to = `".`" }]"
}

# Run the carve (analysis + CodeCarver manifest listing kept files). No --out: correctness only, no copy.
# analysisOnly => plan + manifest only (no multi-GB emit). Manifest lands under codecarver/.
$ccOut = Join-Path $env:TEMP ("cc-gt-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$ccManifest = Join-Path $ccOut 'codecarver\manifest.json'
$gtCfg = "$ccOut.toml"
# entryPoints = the SAME root list the expected set was computed from (it used to be only the first root,
# so a multi-root corpus expected more than the carve was asked for).
@(
  "outputDirectory = $(ConvertTo-TomlPath $ccOut)"
  $(if ($TraceBuild) { 'analysisOnly = false' } else { 'analysisOnly = true' })   # -TraceBuild links the carved tree
  '[common]'
  "entryPoints = $(ConvertTo-TomlArray $rootList)"
  'languages = ["c"]'
  '[advanced]'
  "maxParseBytes = $MaxParseBytes"
  $(if ($TraceBuild) { $pathMapLine })
  $Advanced
  $traceExtra
) -join "`n" | Set-Content -Encoding utf8 $gtCfg
Write-Host "== carving (this may take minutes on a 100 GB tree) ==" -ForegroundColor Cyan
# CodeCarver writes progress ('scanning:', 'warn:') to stderr; under -ErrorActionPreference Stop a native
# exe's stderr is turned into a terminating NativeCommandError even on a clean exit. Switch to Continue for
# the invocation and gate on the exit code instead (a known PS 5.1 hazard).
$ErrorActionPreference = 'Continue'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$carveOut = @(& dotnet $CliDll carve $corpusFull --config $gtCfg 2>&1 | ForEach-Object { "$_" })
$carveExit = $LASTEXITCODE
$carveOut | Select-String 'nodes|files|size|scanning|warn|mode|world|trace|observed|build   :|placeholders' | ForEach-Object { "  " + $_.Line }
if ($carveExit -ne 0 -and $carveExit -ne 3) { $carveOut | Select-Object -Last 15 | ForEach-Object { "  | $_" } }
$sw.Stop()
Remove-Item $gtCfg -Force -ErrorAction SilentlyContinue
if ($carveExit -ne 0) { throw "carve failed (exit $carveExit) - no result to judge" }
if (-not (Test-Path $ccManifest)) { throw "carve produced no manifest ($ccManifest)" }

$cc = Get-Content $ccManifest -Raw | ConvertFrom-Json
$keptBase = @{}; foreach ($f in $cc.keptFiles) { $keptBase[(RelKey ([string]$f))] = $true }

# SOUNDNESS: every reachable function's def-file must be kept.
$missing = @($expectedFiles.Keys | Where-Object { -not $keptBase.ContainsKey($_) })
# PRECISION: kept src_*.c whose function is NOT reachable = over-keep (headers/blobs ignored - not func nodes).
$overkeep = @($cc.keptFiles | ForEach-Object { RelKey ([string]$_) } |
              Where-Object { ($_ -split '/')[-1] -match '^src_\d+\.c$' -and -not $expectedFiles.ContainsKey($_) })

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

$traceFail = $false
if ($TraceBuild) {
  # A file the clean build never compiled cannot be part of it: keeping one is a precision bug.
  $keptNotCompiled = @($cc.keptFiles | ForEach-Object { RelKey ([string]$_) } | Where-Object { $_ -like '*.c' -and -not $compiled.ContainsKey($_) })
  if ($keptNotCompiled.Count -eq 0) {
    Write-Host ("TRACE      : PASS - {0} .c compiled, none of the never-compiled ones kept" -f $compiled.Count) -ForegroundColor Green
  } else {
    Write-Host ("TRACE      : FAIL - {0} never-compiled .c file(s) kept, e.g. {1}" -f $keptNotCompiled.Count, (($keptNotCompiled | Select-Object -First 3) -join ', ')) -ForegroundColor Red
    $traceFail = $true
  }
  # The carved tree must build with the ORIGINAL build's file list (a dropped file it lists is a placeholder) and
  # need no symbol the original build didn't: compile that list from the carved tree, combine, compare undefined.
  $linkDir = Join-Path $traceDir 'link'
  New-Item -ItemType Directory -Force $linkDir | Out-Null
  & wsl -d Ubuntu -- bash (ToWsl (Join-Path $repoRoot 'tools\trace-oracle\wsl-trace-build.sh')) link (ToWsl (Join-Path $ccOut 'carved')) (ToWsl (Join-Path $traceDir 'list.ok')) (ToWsl $linkDir)
  if ($LASTEXITCODE -ne 0) {
    Write-Host "LINK       : FAIL - the original build's file list does not compile from the carved tree (see $linkDir\build.log)" -ForegroundColor Red
    $traceFail = $true
  } else {
    $origUndef = @(Get-Content (Join-Path $traceDir 'undefined.txt') | Where-Object { $_ })
    $newUndef = @(Get-Content (Join-Path $linkDir 'undefined.txt') | Where-Object { $_ } | Where-Object { $origUndef -notcontains $_ })
    $placeholders = @($cc.placeholderFiles).Count
    if ($newUndef.Count -eq 0) {
      Write-Host ("LINK       : PASS - the build's {0} files compile from the carved tree ({1} placeholder(s)); no new undefined symbol" -f @(Get-Content (Join-Path $traceDir 'list.ok')).Count, $placeholders) -ForegroundColor Green
    } else {
      Write-Host ("LINK       : FAIL - {0} symbol(s) undefined in the carved build only, e.g. {1}" -f $newUndef.Count, (($newUndef | Select-Object -First 5) -join ', ')) -ForegroundColor Red
      $traceFail = $true
    }
  }
  Remove-Item -Recurse -Force $traceDir -ErrorAction SilentlyContinue
}

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
$digestFail = $false
if ($m._meta.indirectTruthSha) {
  # WIRED (CodeSpawner 1.0.9). Canonical byte-form (manifest-schema.md, locked 3-way): per edge
  # `source US target US via US dispatched US resolved` (US=0x1F, bools 0/1); dedup + ORDINAL-sort each
  # population; each record trailed by RS(0x1E); digest = sha256( indirectSection GS(0x1D) rootsSection ),
  # lowercase hex. roots section = the dedup+ordinal-sorted _meta.roots, same RS-trailed framing.
  $US=[char]0x1f; $RS=[char]0x1e; $GS=[char]0x1d
  function Compute-IndirectDigest([string[]]$records, [string[]]$roots) {
    $recs=[System.Collections.Generic.List[string]]::new(); foreach($x in ($records|Select-Object -Unique)){$recs.Add([string]$x)}; $recs.Sort([System.StringComparer]::Ordinal)
    $rts=[System.Collections.Generic.List[string]]::new(); foreach($x in ($roots|Select-Object -Unique)){$rts.Add([string]$x)}; $rts.Sort([System.StringComparer]::Ordinal)
    $sb=New-Object System.Text.StringBuilder
    foreach($r in $recs){ [void]$sb.Append($r); [void]$sb.Append($RS) }
    [void]$sb.Append($GS)
    foreach($r in $rts){ [void]$sb.Append($r); [void]$sb.Append($RS) }
    (([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString())))|ForEach-Object{$_.ToString('x2')}) -join ''
  }
  # SELF-TEST against the frozen golden vector FIRST (schema-doc fixture) -- proves our byte-form matches the
  # generator's before we trust a recompute on real data (the gate that locked prevTruthSha).
  $goldRecords = @(
    ("func_0","itgt_0","fnptr","1","1" -join $US),
    ("func_0","iext_1","vector-table","0","0" -join $US),
    ("func_1","itgt_0","init_array","1","1" -join $US))
  $gold = Compute-IndirectDigest $goldRecords @("func_0")
  $goldWant = "fa9432bd75cd97b7d0a509f885f964b86b8ef41d449cc8c23b1b79df1aa1572f"
  if ($gold -ne $goldWant) {
    Write-Host ("indirectTruthSha: SELF-TEST FAILED (got {0}.. want {1}..) -- digest impl drifted from the generator; NOT trusting recompute" -f $gold.Substring(0,12), $goldWant.Substring(0,12)) -ForegroundColor Red
    $digestFail = $true
  } else {
    # Recompute over THIS manifest's indirect edges + roots and assert equality.
    $records = foreach($p in $syms.PSObject.Properties){ foreach($e in (@($p.Value.indirectEdges)|Where-Object{$_})){ ($p.Name,[string]$e.target,[string]$e.via,([int][bool]$e.dispatched),([int][bool]$e.resolved) -join $US) } }
    $recompute = Compute-IndirectDigest @($records) @($m._meta.roots)
    if ($recompute -eq $m._meta.indirectTruthSha) {
      Write-Host ("DIGEST     : PASS - indirectTruthSha {0}.. reproduced ({1} edge(s); self-test OK)" -f $recompute.Substring(0,12), @($records).Count) -ForegroundColor Green
    } else {
      Write-Host ("DIGEST     : FAIL - recompute {0}.. != stored {1}.. (indirect-edge ground truth drifted)" -f $recompute.Substring(0,12), $m._meta.indirectTruthSha.Substring(0,12)) -ForegroundColor Red
      $digestFail = $true
    }
  }
}

# (d) GRADED reduction -- the --oracle-reachable-frac dial. CodeSpawner 1.0.8 does NOT emit an explicit
#     _meta.reachableFraction field; the fraction is IMPLICIT (a fixed reachable core diluted by dead
#     subgraphs). So we take the target from -ExpectedReachableFrac, else auto-derive it from a
#     `carve_r<NN>` corpus name (r75 -> 0.75), and assert observed within tolerance. The dial is approximate
#     by construction (dead subgraphs come in discrete chunks -> ~+/-0.06), so the default tolerance is loose
#     enough not to false-trip yet tight enough to catch "kept everything" / "kept nothing".
$gradedFail = $false
$expFrac = $ExpectedReachableFrac
if ($expFrac -lt 0) {
  $leaf = (Split-Path (Resolve-Path $Corpus) -Leaf)
  if ($leaf -match 'r(\d{1,3})$') { $expFrac = [double]$matches[1] / 100.0 }
}
if ($expFrac -ge 0) {
  $total = @($syms.PSObject.Properties.Name).Count
  $actualFrac = if ($total -gt 0) { [double]$reachDirect.Count / $total } else { 0 }
  $delta = [math]::Abs($actualFrac - $expFrac)
  if ($delta -le $GradedTolerance) {
    Write-Host ("GRADED     : PASS - reachable(direct) {0}/{1} = {2:P0} within {3:P0} of target {4:P0}" -f $reachDirect.Count, $total, $actualFrac, $GradedTolerance, $expFrac) -ForegroundColor Green
  } else {
    Write-Host ("GRADED     : FAIL - reachable(direct) {0}/{1} = {2:P0}, target {3:P0}, off by {4:P0} (> tol {5:P0})" -f $reachDirect.Count, $total, $actualFrac, $expFrac, $delta, $GradedTolerance) -ForegroundColor Red
    $gradedFail = $true
  }
} else {
  Write-Host "GRADED     : SKIP - no -ExpectedReachableFrac and corpus name is not carve_r<NN>" -ForegroundColor DarkGray
}
# =========================================================================================================

Remove-Item -Recurse -Force $ccOut -ErrorAction SilentlyContinue   # script-made temp dir (GUID name)
if ($missing.Count -ne 0 -or $gradedFail -or $digestFail -or $traceFail) { exit 1 }
