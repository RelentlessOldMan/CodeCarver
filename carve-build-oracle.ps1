# carve-build-oracle.ps1
# BUILD oracle for CodeCarver: the strongest end-to-end soundness check -- carve a corpus to --out, then have a
# REAL compiler+linker judge the result. Companion to carver-groundtruth-oracle.ps1 (which proves kept==reachable
# with NO compiler); this one proves the carved slice actually COMPILES and LINKS, and that indirect-edge
# soundness is load-bearing. Two assertions:
#   POSITIVE : every carved .c compiles to a .o, and they link (with a driver main->root and stubs ONLY for the
#              corpus's declared externals, i.e. indirectEdges with resolved:false). No UNEXPECTED undefined
#              reference = the carve kept every internally-needed symbol (sound).
#   NEGATIVE : re-link with the object(s) that DEFINE the reachable resolved indirect targets removed (what a
#              MinimalUnsafe carve would drop). The link MUST fail with undefined itgt_* -- proving the sound
#              carve's over-approximation at indirect edges is necessary, not academic.
#
# The externs-to-stub and the negative-control drop-set are DERIVED FROM THE MANIFEST, so this is corpus-generic
# (not hardcoded). Toolchain-generic too: -Cc lets you point at arm-none-eabi-gcc etc. Runs the compile/link via
# WSL by default (Windows has no native gcc here); -NativeBash runs `bash` directly if you have one on PATH.
#
#   ./carve-build-oracle.ps1 -Corpus C:\Playground\TestHole\carve_r75
#   ./carve-build-oracle.ps1 -Corpus \\IRISH\TestHole\carve_r50 -Cc arm-none-eabi-gcc
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Corpus,
  [string]$Manifest,
  [string]$Root,                       # default: the corpus's _meta.roots[0]
  [string]$CliDll,
  [string]$Cc = 'gcc',                 # compiler command (inside WSL, or native); e.g. arm-none-eabi-gcc
  [string]$OutDir,                     # carve output dir (default: a temp dir, cleaned unless -KeepBuild)
  [switch]$NativeBash,                 # run the build via `bash` on PATH instead of `wsl bash`
  [switch]$KeepBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $CliDll) {
  $CliDll = Join-Path $repoRoot 'src\CodeCarver.Cli\bin\Debug\net8.0\codecarver.dll'
  if (-not (Test-Path $CliDll)) { $CliDll = Join-Path $repoRoot 'src\CodeCarver.Cli\bin\Release\net8.0\codecarver.dll' }
}
if (-not (Test-Path $CliDll)) { throw "CLI not built: dotnet build -c Debug" }
if (-not (Test-Path $Corpus)) { throw "corpus not found: $Corpus" }
if (-not $Manifest) {
  $Manifest = Join-Path (Split-Path (Resolve-Path $Corpus)) ((Split-Path (Resolve-Path $Corpus) -Leaf) + '-manifest.json')
}
if (-not (Test-Path $Manifest)) { throw "manifest not found: $Manifest" }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ("cc-build-" + [Guid]::NewGuid().ToString('N').Substring(0,8)) }

Write-Host "== parsing manifest ==" -ForegroundColor Cyan
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
if ($m._meta.manifestVersion -ne 1) { throw "manifest version $($m._meta.manifestVersion) != 1" }
$syms = $m.symbols

# def-file basename per symbol; direct call graph; indirect edges. NOTE the @() placement: wrap the PIPELINE
# OUTPUT `@($x | ? {})`, not `@($x) | ? {}` -- the latter unwraps a lone hit to a scalar (null .Count) and
# silently drops single-element arrays (the bug that once hid indirect edges in the ground-truth oracle).
$defBase=@{}; $calls=@{}; $indirect=@{}
foreach ($p in $syms.PSObject.Properties) {
  $defBase[$p.Name] = [System.IO.Path]::GetFileName(($p.Value.def -replace ':\d+$',''))
  $e = @($p.Value.edges | Where-Object { $_ }); if ($e.Count -gt 0) { $calls[$p.Name] = $e }
  $ie = @($p.Value.indirectEdges | Where-Object { $_ }); if ($ie.Count -gt 0) { $indirect[$p.Name] = $ie }
}

# Root(s): explicit -Root, else _meta.roots[0].
if (-not $Root) {
  if (-not $m._meta.roots) { throw "no -Root and manifest has no _meta.roots" }
  $Root = @($m._meta.roots)[0]
}
if (-not $defBase.ContainsKey($Root)) { throw "root '$Root' not in manifest (phantom root)" }

# Direct-edge reachable closure from the root -- the set actually emitted, so the set whose indirect edges appear
# in the carved output (externs to stub) and whose resolved targets must be present (negative-control drop-set).
$reach=@{}; $q=New-Object System.Collections.Queue; $reach[$Root]=$true; $q.Enqueue($Root)
while ($q.Count -gt 0) { $c=$q.Dequeue(); if ($calls.ContainsKey($c)) { foreach ($n in $calls[$c]) { if (-not $reach.ContainsKey($n)) { $reach[$n]=$true; $q.Enqueue($n) } } } }

$externs = @{}      # resolved:false targets referenced by reachable code -> stub them (vendor-supplied in reality)
$dropObjs = @{}     # object files defining resolved:true indirect targets reachable code references -> neg control
foreach ($s in $reach.Keys) {
  if (-not $indirect.ContainsKey($s)) { continue }
  foreach ($e in $indirect[$s]) {
    if ($e.resolved -eq $false) { $externs[[string]$e.target] = $true }
    else {
      $t = [string]$e.target
      if ($defBase.ContainsKey($t)) { $dropObjs[([System.IO.Path]::GetFileNameWithoutExtension($defBase[$t]) + '.o')] = $true }
    }
  }
}
$externList = @($externs.Keys | Sort-Object)
$dropList   = @($dropObjs.Keys | Sort-Object)
Write-Host ("root {0}: {1} reachable; stub externs: [{2}]; neg-control drop: [{3}]" -f $Root, $reach.Count, ($externList -join ' '), ($dropList -join ' '))

# Carve (file-level; correctness of the build, not intra-file pruning, is what we're judging here). Inputs go in
# a TOML config; the carved tree lands at <OutDir>/carved (keep-by-default complete project).
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
Write-Host "== carving ==" -ForegroundColor Cyan
$cfg = Join-Path $env:TEMP ("cc-bo-" + [Guid]::NewGuid().ToString('N').Substring(0,8) + ".toml")
@"
outputDirectory = "$($OutDir -replace '\\','/')"
[common]
entryPoints = ["$Root"]
languages = ["c"]
"@ | Set-Content -Encoding utf8 $cfg
$ErrorActionPreference = 'Continue'
& dotnet $CliDll carve $Corpus --config $cfg 2>&1 | Select-String 'emitted|files|error' | ForEach-Object { "  " + $_.Line }
if ($LASTEXITCODE -ne 0) { throw "carve failed (exit $LASTEXITCODE)" }
Remove-Item $cfg -Force -ErrorAction SilentlyContinue
$carvedDir = Join-Path $OutDir 'carved'

# Windows path -> WSL /mnt path.
function ConvertTo-WslPath([string]$p) { $f=(Resolve-Path $p).Path; '/mnt/' + $f.Substring(0,1).ToLower() + ($f.Substring(2) -replace '\\','/') }
$srcForBash = if ($NativeBash) { (Resolve-Path $carvedDir).Path -replace '\\','/' } else { ConvertTo-WslPath $carvedDir }

# Emit a build script (tagged output lines the PS side parses). Compiler + paths interpolated.
$bash = @"
#!/bin/bash
set -u
CC="$Cc"
SRC="$srcForBash"
B=/tmp/cc_build_oracle
rm -rf "`$B"; mkdir -p "`$B"; cd "`$B" || exit 2
pos_fail=0
for f in `$(find "`$SRC" -name '*.c'); do
  if ! "`$CC" -std=c11 -c "`$f" -o "`$(basename "`${f%.c}").o" 2>e.log; then
    echo "TAG COMPILE_FAIL `$f"; sed 's/^/  /' e.log; pos_fail=1
  fi
done
echo "TAG COMPILED `$(ls *.o 2>/dev/null | wc -l)"
printf 'int %s(); int main(void){ return %s(3); }\n' "$Root" "$Root" > drv.c
: > ext_stubs.c
EXTERNS="$($externList -join ' ')"
for s in `$EXTERNS; do printf 'int %s(int x){return x;}\n' "`$s" >> ext_stubs.c; done
"`$CC" -std=c11 -c drv.c ext_stubs.c 2>>e.log || { echo "TAG DRV_FAIL"; pos_fail=1; }
if "`$CC" *.o -o prog 2>link.log; then
  echo "TAG LINK_OK"; ./prog >/dev/null 2>&1; echo "TAG RAN exit=`$?"
else
  echo "TAG LINK_FAIL"; grep -oE 'undefined reference to .[A-Za-z_0-9]+.' link.log | sort -u | sed 's/^/  /'; pos_fail=1
fi
echo "TAG POSITIVE `$([ `$pos_fail -eq 0 ] && echo PASS || echo FAIL)"
DROP="$($dropList -join ' ')"
if [ -z "`$DROP" ]; then
  echo "TAG NEGATIVE SKIP"
else
  rm -rf neg; mkdir neg; cp *.o neg/; for o in `$DROP; do rm -f "neg/`$o"; done
  if "`$CC" neg/*.o -o neg/prog 2>neg.log; then
    echo "TAG NEGATIVE FAIL"
  else
    echo "TAG NEGATIVE PASS"; grep -oE 'undefined reference to .[A-Za-z_0-9]+.' neg.log | sort -u | sed 's/^/  /'
  fi
fi
"@
$bashPath = Join-Path $env:TEMP ("cc-build-oracle-" + [Guid]::NewGuid().ToString('N').Substring(0,8) + ".sh")
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
Write-Host ("corpus     : {0}" -f (Resolve-Path $Corpus))
Write-Host ("compiler   : {0}{1}" -f $Cc, $(if ($NativeBash) { ' (native bash)' } else { ' (wsl)' }))
Write-Host ("root       : {0}  ({1} reachable)" -f $Root, $reach.Count)
Write-Host ("compiled   : {0} object files" -f $compiled)
if ($positive) {
  Write-Host ("POSITIVE   : PASS - carved tree compiles + links (ran, {0})" -f $ran) -ForegroundColor Green
} else {
  Write-Host  "POSITIVE   : FAIL - carved tree did not compile/link cleanly (see undefined refs above)" -ForegroundColor Red
}
switch ($negState) {
  'PASS' { Write-Host ("NEGATIVE   : PASS - dropping [{0}] breaks the link (indirect soundness is load-bearing)" -f ($dropList -join ' ')) -ForegroundColor Green }
  'SKIP' { Write-Host  "NEGATIVE   : SKIP - no reachable resolved indirect targets to test" -ForegroundColor DarkGray }
  default { Write-Host ("NEGATIVE   : FAIL - linked even without [{0}] (expected undefined refs)" -f ($dropList -join ' ')) -ForegroundColor Red }
}

Remove-Item $bashPath -Force -ErrorAction SilentlyContinue
if (-not $KeepBuild) { Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host ("kept carve output: {0}" -f $OutDir) }

# FAIL if the carve doesn't build, or if a present drop-set didn't break the link (negative control must hold).
if (-not $positive -or $negState -eq 'FAIL') { exit 1 }
