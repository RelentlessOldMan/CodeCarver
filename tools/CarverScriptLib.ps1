# Shared helpers for the repo's oracle / sweep / bench scripts. Dot-source it:
#
#   . (Join-Path $PSScriptRoot 'tools\CarverScriptLib.ps1')
#   $cli = Resolve-CarverDll -RepoRoot $PSScriptRoot
#
# Windows PowerShell 5.1 compatible. Keep this file ASCII-only.

# Build the CLI incrementally and return the path of codecarver.dll, after checking that the DLL's
# informational version (1.0.<count>+<sha>[-dirty]) was stamped from the current HEAD. A stale DLL (built
# from an older commit) makes every oracle result meaningless, so a mismatch is a hard error (review SC-D1).
function Resolve-CarverDll {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
        [switch]$NoBuild
    )
    $proj = Join-Path $RepoRoot 'src\CodeCarver.Cli\CodeCarver.Cli.csproj'
    $dll  = Join-Path $RepoRoot "src\CodeCarver.Cli\bin\$Configuration\net8.0\codecarver.dll"
    $eap = $ErrorActionPreference
    # Native commands below write to stderr; under 'Stop' Windows PowerShell 5.1 turns that into a
    # terminating error even on exit 0. Gate on exit codes instead.
    $ErrorActionPreference = 'Continue'
    try {
        if (-not $NoBuild) {
            Write-Host "Building CodeCarver CLI ($Configuration, incremental)..." -ForegroundColor DarkGray
            $log = & dotnet build $proj -c $Configuration --nologo -v q 2>&1 | ForEach-Object { "$_" }
            if ($LASTEXITCODE -ne 0) {
                $log | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }
                throw "dotnet build -c $Configuration failed (exit $LASTEXITCODE)"
            }
        }
        if (-not (Test-Path $dll)) { throw "CLI dll not found: $dll (dotnet build -c $Configuration)" }

        # No `| Select-Object -First 1` here: it stops the pipeline early and leaves a bogus $LASTEXITCODE.
        $head = @(& git -C $RepoRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or $head.Count -eq 0) { throw "git rev-parse HEAD failed in $RepoRoot - cannot verify the CLI version" }
        $head = "$($head[0])".Trim()
        $dirtyNow = @(& git -C $RepoRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0

        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $dll).Path).ProductVersion
        if ("$info" -notmatch '\+([0-9a-f]{7,40})(-dirty)?$') {
            throw "CLI version '$info' carries no git SHA (expected 1.0.<count>+<sha>[-dirty]): $dll"
        }
        $dllSha = $Matches[1]; $dllDirty = [bool]$Matches[2]
        if (-not $head.StartsWith($dllSha, [StringComparison]::OrdinalIgnoreCase)) {
            throw "STALE CLI: $dll is $info but HEAD is $($head.Substring(0, 9)). Rebuild (dotnet build -c $Configuration)."
        }
        if ($dllDirty -ne $dirtyNow) {
            throw "STALE CLI: $dll is $info but the working tree is $(if ($dirtyNow) { 'dirty' } else { 'clean' }). Rebuild (dotnet build -c $Configuration)."
        }
        if ($dllDirty) { Write-Host "note: CLI built from a dirty working tree ($info)" -ForegroundColor Yellow }
        else { Write-Host "CLI: $info" -ForegroundColor DarkGray }
        return $dll
    } finally {
        $ErrorActionPreference = $eap
    }
}

# TOML basic string (review SC-D18): escape backslash, quote and control characters, so a path or a name
# containing them cannot break (or inject into) the generated config.
function ConvertTo-TomlString([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $s.ToCharArray()) {
        $c = [int]$ch
        if ($ch -eq '\') { [void]$sb.Append('\\') }
        elseif ($ch -eq '"') { [void]$sb.Append('\"') }
        elseif ($c -lt 0x20 -or $c -eq 0x7f) { [void]$sb.Append(('\u{0:X4}' -f $c)) }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# TOML array of strings. Accepts an array or a comma-separated string; blank items are dropped.
function ConvertTo-TomlArray($items) {
    $list = @()
    foreach ($i in @($items)) {
        foreach ($part in ("$i" -split ',')) { $t = $part.Trim(); if ($t) { $list += $t } }
    }
    return '[' + ((@($list | ForEach-Object { ConvertTo-TomlString $_ })) -join ', ') + ']'
}

# Path for a TOML value: forward slashes (CodeCarver accepts them on every OS), then escaped.
function ConvertTo-TomlPath([string]$p) { return ConvertTo-TomlString ($p -replace '\\', '/') }

# A C identifier (review SC-D7): anything interpolated into a generated shell script must pass this.
function Test-CIdentifier([string]$s) { return $s -match '^[A-Za-z_][A-Za-z0-9_]*$' }
