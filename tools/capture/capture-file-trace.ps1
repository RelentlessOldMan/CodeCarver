<#
.SYNOPSIS
  Capture a CodeCarver file-access trace on Windows (via Sysinternals Process Monitor) — CONSOLIDATED.

  A file-access trace is the list of files the OS actually opened under your repo during a build or a run.
  A full ProcMon capture of a build is huge and mostly redundant: the compiler re-opens the same headers once
  per translation unit, and records many failed/unrelated accesses. This script reduces the capture to what a
  carve actually uses:
    1. keep only SUCCESSFUL file opens (CreateFile / ReadFile with result SUCCESS)
    2. DEDUPLICATE to the unique set of paths, one per line
  That is lossless for carving — CodeCarver only cares about the SET of files touched — and turns a multi-
  hundred-MB CSV into a small path list. Paths are NOT restricted to the repo (CodeCarver filters to the carve
  root itself, and uses "a source file opened OUTSIDE the root" as a missing-dependency signal).

.PARAMETER Out
  Output path (what you put in buildTraceFiles / runTraceFiles). A deduplicated one-path-per-line list.

.PARAMETER Build
  A build command to wrap (its file opens become the trace). Omit for an interactive RUN capture, where you
  perform the session yourself (e.g. a TRACE32 flash/debug) and press Enter to stop. Use a REAL build.

.PARAMETER Procmon
  Path to Process Monitor (default: Procmon.exe on PATH). Get it from https://learn.microsoft.com/sysinternals.

.PARAMETER Raw
  Also keep the full intermediate ProcMon CSV (as <Out>.full.csv) and the .pml backing file, for debugging.

.EXAMPLE
  # BUILD trace — wrap your real build:
  ./capture-file-trace.ps1 -Out build.trace -Build 'msbuild /m firmware.sln'

.EXAMPLE
  # RUN trace — do the TRACE32 flash/debug session yourself, then press Enter:
  ./capture-file-trace.ps1 -Out run.trace
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Build = '',
    [string]$Procmon = 'Procmon.exe',
    [switch]$Raw
)
$ErrorActionPreference = 'Stop'

if (-not (Get-Command $Procmon -ErrorAction SilentlyContinue)) {
    throw "Process Monitor not found ('$Procmon'). Install Sysinternals ProcMon and pass -Procmon <path>, or put it on PATH."
}

$Out = [System.IO.Path]::GetFullPath($Out)
$pml = [System.IO.Path]::ChangeExtension($Out, '.pml')
$fullCsv = "$Out.full.csv"
Write-Host "starting capture -> $pml" -ForegroundColor Cyan
# Launch ProcMon capturing to a backing file and RETURN (it runs in the background until /Terminate).
Start-Process $Procmon -ArgumentList '/AcceptEula', '/Quiet', '/Minimized', '/BackingFile', "`"$pml`""
Start-Sleep -Milliseconds 800

try {
    if ($Build) {
        Write-Host "capturing build: $Build" -ForegroundColor Cyan
        Invoke-Expression $Build
    }
    else {
        Read-Host "Capturing. Run your session now (flash/debug/run), then press Enter to stop" | Out-Null
    }
}
finally {
    Write-Host "stopping capture..." -ForegroundColor Cyan
    & $Procmon /Terminate | Out-Null
}

# Convert the binary .pml to a CSV, then consolidate that CSV into a deduped path list.
Write-Host "consolidating..." -ForegroundColor Cyan
& $Procmon /OpenLog "`"$pml`"" /SaveAs "`"$fullCsv`"" | Out-Null

$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$writer = [System.IO.StreamWriter]::new($Out, $false)
try {
    # Import-Csv streams row-by-row; we keep only successful file opens and hold just the unique paths.
    Import-Csv $fullCsv | ForEach-Object {
        if (($_.Result -eq 'SUCCESS') -and ($_.Operation -eq 'CreateFile' -or $_.Operation -eq 'ReadFile')) {
            $p = $_.Path
            if ($p -and $seen.Add($p)) { $writer.WriteLine($p) }
        }
    }
}
finally { $writer.Dispose() }

if ($Raw) {
    Write-Host "kept raw ProcMon CSV -> $fullCsv  and backing file -> $pml" -ForegroundColor DarkGray
}
else {
    Remove-Item $pml -ErrorAction SilentlyContinue
    Remove-Item $fullCsv -ErrorAction SilentlyContinue
}

Write-Host "wrote $Out ($($seen.Count) unique path(s))" -ForegroundColor Green
Write-Host "add it to carve.toml:  buildTraceFiles = [`"$Out`"]   (or runTraceFiles for a run capture)"
