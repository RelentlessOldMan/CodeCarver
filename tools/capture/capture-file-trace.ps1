<#
.SYNOPSIS
  Capture a CodeCarver file-access trace on Windows (via Sysinternals Process Monitor).

  A file-access trace is the list of files the OS actually opened under your repo during a build or a run.
  Feed the resulting CSV to a carve.toml:  buildTraceFiles = ["build.csv"]  /  runTraceFiles = ["run.csv"].
  You do NOT need to filter it — CodeCarver keeps only paths under the carve root, so a raw full capture is fine.

.PARAMETER Out
  Output CSV path (what you put in buildTraceFiles / runTraceFiles).

.PARAMETER Build
  A build command to wrap (its file opens become the trace). Omit for an interactive RUN capture, where you
  perform the session yourself (e.g. a TRACE32 flash/debug) and press Enter to stop.

.PARAMETER Procmon
  Path to Process Monitor (default: Procmon.exe on PATH). Get it from https://learn.microsoft.com/sysinternals.

.EXAMPLE
  # BUILD trace — wrap your build:
  ./capture-file-trace.ps1 -Out build.csv -Build 'make -n'

.EXAMPLE
  # RUN trace — do the TRACE32 flash/debug session yourself, then press Enter:
  ./capture-file-trace.ps1 -Out run.csv
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Build = '',
    [string]$Procmon = 'Procmon.exe'
)
$ErrorActionPreference = 'Stop'

if (-not (Get-Command $Procmon -ErrorAction SilentlyContinue)) {
    throw "Process Monitor not found ('$Procmon'). Install Sysinternals ProcMon and pass -Procmon <path>, or put it on PATH."
}

$Out = [System.IO.Path]::GetFullPath($Out)
$pml = [System.IO.Path]::ChangeExtension($Out, '.pml')
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

# Convert the binary .pml to the CSV CodeCarver reads.
& $Procmon /OpenLog "`"$pml`"" /SaveAs "`"$Out`"" | Out-Null
Remove-Item $pml -ErrorAction SilentlyContinue
Write-Host "wrote $Out" -ForegroundColor Green
Write-Host "add it to carve.toml:  buildTraceFiles = [`"$Out`"]   (or runTraceFiles for a run capture)"
