# Makes -To an exact copy of -From, copying only the files whose size or last-write time differ and deleting the
# ones -From no longer has. Use it to carve a local copy of a tree that lives on a slow share:
#
#   1. Copy the tree in (the first time copies everything; later only what changed):
#        powershell -ExecutionPolicy Bypass -File tools\mirror\mirror.ps1 -From \\server\share\tree -To D:\local\tree
#   2. Carve the local copy, with the share named in the config so the run first checks the copy still matches:
#        [advanced]
#        mirrorOf = "//server/share/tree"
#        pathMap  = [{ from = "//server/share/tree", to = "." }]   # if the build log / traces name share paths
#   3. Copy the output back, if it must live on the share (again only what changed):
#        powershell -ExecutionPolicy Bypass -File tools\mirror\mirror.ps1 -From D:\local\out -To \\server\share\out
#
# Uses robocopy (part of Windows). Last-write times are copied too: they are how the next copy and the carve's
# mirrorOf check tell an unchanged file. Exits 0 when -To matches -From, 1 otherwise.
#
# -To is made to match -From exactly: files only -To has are DELETED. So the first copy into a directory needs it
# empty or missing (or -Force); it then leaves <To>.mirror-of.txt beside it, and later copies to it need no -Force.
# Windows PowerShell 5.1 or later; keep this file ASCII (5.1 misreads non-ASCII without a BOM).
param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To,
    # Directory names to leave out on both sides, as excludeDirectories in the carve config.
    [string[]]$ExcludeDirs = @(),
    [int]$Threads = 16,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $From -PathType Container)) { Write-Host "-From '$From' is not a directory (or can't be reached)."; exit 1 }
$From = (Resolve-Path -LiteralPath $From).ProviderPath.TrimEnd('\')
$To = [System.IO.Path]::GetFullPath($To).TrimEnd('\')
$fromSlash = $From + '\'; $toSlash = $To + '\'
if ($toSlash.StartsWith($fromSlash, [StringComparison]::OrdinalIgnoreCase) -or $fromSlash.StartsWith($toSlash, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "-From and -To overlap; they must be separate trees."; exit 1
}

$marker = $To + '.mirror-of.txt'
if ((Test-Path -LiteralPath $To) -and -not (Test-Path -LiteralPath $marker) -and -not $Force) {
    if (Get-ChildItem -LiteralPath $To -Force | Select-Object -First 1) {
        Write-Host "'$To' is not empty and was not made by this script: its files not in '$From' would be deleted."
        Write-Host "Choose another -To, or pass -Force if that is what you want."
        exit 1
    }
}
New-Item -ItemType Directory -Force -Path $To | Out-Null
Set-Content -LiteralPath $marker -Encoding ASCII -Value "Mirror of $From, made by tools\mirror\mirror.ps1. Last copied $(Get-Date -Format s)."

# /MIR copy and delete; /COPY:DAT and /DCOPY:DAT keep last-write times; /FFT counts times 2 s apart as the same, as
# some shares keep them; /R /W retry a dropped connection; the rest keep the log to the summary.
$rc = @($From, $To, '/MIR', '/COPY:DAT', '/DCOPY:DAT', '/FFT', "/MT:$Threads", '/R:3', '/W:5', '/NP', '/NFL', '/NDL')
if ($ExcludeDirs.Count -gt 0) { $rc += '/XD'; $rc += $ExcludeDirs }
$clock = [Diagnostics.Stopwatch]::StartNew()
& robocopy @rc
$code = $LASTEXITCODE
Write-Host ("mirror: {0:N0} s" -f $clock.Elapsed.TotalSeconds)
# robocopy: 0-7 is success (bits for copied / extra / mismatched), 8 and up is a failure.
if ($code -ge 8) { Write-Host "mirror: robocopy failed (exit $code); '$To' may be partly copied. Run it again."; exit 1 }
exit 0
