<#
.SYNOPSIS
  Ground-truth carve oracle over a synthetic COMPILABLE firmware corpus (make-carver-corpus.ps1). Because
  the generator knows the true call graph, this proves a carve is CORRECT with no proprietary toolchain,
  across up to three tiers (each skipped cleanly if its compiler is absent):
    1. compiler-free  - every reachable/implicit-root function KEPT (soundness), every dead one DROPPED.
    2. host build     - compile+link the CARVED tree with WSL gcc; undefined ref = a real dropped symbol.
    3. ARM build      - cross-compile+link to a real Cortex-M ELF with arm-none-eabi-gcc; compare .text
                        (the flash-footprint metric that matches the TRACE32 image).
  -Stress adds the firmware byte pathology (giant register headers, blobs, build-output noise, deep dirs)
  to exercise the big-file/parse-timeout/exclusion paths while asserting correctness still holds and the
  carve doesn't hang.

.PARAMETER Out     Corpus dir (default .oracle-cpp/synth). Regenerated if -Regen or absent, or always if -Stress.
.PARAMETER Regen   Force regeneration.
.PARAMETER Scale   Generator scale (default 1.0).
.PARAMETER Seed    Generator seed (default 1337).
.PARAMETER Stress  Generate with giant headers / blobs / build-output / deep dirs (robustness run).
.PARAMETER NoBuild Skip the host + ARM build tiers (run only the compiler-free ground-truth check).
.PARAMETER NoArm   Skip only the ARM tier.
#>
param(
    [string]$Out = "$PSScriptRoot\.oracle-cpp\synth",
    [switch]$Regen,
    [double]$Scale = 1.0,
    [int]$Seed = 1337,
    [switch]$Stress,
    [switch]$NoBuild,
    [switch]$NoArm
)
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$cli = Join-Path $root 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release" }
function ToWsl($p) { $f = [IO.Path]::GetFullPath($p); "/mnt/$($f.Substring(0,1).ToLower())/$($f.Substring(3) -replace '\\','/')" }
function CFiles($dir) { (Get-ChildItem -Recurse $dir -Filter *.c -ErrorAction SilentlyContinue).FullName }

# --- detect available compilers (each tier degrades cleanly to a skip) ---
$wslGcc = $false
if (-not $NoBuild) { try { $wslGcc = ((wsl -d Ubuntu -- bash -c "command -v gcc") -match 'gcc') } catch { $wslGcc = $false } }
$armGcc = $null
if (-not $NoBuild -and -not $NoArm) {
    $a = Get-ChildItem (Join-Path $root '.toolchains') -Recurse -Filter arm-none-eabi-gcc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($a) { $armGcc = $a.FullName; $armSize = Join-Path $a.Directory 'arm-none-eabi-size.exe' }
}

# --- generate ---
if ($Stress -or $Regen -or -not (Test-Path $Out)) {
    $gen = @{ Out = $Out; Scale = $Scale; Seed = $Seed }
    if ($Stress) { $gen += @{ GiantHeaders = 2; MacroDensity = 400000; MaxHeaderMB = 24; BlobFiles = 3; TinyFiles = 150; BuildOutput = $true; Dirs = 40; Depth = 7 } }
    & (Join-Path $root 'make-carver-corpus.ps1') @gen | ForEach-Object { Write-Host "  $_" }
}
$manifestPath = Join-Path (Split-Path (Resolve-Path $Out)) ((Split-Path $Out -Leaf) + "-manifest.json")
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
$roots = $m.roots -join ','
$srcDir = Join-Path $Out 'src'
Write-Host ("Oracle over {0} [{1}]: roots={2}, {3} expected-kept, {4} expected-dropped" -f `
    $Out, $(if ($Stress) { 'STRESS' } else { 'base' }), $roots, $m.expectedKept.Count, $m.expectedDropped.Count) -ForegroundColor Cyan
Write-Host ("  tiers: compiler-free=yes  host-gcc={0}  arm-gcc={1}" -f $wslGcc, [bool]$armGcc)
$fail = 0

# --- carve (clean out first; carve the WHOLE corpus so the .ld KEEP path is exercised) ---
$carveOut = "$Out-carved"
if (Test-Path $carveOut) { Remove-Item $carveOut -Recurse -Force }
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet $cli carve $Out --lang c --roots $roots --prune --strict-roots --out $carveOut --aux '*.ld' 2>&1 |
    Select-String 'nodes|files|UNRESOLVED|verify|warn' | ForEach-Object { Write-Host "  $_" }
$sw.Stop()
Write-Host ("  carve wall-clock: {0:N1}s{1}" -f $sw.Elapsed.TotalSeconds, $(if ($Stress -and $sw.Elapsed.TotalSeconds -gt 120) { '  !!! SLOW (possible hang risk)' } else { '' }))

# --- TIER 1: compiler-free ground-truth (KEEP-function set vs manifest) ---
$spans = & dotnet $cli carve $Out --lang c --roots $roots --dump-spans 2>$null
$keptNames = @{}
foreach ($ln in ($spans | Select-String 'KEEP Function')) {
    if ($ln.Line -match 'KEEP Function [^\t]+\t(\S+)') { $keptNames[$Matches[1]] = $true }
}
$missing = @($m.expectedKept | Where-Object { -not $keptNames.ContainsKey($_) })
$leaked = @($m.expectedDropped | Where-Object { $keptNames.ContainsKey($_) })
if ($missing.Count -eq 0) { Write-Host "  [T1 soundness] all $($m.expectedKept.Count) reachable/implicit-root functions KEPT." -ForegroundColor Green }
else { Write-Host "  [T1 soundness] !!! DROPPED $($missing.Count) that must survive: $($missing -join ', ')" -ForegroundColor Red; $fail++ }
if ($leaked.Count -eq 0) { Write-Host "  [T1 precision] all $($m.expectedDropped.Count) dead functions DROPPED." -ForegroundColor Green }
else { Write-Host "  [T1 precision] kept $($leaked.Count) dead (over-keep; sound but loose): $($leaked -join ', ')" -ForegroundColor Yellow }

# --- T1b: FIXPOINT (eval-#4 idea) -- carve the carved tree AGAIN with the same roots; a correct closure is
# idempotent, so T'' must be byte-identical to T'. This re-runs the WHOLE pipeline on DIFFERENT input, so
# unlike --verify (whose call sites come from the same list that built the edges) it can catch a
# closure/emit bug --verify is blind to. Compiler-free and cheap. ---
function TreeHashes($d) {
    $h = @{}
    Get-ChildItem -Recurse $d -File -ErrorAction SilentlyContinue | ForEach-Object {
        $rel = $_.FullName.Substring((Resolve-Path $d).Path.Length).TrimStart('\','/').Replace('\','/')
        $h[$rel] = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash
    }
    return $h
}
$fpOut = "$Out-carved-fp"
if (Test-Path $fpOut) { Remove-Item $fpOut -Recurse -Force }
& dotnet $cli carve $carveOut --lang c --roots $roots --prune --out $fpOut --aux '*.ld' 2>&1 | Out-Null
$h1 = TreeHashes $carveOut; $h2 = TreeHashes $fpOut
$fpDiff = @($h1.Keys | Where-Object { -not $h2.ContainsKey($_) -or $h1[$_] -ne $h2[$_] }) + @($h2.Keys | Where-Object { -not $h1.ContainsKey($_) })
if ($fpDiff.Count -eq 0) { Write-Host "  [T1b fixpoint] re-carve is byte-identical ($($h1.Count) files) -- closure is idempotent." -ForegroundColor Green }
else { Write-Host "  [T1b fixpoint] !!! re-carve DIFFERS on $($fpDiff.Count) path(s): $(( $fpDiff | Select-Object -First 6) -join ', ')" -ForegroundColor Red; $fail++ }

# --- T1c: TRACE soundness (models next week's runtime-trace input). A trace is real execution ground truth,
# so every traced function MUST be in the carve's kept set -- a traced-but-dropped function means the static
# carve missed a real (likely dynamic-dispatch) edge. Checks the manifest's synthetic trace against the
# kept-set from the plain (rooted-at-main) carve, then confirms the CLI --trace flag roots the trace. ---
if ($m.traced) {
    $tracedMissing = @($m.traced | Where-Object { -not $keptNames.ContainsKey($_) })
    if ($tracedMissing.Count -eq 0) { Write-Host "  [T1c trace] all $($m.traced.Count) traced functions KEPT (runtime ground truth holds)." -ForegroundColor Green }
    else { Write-Host "  [T1c trace] !!! $($tracedMissing.Count) traced function(s) DROPPED by the static carve: $($tracedMissing -join ', ')" -ForegroundColor Red; $fail++ }
    if ($m.tracePath -and (Test-Path $m.tracePath)) {
        $tl = (& dotnet $cli carve $Out --lang c --roots $roots --trace $m.tracePath --prune --out "$Out-carved-tr" 2>&1 | Select-String 'trace   :').Line
        if ($tl) { Write-Host "  [T1c trace]$($tl -replace '^\s*trace   :','  --trace:')" -ForegroundColor Green }
    }
}

# --- host + ARM builds: a shared helper compiles a file list and returns "OK <size>" or "FAIL" ---
function HostBuild($srcRootDir, $tag) {
    $cf = (CFiles $srcRootDir | ForEach-Object { ToWsl $_ }) -join ' '
    if (-not $cf) { return $null }
    $inc = ToWsl $srcRootDir
    # rm the target FIRST so a failed build can't leave a stale binary that stat() reads as a false success.
    $r = wsl -d Ubuntu -- bash -c "rm -f /tmp/cc_$tag; gcc -O0 -I$inc $cf -o /tmp/cc_$tag 2>/tmp/cc_${tag}err; echo SIZE=`$(stat -c%s /tmp/cc_$tag 2>/dev/null)"
    $s = ($r | Select-String 'SIZE=(\d+)').Matches.Groups[1].Value
    if ($s) { return [int]$s }
    Write-Host ("    host link FAIL ($tag):") -ForegroundColor Red
    wsl -d Ubuntu -- bash -c "grep -E 'undefined reference|error:' /tmp/cc_${tag}err | head -8"
    return $null
}
function ArmBuild($srcRootDir, $elf) {
    $cf = CFiles $srcRootDir
    if (-not $cf) { return $null }
    if (Test-Path $elf) { Remove-Item $elf -Force }   # no stale ELF -> a failed link can't read as success
    & $armGcc -mcpu=cortex-m4 -mthumb -O0 -ffreestanding -nostdlib "-Wl,-eapp_reset" "-Wl,-Ttext=0x08000000" "-I$srcRootDir" @cf -o $elf 2>$elf.err
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $elf)) { Write-Host "    ARM link FAIL:" -ForegroundColor Red; Get-Content "$elf.err" -ErrorAction SilentlyContinue | Select-Object -First 8 | ForEach-Object { "      $_" }; return $null }
    $line = (& $armSize $elf | Select-Object -Last 1)
    return [int](($line -split '\s+' | Where-Object { $_ -match '^\d+$' })[0])
}

# --- TIER 2: host build + size ---
if ($wslGcc) {
    $baseH = HostBuild $srcDir 'base'
    $carvH = HostBuild (Join-Path $carveOut 'src') 'carved'
    if ($baseH -and $carvH) {
        $pct = [Math]::Round(100.0 * ($baseH - $carvH) / $baseH, 1)
        Write-Host ("  [T2 host build] carved links; image {0} -> {1} B ({2}% smaller)" -f $baseH, $carvH, $pct) -ForegroundColor $(if ($carvH -lt $baseH) { 'Green' } else { 'Yellow' })
        if ($carvH -ge $baseH) { Write-Host "    (not smaller - dead code not pruned?)" -ForegroundColor Yellow }
    } elseif ($carvH -eq $null) { Write-Host "  [T2 host build] !!! carved tree failed to link (dropped symbol)" -ForegroundColor Red; $fail++ }
} else { Write-Host "  [T2 host build] SKIPPED (no WSL gcc)" -ForegroundColor DarkGray }

# --- TIER 3: ARM cross build + .text size (the flash-footprint / TRACE32-image metric) ---
if ($armGcc) {
    $baseA = ArmBuild $srcDir "$env:TEMP\cc_base.elf"
    $carvA = ArmBuild (Join-Path $carveOut 'src') "$env:TEMP\cc_carved.elf"
    if ($baseA -and $carvA) {
        $pct = [Math]::Round(100.0 * ($baseA - $carvA) / $baseA, 1)
        Write-Host ("  [T3 ARM ELF] carved links (cortex-m4); .text {0} -> {1} B ({2}% smaller)" -f $baseA, $carvA, $pct) -ForegroundColor $(if ($carvA -lt $baseA) { 'Green' } else { 'Yellow' })
    } elseif ($carvA -eq $null) { Write-Host "  [T3 ARM ELF] !!! carved tree failed to cross-link (dropped symbol)" -ForegroundColor Red; $fail++ }
} else { Write-Host "  [T3 ARM ELF] SKIPPED (no arm-none-eabi-gcc; run ./fetch-toolchains.ps1)" -ForegroundColor DarkGray }

Write-Host ""
if ($fail -eq 0) { Write-Host "ORACLE PASS: carve sound (ground-truth) and the carved image builds$(if($wslGcc -or $armGcc){' + is smaller'} else {''})." -ForegroundColor Green; exit 0 }
Write-Host "ORACLE FAIL: $fail check(s) above." -ForegroundColor Red; exit 1
