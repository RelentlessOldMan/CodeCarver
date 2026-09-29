<#
.SYNOPSIS
  Parse-throughput + peak-memory benchmark for CodeCarver. Generates a deterministic synthetic C tree,
  carves it, and measures wall-clock, parse-phase time, parse throughput (MB/s of source), and peak
  working set. Prints a JSON result; optionally compares against a committed baseline with regression
  thresholds. This is the harness that PROVES a performance change (e.g. parallel parsing) helped and
  that it didn't regress — before/after on the SAME machine (absolute numbers are machine-dependent).

.DESCRIPTION
  The synthetic tree is fully deterministic (seeded by file/func counts), so repeated runs are
  comparable. It includes a large macro-DENSE register header to exercise the auto-skip path, and a
  shared header included by every TU (realistic include-closure fan-out).

  ComputeWarden: a DEFAULT-size run (a single carve of a few thousand files) is a single `carve` and is
  NOT machine-saturating. A LARGE run (-Files in the tens of thousands) approaches a build sweep — gate
  it via computewarden_acquire/release before starting, per this repo's CLAUDE.md.

.PARAMETER Files        Number of .c translation units to generate (default 2000).
.PARAMETER FuncsPerFile Functions per TU (default 30).
.PARAMETER Baseline     Path to a baseline JSON to compare against (optional).
.PARAMETER UpdateBaseline  Write the measured result to -Baseline (or ./perf-baseline.json) and exit 0.
.PARAMETER ThroughputDropPct  Fail if parse MB/s is more than this % BELOW baseline (default 25).
.PARAMETER PeakGrowthPct      Fail if peak working set is more than this % ABOVE baseline (default 25).

.EXAMPLE
  # Establish a baseline (current build), then after a change compare against it:
  ./carve-perf-bench.ps1 -UpdateBaseline
  ./carve-perf-bench.ps1 -Baseline ./perf-baseline.json
#>
[CmdletBinding()]
param(
    [int]$Files = 2000,
    [int]$FuncsPerFile = 30,
    [string]$Baseline,
    [switch]$UpdateBaseline,
    [double]$ThroughputDropPct = 25,
    [double]$PeakGrowthPct = 25
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# --- Locate the CLI dll (build Release if missing) --------------------------------------------------
$dll = Join-Path $root 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $dll)) {
    Write-Host 'Building CodeCarver (Release)...'
    dotnet build (Join-Path $root 'CodeCarver.sln') -c Release -v q | Out-Null
}
if (-not (Test-Path $dll)) { throw "CLI dll not found after build: $dll" }

# --- Generate a deterministic synthetic tree -------------------------------------------------------
$work = Join-Path ([IO.Path]::GetTempPath()) ("cc-perf-" + [Guid]::NewGuid().ToString('N'))
$src = Join-Path $work 'src'
New-Item -ItemType Directory -Force -Path $src | Out-Null
Write-Host "Generating $Files files x $FuncsPerFile funcs -> $src"

# Shared header included by every TU (include-closure fan-out).
$common = [Text.StringBuilder]::new()
[void]$common.AppendLine('#ifndef COMMON_H')
[void]$common.AppendLine('#define COMMON_H')
[void]$common.AppendLine('int shared_helper(int x);')
[void]$common.AppendLine('#endif')
[IO.File]::WriteAllText((Join-Path $src 'common.h'), $common.ToString())

# A large macro-DENSE register header (exercises the auto-skip path; >1 MB, ~all #defines).
$regs = [Text.StringBuilder]::new(1500000)
for ($i = 0; $i -lt 60000; $i++) { [void]$regs.Append('#define REG_').Append($i).Append(' 0x').Append($i.ToString('X6')).Append("`n") }
[IO.File]::WriteAllText((Join-Path $src 'chip_regs.h'), $regs.ToString())

$totalSrcBytes = [int64]0
for ($f = 0; $f -lt $Files; $f++) {
    $sb = [Text.StringBuilder]::new()
    [void]$sb.AppendLine('#include "common.h"')
    for ($fn = 0; $fn -lt $FuncsPerFile; $fn++) {
        $prev = $fn - 1
        if ($fn -eq 0) {
            [void]$sb.AppendLine("int f${f}_0(void){ return shared_helper($f); }")
        } else {
            [void]$sb.AppendLine("int f${f}_$fn(void){ return f${f}_$prev() + $fn; }")
        }
    }
    $path = Join-Path $src "mod$f.c"
    [IO.File]::WriteAllText($path, $sb.ToString())
    $totalSrcBytes += (Get-Item $path).Length
}
# One root TU that defines shared_helper and a main entry that chains into module roots.
$mainSb = [Text.StringBuilder]::new()
[void]$mainSb.AppendLine('#include "common.h"')
[void]$mainSb.AppendLine('int shared_helper(int x){ return x + 1; }')
[void]$mainSb.Append('int main(void){ return ')
for ($f = 0; $f -lt [Math]::Min($Files, 50); $f++) { [void]$mainSb.Append("f${f}_0() + ") }
[void]$mainSb.AppendLine('0; }')
[IO.File]::WriteAllText((Join-Path $src 'main.c'), $mainSb.ToString())

$srcMB = [Math]::Round($totalSrcBytes / 1MB, 1)
Write-Host "Source: ~$srcMB MB across $($Files + 1) TUs (+ dense header)"

# --- Run the carve, capturing wall-clock, parse-phase ms, and peak working set ----------------------
$psi = [Diagnostics.ProcessStartInfo]::new('dotnet')
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['CODECARVER_TIMING'] = '1'
# Windows PowerShell 5.1 runs on .NET Framework, which has no ProcessStartInfo.ArgumentList — use the
# single Arguments string with quoted paths.
$psi.Arguments = '"{0}" carve "{1}" --roots main' -f $dll, $src

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = [Diagnostics.Process]::Start($psi)
# Read the streams async so we can poll peak memory without deadlocking on a full pipe. PeakWorkingSet64
# is monotonic and can't be read once the process has exited, so sample it while running and keep the max.
$outTask = $proc.StandardOutput.ReadToEndAsync()
$errTask = $proc.StandardError.ReadToEndAsync()
$peakBytes = [int64]0
while (-not $proc.HasExited) {
    try { $proc.Refresh(); if ($proc.PeakWorkingSet64 -gt $peakBytes) { $peakBytes = $proc.PeakWorkingSet64 } } catch { }
    Start-Sleep -Milliseconds 50
}
$proc.WaitForExit()
$sw.Stop()
$stdout = $outTask.Result
$stderr = $errTask.Result
$peakMB = [Math]::Round($peakBytes / 1MB, 1)
$wallSec = [Math]::Round($sw.Elapsed.TotalSeconds, 2)

if ($proc.ExitCode -ne 0) {
    Write-Host $stdout; Write-Host $stderr
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    throw "carve failed (exit $($proc.ExitCode))"
}

# Parse-phase ms from the CODECARVER_TIMING 'build-graph' line: "  timing  : build-graph     12345 ms"
$parseMs = 0
$m = [Regex]::Match($stderr, 'build-graph\s+(\d+)\s+ms')
if ($m.Success) { $parseMs = [int]$m.Groups[1].Value }
$parseSec = if ($parseMs -gt 0) { [Math]::Round($parseMs / 1000.0, 2) } else { $wallSec }
$throughput = if ($parseSec -gt 0) { [Math]::Round($srcMB / $parseSec, 2) } else { 0 }

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$result = [ordered]@{
    files          = $Files + 1
    funcsPerFile   = $FuncsPerFile
    sourceMB       = $srcMB
    wallSec        = $wallSec
    parseSec       = $parseSec
    throughputMBs  = $throughput
    peakWorkingMB  = $peakMB
}
Write-Host ''
Write-Host '=== CodeCarver perf ==='
$result.GetEnumerator() | ForEach-Object { '{0,-16}: {1}' -f $_.Key, $_.Value } | Write-Host
Write-Host '======================='

# --- Baseline compare / update ---------------------------------------------------------------------
if ($UpdateBaseline) {
    $bp = if ($Baseline) { $Baseline } else { Join-Path $root 'perf-baseline.json' }
    ($result | ConvertTo-Json) | Out-File -FilePath $bp -Encoding utf8
    Write-Host "Baseline written: $bp"
    exit 0
}

if ($Baseline) {
    if (-not (Test-Path $Baseline)) { throw "baseline not found: $Baseline" }
    $base = Get-Content $Baseline -Raw | ConvertFrom-Json
    $tDropPct = if ($base.throughputMBs -gt 0) { [Math]::Round(100 * ($base.throughputMBs - $throughput) / $base.throughputMBs, 1) } else { 0 }
    $pGrowPct = if ($base.peakWorkingMB -gt 0) { [Math]::Round(100 * ($peakMB - $base.peakWorkingMB) / $base.peakWorkingMB, 1) } else { 0 }
    Write-Host ("Throughput vs baseline: {0} -> {1} MB/s ({2}% {3})" -f $base.throughputMBs, $throughput, [Math]::Abs($tDropPct), $(if ($tDropPct -gt 0) { 'slower' } else { 'faster' }))
    Write-Host ("Peak memory vs baseline: {0} -> {1} MB ({2}% {3})" -f $base.peakWorkingMB, $peakMB, [Math]::Abs($pGrowPct), $(if ($pGrowPct -gt 0) { 'more' } else { 'less' }))
    $fail = $false
    if ($tDropPct -gt $ThroughputDropPct) { Write-Host "FAIL: throughput dropped $tDropPct% (> $ThroughputDropPct%)" -ForegroundColor Red; $fail = $true }
    if ($pGrowPct -gt $PeakGrowthPct) { Write-Host "FAIL: peak memory grew $pGrowPct% (> $PeakGrowthPct%)" -ForegroundColor Red; $fail = $true }
    if ($fail) { exit 1 }
    Write-Host 'PASS: within regression thresholds' -ForegroundColor Green
}
exit 0
