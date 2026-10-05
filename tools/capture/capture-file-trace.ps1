<#
.SYNOPSIS
  Capture a CodeCarver file-access trace on Windows (via Sysinternals Process Monitor), then consolidate it.

  A file-access trace is the list of files the OS actually opened under your repo during a build or a run.
  ProcMon records EVERY process on the machine to a backing .pml file; when the capture stops, this script
  exports it and reduces it to what a carve uses:
    1. only events from the BUILD's process tree (or, for a run capture, the processes you name and their
       children) - an indexer, antivirus scan, IDE or `git status` touching the repo during the build must not
       make files "observed"
    2. only SUCCESSFUL opens for READING, images loaded, and programs started (attribute probes, writes,
       directory opens and failures are not dependencies)
    3. DEDUPLICATED to the unique set of paths, one per line
  The .pml and the full CSV are deleted afterwards (keep them with -Raw). Note the .pml holds every process's
  activity and command line while it exists.

  Needs ADMINISTRATOR rights (ProcMon loads a driver). Capture a CLEAN, FULL build - an incremental build
  opens almost nothing. If the build fails, the trace is still written (it may be partial) and the script
  exits with the build's exit code.

.PARAMETER Out
  Output path (what you put in buildTraceFiles / runTraceFiles). A deduplicated one-path-per-line list.

.PARAMETER Build
  A build command line to wrap; it runs under cmd.exe and only its process tree is kept.

.PARAMETER ProcessName
  RUN capture: the image name(s) whose activity to keep, with their child processes (e.g. 't32marm.exe').
  You perform the session yourself and press Enter to stop.

.PARAMETER AllProcesses
  RUN capture without -ProcessName: keep EVERY process's file activity. Only for a quiet machine.

.PARAMETER Procmon
  Path to Process Monitor (default: Procmon.exe on PATH). Get it from https://learn.microsoft.com/sysinternals.

.PARAMETER Raw
  Also keep the full ProcMon CSV (<Out>.full.csv) and the backing file (<Out>.pml), for debugging.

.PARAMETER ConsolidateOnly
  Re-consolidate an existing ProcMon CSV export instead of capturing (with -RootPid or -ProcessName).

.PARAMETER RootPid
  With -ConsolidateOnly: the PID(s) whose process tree to keep.

.EXAMPLE
  ./capture-file-trace.ps1 -Out build.trace -Build 'msbuild /m firmware.sln'

.EXAMPLE
  ./capture-file-trace.ps1 -Out run.trace -ProcessName t32marm.exe
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Build = '',
    [string[]]$ProcessName = @(),
    [switch]$AllProcesses,
    [string]$Procmon = 'Procmon.exe',
    [switch]$Raw,
    [string]$ConsolidateOnly = '',
    [int[]]$RootPid = @()
)
$ErrorActionPreference = 'Stop'

# Resolve against the PowerShell location ($PWD), not the process working directory.
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)

function Get-Consolidated([string]$csv, [int[]]$roots, [string[]]$names, [bool]$all) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($csv, [System.Text.Encoding]::UTF8)
    try {
        $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
        $parser.SetDelimiters(',')
        $parser.HasFieldsEnclosedInQuotes = $true
        $header = $parser.ReadFields()
        $col = @{}
        for ($i = 0; $i -lt $header.Length; $i++) { $col[$header[$i].Trim()] = $i }
        foreach ($need in 'Process Name', 'PID', 'Operation', 'Path', 'Result', 'Detail') {
            if (-not $col.ContainsKey($need)) {
                throw "the ProcMon CSV has no '$need' column (columns: $($header -join ', ')). Reset ProcMon's columns to the defaults."
            }
        }
        $tree = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($r in $roots) { [void]$tree.Add($r) }
        $nameSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($n in $names) { [void]$nameSet.Add($n) }
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $paths = [System.Collections.Generic.List[string]]::new()
        while (-not $parser.EndOfData) {
            $f = $parser.ReadFields()
            if ($f.Length -lt $header.Length) { continue }
            $procId = 0
            [void][int]::TryParse($f[$col['PID']], [ref]$procId)
            $op = $f[$col['Operation']]
            $detail = $f[$col['Detail']]
            if (-not $all) {
                if ($nameSet.Count -gt 0 -and $nameSet.Contains($f[$col['Process Name']])) { [void]$tree.Add($procId) }
                if (-not $tree.Contains($procId)) { continue }
                # A process in the tree that starts another adds the child to the tree.
                if ($op -eq 'Process Create' -and $detail -match 'PID:\s*(\d+)') { [void]$tree.Add([int]$Matches[1]) }
            }
            if ($f[$col['Result']] -ne 'SUCCESS') { continue }
            $p = $f[$col['Path']]
            if (-not $p) { continue }
            $keep = switch ($op) {
                'ReadFile' { $true }
                'Load Image' { $true }
                'Process Create' { $true }
                'CreateFile' {
                    ($detail -notmatch 'Options:[^,]*Directory File') -and
                    ($detail -match 'Desired Access:[^,]*(Read Data|Generic Read|Execute|All Access|Generic All)')
                }
                default { $false }
            }
            if ($keep -and $seen.Add($p)) { $paths.Add($p) }
        }
        return , $paths
    }
    finally { $parser.Close() }
}

function Write-Trace([System.Collections.Generic.List[string]]$paths, [int]$exitCode) {
    if ($paths.Count -eq 0) {
        Write-Host "error: the capture produced 0 paths - nothing from the traced processes was recorded (stale ProcMon filter? wrong -ProcessName?)" -ForegroundColor Red
        exit 3
    }
    [System.IO.File]::WriteAllLines($Out, $paths, [System.Text.UTF8Encoding]::new($false))
    Write-Host "wrote $Out ($($paths.Count) unique path(s))" -ForegroundColor Green
    if ($exitCode -ne 0) { Write-Host "warning: the build exited with code $exitCode - the trace may be PARTIAL" -ForegroundColor Yellow }
    Write-Host "add it to carve.toml:  buildTraceFiles = ['$Out']   (or runTraceFiles for a run capture; single quotes = TOML literal string)"
}

if ($ConsolidateOnly) {
    if (-not $RootPid -and -not $ProcessName -and -not $AllProcesses) { throw "-ConsolidateOnly needs -RootPid, -ProcessName or -AllProcesses" }
    $paths = Get-Consolidated $ConsolidateOnly $RootPid $ProcessName $AllProcesses.IsPresent
    Write-Trace $paths 0
    exit 0
}

if (-not $Build -and -not $ProcessName -and -not $AllProcesses) {
    throw "give -Build '<command>' for a build capture, or -ProcessName <image.exe> (or -AllProcesses) for a run capture"
}
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Process Monitor needs administrator rights - run this from an elevated PowerShell."
}
$pm = Get-Command $Procmon -ErrorAction SilentlyContinue
if (-not $pm) { throw "Process Monitor not found ('$Procmon'). Install Sysinternals ProcMon and pass -Procmon <path>, or put it on PATH." }
$Procmon = $pm.Source

$pml = "$Out.pml"
$fullCsv = "$Out.full.csv"
$exitCode = 0
$roots = @()
try {
    Write-Host "starting capture -> $pml" -ForegroundColor Cyan
    Start-Process $Procmon -ArgumentList '/AcceptEula', '/Quiet', '/Minimized', '/NoFilter', '/BackingFile', "`"$pml`""
    & $Procmon /WaitForIdle | Out-Null
    try {
        if ($Build) {
            Write-Host "capturing build: $Build" -ForegroundColor Cyan
            $p = Start-Process cmd.exe -ArgumentList '/d', '/s', '/c', "`"$Build`"" -NoNewWindow -PassThru
            $roots = @($p.Id)
            $p.WaitForExit()
            $exitCode = $p.ExitCode
        }
        else {
            Read-Host "Capturing. Run your session now (flash/debug/run), then press Enter to stop" | Out-Null
        }
    }
    finally {
        Write-Host "stopping capture..." -ForegroundColor Cyan
        & $Procmon /Terminate | Out-Null
        # /Terminate returns before the backing file is closed: wait for every ProcMon process to exit.
        for ($i = 0; $i -lt 120 -and (Get-Process -Name 'Procmon*' -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    }

    Write-Host "consolidating..." -ForegroundColor Cyan
    $conv = Start-Process $Procmon -ArgumentList '/OpenLog', "`"$pml`"", '/SaveAs', "`"$fullCsv`"" -PassThru
    $conv.WaitForExit()
    if (-not (Test-Path $fullCsv)) { throw "ProcMon did not export $fullCsv" }
    $paths = Get-Consolidated $fullCsv $roots $ProcessName $AllProcesses.IsPresent
    Write-Trace $paths $exitCode
}
finally {
    if ($Raw) { Write-Host "kept raw ProcMon CSV -> $fullCsv  and backing file -> $pml" -ForegroundColor DarkGray }
    else {
        Remove-Item -LiteralPath $pml -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $fullCsv -ErrorAction SilentlyContinue
    }
}
exit $exitCode
