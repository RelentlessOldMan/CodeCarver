<#
.SYNOPSIS
  Ground-truth carve oracle over a synthetic COMPILABLE corpus (make-carver-corpus.ps1). The generator
  knows the true call graph, so this asserts a carve is correct WITHOUT the proprietary build:
    1. compiler-free: every function the manifest says is reachable-from-root (or an implicit-root closure)
       is KEPT (soundness), and every dead function is DROPPED (precision);
    2. authoritative: compile+link the CARVED output with gcc -- an undefined reference is a real dropped
       symbol (this is the "builds" tier, run locally);
    3. size: the carved image must be strictly smaller than the full baseline (the dead code was removed).
  This is the trust ladder (builds+smaller > builds > kept-set-correct) on ground truth, no real toolchain.

.PARAMETER Out     Corpus dir (default .oracle-cpp/synth). Regenerated if -Regen or absent.
.PARAMETER Regen   Force regeneration.
.PARAMETER Scale   Passed to the generator (default 1.0).
.PARAMETER Seed    Passed to the generator (default 1337).
.PARAMETER NoBuild Skip the compile/link/size tier (run only the compiler-free ground-truth check).
#>
param(
    [string]$Out = "$PSScriptRoot\.oracle-cpp\synth",
    [switch]$Regen,
    [double]$Scale = 1.0,
    [int]$Seed = 1337,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$cli = Join-Path $root 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release" }
function ToWsl($p) { $f = [IO.Path]::GetFullPath($p); "/mnt/$($f.Substring(0,1).ToLower())/$($f.Substring(3) -replace '\\','/')" }

if ($Regen -or -not (Test-Path $Out)) {
    & (Join-Path $root 'make-carver-corpus.ps1') -Out $Out -Scale $Scale -Seed $Seed | ForEach-Object { Write-Host "  $_" }
}
$manifestPath = Join-Path (Split-Path (Resolve-Path $Out)) ((Split-Path $Out -Leaf) + "-manifest.json")
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
$roots = $m.roots -join ','
$srcDir = Join-Path $Out 'src'
Write-Host ("Oracle over {0}: roots={1}, {2} expected-kept, {3} expected-dropped" -f $Out, $roots, $m.expectedKept.Count, $m.expectedDropped.Count) -ForegroundColor Cyan

$fail = 0

# --- baseline build (full corpus) for the size comparison ---
$baseSize = $null
if (-not $NoBuild) {
    $cfiles = (Get-ChildItem -Recurse $srcDir -Filter *.c | ForEach-Object { ToWsl $_.FullName }) -join ' '
    $incW = ToWsl $srcDir
    $r = wsl -d Ubuntu -- bash -c "gcc -O0 -I$incW $cfiles -o /tmp/cc_base 2>/tmp/cc_baseerr; echo SIZE=`$(stat -c%s /tmp/cc_base 2>/dev/null)"
    $sz = ($r | Select-String 'SIZE=(\d+)').Matches.Groups[1].Value
    if ($sz) { $baseSize = [int]$sz } else { Write-Host "  baseline build FAILED (corpus itself doesn't compile?)" -ForegroundColor Red; wsl -d Ubuntu -- bash -c "head -8 /tmp/cc_baseerr"; $fail++ }
}

# --- carve rooted at the manifest roots (carve the WHOLE corpus so the .ld KEEP path is exercised) ---
$carveOut = "$Out-carved"
if (Test-Path $carveOut) { Remove-Item $carveOut -Recurse -Force }   # avoid stale files from a prior run
& dotnet $cli carve $Out --lang c --roots $roots --prune --strict-roots --out $carveOut --aux '*.ld' 2>&1 |
    Select-String 'nodes|files|UNRESOLVED|verify' | ForEach-Object { Write-Host "  $_" }

# --- 1. compiler-free ground-truth: KEEP-function set vs manifest ---
$spans = & dotnet $cli carve $Out --lang c --roots $roots --dump-spans 2>$null
$keptNames = @{}
foreach ($ln in ($spans | Select-String 'KEEP Function')) {
    if ($ln.Line -match 'KEEP Function [^\t]+\t(\S+)') { $keptNames[$Matches[1]] = $true }
}
$missing = @($m.expectedKept | Where-Object { -not $keptNames.ContainsKey($_) })
$leaked  = @($m.expectedDropped | Where-Object { $keptNames.ContainsKey($_) })
if ($missing.Count -eq 0) { Write-Host "  [soundness] all $($m.expectedKept.Count) reachable/implicit-root functions KEPT." -ForegroundColor Green }
else { Write-Host "  [soundness] !!! DROPPED $($missing.Count) function(s) that must survive: $($missing -join ', ')" -ForegroundColor Red; $fail++ }
if ($leaked.Count -eq 0) { Write-Host "  [precision] all $($m.expectedDropped.Count) dead functions DROPPED." -ForegroundColor Green }
else { Write-Host "  [precision] kept $($leaked.Count) dead function(s) (over-keep; sound but loose): $($leaked -join ', ')" -ForegroundColor Yellow }

# --- 2 + 3. build the carved output and compare size ---
if (-not $NoBuild -and $baseSize) {
    $carveSrc = Join-Path $carveOut 'src'
    $cf2 = (Get-ChildItem -Recurse $carveSrc -Filter *.c | ForEach-Object { ToWsl $_.FullName }) -join ' '
    $inc2 = ToWsl $carveSrc
    $r2 = wsl -d Ubuntu -- bash -c "gcc -O0 -I$inc2 $cf2 -o /tmp/cc_carved 2>/tmp/cc_carveerr; echo SIZE=`$(stat -c%s /tmp/cc_carved 2>/dev/null)"
    $sz2 = ($r2 | Select-String 'SIZE=(\d+)').Matches.Groups[1].Value
    if ($sz2) {
        $carvedSize = [int]$sz2
        Write-Host "  [build] carved tree links under gcc." -ForegroundColor Green
        $pct = [Math]::Round(100.0 * ($baseSize - $carvedSize) / $baseSize, 1)
        if ($carvedSize -lt $baseSize) { Write-Host ("  [size] {0} -> {1} bytes ({2}% smaller) -- dead code removed." -f $baseSize, $carvedSize, $pct) -ForegroundColor Green }
        else { Write-Host ("  [size] {0} -> {1} bytes (NOT smaller -- dead code not pruned?)" -f $baseSize, $carvedSize) -ForegroundColor Yellow }
    } else {
        Write-Host "  [build] !!! carved tree FAILED to link -- a needed symbol was dropped:" -ForegroundColor Red
        wsl -d Ubuntu -- bash -c "grep -E 'undefined reference|error:' /tmp/cc_carveerr | head -10"
        $fail++
    }
}

Write-Host ""
if ($fail -eq 0) { Write-Host "ORACLE PASS: carve is sound (ground-truth) and the carved image builds + is smaller." -ForegroundColor Green; exit 0 }
Write-Host "ORACLE FAIL: $fail check(s) above." -ForegroundColor Red; exit 1
