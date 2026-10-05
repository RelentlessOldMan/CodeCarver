# carve-build-oracle.ps1
# BUILD oracle for CodeCarver: the strongest end-to-end soundness check -- carve a corpus to --out, then have a
# REAL compiler+linker judge the result. Companion to carver-groundtruth-oracle.ps1 (which proves kept==reachable
# with NO compiler); this one proves the carved slice actually COMPILES and LINKS, and that indirect-edge
# soundness is load-bearing. Two assertions:
#   POSITIVE : every carved .c compiles to a .o, and they link (with a driver main->root and stubs ONLY for the
#              corpus's declared externals, i.e. indirectEdges with resolved:false). No UNEXPECTED undefined
#              reference = the carve kept every internally-needed symbol (sound).
#   NEGATIVE : re-link with the object(s) that DEFINE the reachable resolved indirect targets removed (what a
#              MinimalUnsafe carve would drop). The link MUST fail with an undefined reference to one of those
#              targets -- proving the sound carve's over-approximation at indirect edges is necessary, not
#              academic. A link failure for any OTHER reason does not count as a pass.
#
# The externs-to-stub and the negative-control drop-set are DERIVED FROM THE MANIFEST, so this is corpus-generic
# (not hardcoded). Toolchain-generic too: -Cc lets you point at arm-none-eabi-gcc etc. Runs the compile/link via
# WSL by default (Windows has no native gcc here); -NativeBash runs `bash` directly if you have one on PATH.
#
# -Corpus may be a full path, or a name resolved under $env:CODECARVER_TESTHOLE (local dir or share):
#   ./carve-build-oracle.ps1 -Corpus C:\path\to\TestHole\carve_r75
#   ./carve-build-oracle.ps1 -Corpus carve_r50 -Cc arm-none-eabi-gcc     # $env:CODECARVER_TESTHOLE\carve_r50
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Corpus,
  [string]$Manifest,
  [string]$Root,                       # default: the corpus's _meta.roots[0]
  [string]$CliDll,
  [string]$Cc = 'gcc',                 # compiler command (inside WSL, or native); e.g. arm-none-eabi-gcc
  [string]$OutDir,                     # carve output dir (default: a temp dir, cleaned unless -KeepBuild).
                                       # An existing non-empty -OutDir is only ever deleted if this script
                                       # created it (it carries the .carve-build-oracle marker).
  [switch]$NativeBash,                 # run the build via `bash` on PATH instead of `wsl bash`
  [switch]$KeepBuild
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
$corpusFull = (Resolve-Path $Corpus).ProviderPath.TrimEnd('\', '/')
if (-not $Manifest) {
  $Manifest = Join-Path (Split-Path $corpusFull) ((Split-Path $corpusFull -Leaf) + '-manifest.json')
}
if (-not (Test-Path $Manifest)) { throw "manifest not found: $Manifest" }

# Everything below is interpolated into a generated bash script: validate it first (review SC-D7).
if ($Cc -notmatch '^[A-Za-z0-9_./+-]+$') { throw "-Cc '$Cc' must be a plain command name or path (no spaces or shell characters)" }

$ownMarker = '.carve-build-oracle'
if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) ("cc-build-" + [Guid]::NewGuid().ToString('N').Substring(0,8)) }

Write-Host "== parsing manifest ==" -ForegroundColor Cyan
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
if ($m._meta.manifestVersion -ne 1) { throw "manifest version $($m._meta.manifestVersion) != 1" }
$syms = $m.symbols

# Def-file per symbol as a path RELATIVE to the corpus root (review SC-D6: basenames collide across directories);
# direct call graph; indirect edges. NOTE the @() placement: wrap the PIPELINE OUTPUT `@($x | ? {})`, not
# `@($x) | ? {}` -- the latter unwraps a lone hit to a scalar (null .Count) and silently drops single-element
# arrays (the bug that once hid indirect edges in the ground-truth oracle).
$defRel=@{}; $calls=@{}; $indirect=@{}
foreach ($p in $syms.PSObject.Properties) {
  $defRel[$p.Name] = (($p.Value.def -replace ':\d+$','') -replace '\\','/') -replace '^\./',''
  $e = @($p.Value.edges | Where-Object { $_ }); if ($e.Count -gt 0) { $calls[$p.Name] = $e }
  $ie = @($p.Value.indirectEdges | Where-Object { $_ }); if ($ie.Count -gt 0) { $indirect[$p.Name] = $ie }
}

# Root(s): explicit -Root, else _meta.roots[0].
if (-not $Root) {
  if (-not $m._meta.roots) { throw "no -Root and manifest has no _meta.roots" }
  $Root = @($m._meta.roots)[0]
}
if (-not $defRel.ContainsKey($Root)) { throw "root '$Root' not in manifest (phantom root)" }
if (-not (Test-CIdentifier $Root)) { throw "root '$Root' is not a C identifier" }

# The object name a carved .c compiles to: its relative path, '/' -> '__', '.c' -> '.o' (unique per path).
function ObjName([string]$rel) { return (($rel -replace '\.c$','') -replace '/','__') + '.o' }

# Direct-edge reachable closure from the root -- the set actually emitted, so the set whose indirect edges appear
# in the carved output (externs to stub) and whose resolved targets must be present (negative-control drop-set).
$reach=@{}; $q=New-Object System.Collections.Queue; $reach[$Root]=$true; $q.Enqueue($Root)
while ($q.Count -gt 0) { $c=$q.Dequeue(); if ($calls.ContainsKey($c)) { foreach ($n in $calls[$c]) { if (-not $reach.ContainsKey($n)) { $reach[$n]=$true; $q.Enqueue($n) } } } }

$externs = @{}      # resolved:false targets referenced by reachable code -> stub them (vendor-supplied in reality)
$dropObjs = @{}     # object files defining resolved:true indirect targets reachable code references -> neg control
$dropTargets = @{}  # those targets: the negative link must report at least one of them undefined
foreach ($s in $reach.Keys) {
  if (-not $indirect.ContainsKey($s)) { continue }
  foreach ($e in $indirect[$s]) {
    if ($e.resolved -eq $false) { $externs[[string]$e.target] = $true }
    else {
      $t = [string]$e.target
      if ($defRel.ContainsKey($t)) { $dropObjs[(ObjName $defRel[$t])] = $true; $dropTargets[$t] = $true }
    }
  }
}
$externList = @($externs.Keys | Sort-Object)
$dropList   = @($dropObjs.Keys | Sort-Object)
$dropTargetList = @($dropTargets.Keys | Sort-Object)
foreach ($n in @($externList + $dropTargetList)) { if (-not (Test-CIdentifier $n)) { throw "manifest symbol '$n' is not a C identifier - refusing to put it in a shell script" } }
foreach ($o in $dropList) { if ($o -notmatch '^[A-Za-z0-9_.+-]+$') { throw "object name '$o' has characters this oracle does not quote" } }
Write-Host ("root {0}: {1} reachable; stub externs: [{2}]; neg-control drop: [{3}]" -f $Root, $reach.Count, ($externList -join ' '), ($dropList -join ' '))

# Output directory: never Remove-Item -Recurse a directory this script did not create (review SC-D7).
if (Test-Path $OutDir) {
  $entries = @(Get-ChildItem -Force -LiteralPath $OutDir)
  if ($entries.Count -gt 0) {
    if (-not (Test-Path (Join-Path $OutDir $ownMarker))) {
      throw "-OutDir '$OutDir' exists, is not empty and was not created by this script (no $ownMarker) - refusing to delete it"
    }
    Remove-Item -LiteralPath $OutDir -Recurse -Force
  }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Set-Content -LiteralPath (Join-Path $OutDir $ownMarker) -Value 'created by carve-build-oracle.ps1; safe to delete' -Encoding ascii

# Carve (file-level; correctness of the build, not intra-file pruning, is what we're judging here). Inputs go in
# a TOML config; the carved tree lands at <OutDir>/carved (keep-by-default complete project).
Write-Host "== carving ==" -ForegroundColor Cyan
$cfg = Join-Path ([IO.Path]::GetTempPath()) ("cc-bo-" + [Guid]::NewGuid().ToString('N').Substring(0,8) + ".toml")
@(
  "outputDirectory = $(ConvertTo-TomlPath $OutDir)"
  '[common]'
  "entryPoints = $(ConvertTo-TomlArray $Root)"
  'languages = ["c"]'
) -join "`n" | Set-Content -Encoding utf8 $cfg
$ErrorActionPreference = 'Continue'
& dotnet $CliDll carve $corpusFull --config $cfg 2>&1 | Select-String 'emitted|files|error' | ForEach-Object { "  " + $_.Line }
$carveExit = $LASTEXITCODE
Remove-Item $cfg -Force -ErrorAction SilentlyContinue
if ($carveExit -ne 0) { throw "carve failed (exit $carveExit)" }
$carvedDir = Join-Path $OutDir 'carved'
if (-not (Test-Path $carvedDir)) { throw "carve produced no carved tree ($carvedDir)" }

# Windows path -> WSL /mnt path.
function ConvertTo-WslPath([string]$p) { $f=(Resolve-Path $p).ProviderPath; if ($f -notmatch '^[A-Za-z]:') { throw "not a drive path (WSL needs one): $f" }; '/mnt/' + $f.Substring(0,1).ToLower() + ($f.Substring(2) -replace '\\','/') }
$srcForBash = if ($NativeBash) { (Resolve-Path $carvedDir).ProviderPath -replace '\\','/' } else { ConvertTo-WslPath $carvedDir }
if ($srcForBash -match '["$`\\]|[\r\n]') { throw "carved path '$srcForBash' contains characters that cannot be quoted safely" }

# Emit a build script (tagged output lines the PS side parses). Compiler, path and names were validated above.
# Work dir from mktemp (no fixed /tmp name shared by concurrent runs); find -print0 so paths with spaces work.
$bash = @"
#!/bin/bash
set -u
export LC_ALL=C   # ASCII quotes in linker messages, so the undefined-reference parse below is reliable
CC="$Cc"
SRC="$srcForBash"
B="`$(mktemp -d "`${TMPDIR:-/tmp}/cc_build_oracle.XXXXXX")" || { echo "TAG MKTEMP_FAIL"; exit 2; }
trap 'rm -rf "`$B"' EXIT
cd "`$B" || exit 2
pos_fail=0
nsrc=0
while IFS= read -r -d '' f; do
  nsrc=`$((nsrc + 1))
  rel="`${f#"`$SRC"/}"; obj="`${rel%.c}"; obj="`${obj//\//__}.o"
  if ! "`$CC" -std=c11 -c "`$f" -o "`$obj" 2>e.log; then
    echo "TAG COMPILE_FAIL `$rel"; sed 's/^/  /' e.log; pos_fail=1
  fi
done < <(find "`$SRC" -name '*.c' -print0)
nobj=`$(find . -maxdepth 1 -name '*.o' | wc -l)
echo "TAG COMPILED `$nobj of `$nsrc"
[ "`$nobj" -gt 0 ] || pos_fail=1
printf 'int %s(); int main(void){ return %s(3); }\n' "$Root" "$Root" > __oracle_drv.c
: > __oracle_ext_stubs.c
for s in $($externList -join ' '); do printf 'int %s(int x){return x;}\n' "`$s" >> __oracle_ext_stubs.c; done
"`$CC" -std=c11 -c __oracle_drv.c __oracle_ext_stubs.c 2>>e.log || { echo "TAG DRV_FAIL"; pos_fail=1; }
if "`$CC" ./*.o -o prog 2>link.log; then
  echo "TAG LINK_OK"; ./prog >/dev/null 2>&1; echo "TAG RAN exit=`$?"
else
  echo "TAG LINK_FAIL"; grep -oE "undefined reference to .[A-Za-z_0-9]+." link.log | sort -u | sed 's/^/  /'; pos_fail=1
fi
echo "TAG POSITIVE `$([ `$pos_fail -eq 0 ] && echo PASS || echo FAIL)"
DROP="$($dropList -join ' ')"
EXPECT="$($dropTargetList -join ' ')"
if [ -z "`$DROP" ]; then
  echo "TAG NEGATIVE SKIP"
else
  mkdir neg; cp ./*.o neg/; for o in `$DROP; do rm -f "neg/`$o"; done
  if "`$CC" neg/*.o -o neg/prog 2>neg.log; then
    echo "TAG NEGATIVE FAIL linked"
  else
    grep -oE "undefined reference to .[A-Za-z_0-9]+." neg.log | sed -E 's/^undefined reference to .//; s/.$//' | sort -u > neg_undef.txt
    hit=0; for t in `$EXPECT; do grep -qx "`$t" neg_undef.txt && hit=1; done
    sed 's/^/  undefined: /' neg_undef.txt
    if [ `$hit -eq 1 ]; then echo "TAG NEGATIVE PASS"; else echo "TAG NEGATIVE FAIL wrong-reason"; sed 's/^/  /' neg.log | head -10; fi
  fi
fi
"@
$bashPath = Join-Path ([IO.Path]::GetTempPath()) ("cc-build-oracle-" + [Guid]::NewGuid().ToString('N').Substring(0,8) + ".sh")
# LF line endings for bash.
[System.IO.File]::WriteAllText($bashPath, ($bash -replace "`r`n","`n"))

Write-Host "== compiling + linking the carved tree ==" -ForegroundColor Cyan
$out = if ($NativeBash) { & bash $bashPath 2>&1 } else { & wsl bash (ConvertTo-WslPath $bashPath) 2>&1 }
$out = @($out | ForEach-Object { [string]$_ })
$out | Where-Object { $_ -notmatch '^TAG ' } | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

function Tag([string]$name) { ($out | Where-Object { $_ -match "^TAG $name(\b|$)" } | Select-Object -First 1) }
$compiled = (Tag 'COMPILED') -replace '^TAG COMPILED\s*',''
$positive = if ((Tag 'POSITIVE') -match 'PASS') { $true } else { $false }
$ran      = (Tag 'RAN') -replace '^TAG RAN\s*',''
$negRaw   = (Tag 'NEGATIVE')
$negState = if ($negRaw -match 'PASS') { 'PASS' } elseif ($negRaw -match 'SKIP') { 'SKIP' } else { 'FAIL' }

Write-Host ''
Write-Host '================ CARVE-BUILD ORACLE ================' -ForegroundColor Cyan
Write-Host ("corpus     : {0}" -f $corpusFull)
Write-Host ("compiler   : {0}{1}" -f $Cc, $(if ($NativeBash) { ' (native bash)' } else { ' (wsl)' }))
Write-Host ("root       : {0}  ({1} reachable)" -f $Root, $reach.Count)
Write-Host ("compiled   : {0} object files" -f $compiled)
if ($positive) {
  Write-Host ("POSITIVE   : PASS - carved tree compiles + links (ran, {0})" -f $ran) -ForegroundColor Green
} else {
  Write-Host  "POSITIVE   : FAIL - carved tree did not compile/link cleanly (see undefined refs above)" -ForegroundColor Red
}
switch ($negState) {
  'PASS' { Write-Host ("NEGATIVE   : PASS - dropping [{0}] leaves [{1}] undefined (indirect soundness is load-bearing)" -f ($dropList -join ' '), ($dropTargetList -join ' ')) -ForegroundColor Green }
  'SKIP' { Write-Host  "NEGATIVE   : SKIP - no reachable resolved indirect targets to test" -ForegroundColor DarkGray }
  default { Write-Host ("NEGATIVE   : FAIL - without [{0}] the link {1} (expected undefined [{2}])" -f ($dropList -join ' '), $(if ($negRaw -match 'linked') { 'succeeded' } else { 'failed for another reason' }), ($dropTargetList -join ' ')) -ForegroundColor Red }
}

Remove-Item $bashPath -Force -ErrorAction SilentlyContinue
if (-not $KeepBuild) {
  # Only our own directory (it carries the marker written above).
  if (Test-Path (Join-Path $OutDir $ownMarker)) { Remove-Item -LiteralPath $OutDir -Recurse -Force -ErrorAction SilentlyContinue }
} else { Write-Host ("kept carve output: {0}" -f $OutDir) }

# FAIL if the carve doesn't build, or if a present drop-set didn't break the link (negative control must hold).
if (-not $positive -or $negState -eq 'FAIL') { exit 1 }
