<#
.SYNOPSIS
  T2 truth check (docs/TESTING.md, "Coverage plan"): carve CodeSpawner's firmware corpus with header contents carved
  and check the result against the truth in its manifest.

.DESCRIPTION
  Generates the corpus with the pinned tools/codespawner/codespawner.exe (or takes one already generated), verifies
  it, carves its fw/ root twice with every file forced kept (the truth is about the header carve alone) and fails when:
    - a mustKeepDefines name is not defined in the carved tree, or a mustDropDefines name is;
    - a mustKeepFiles file is missing;
    - a header-carve count differs from the manifest's exact counts, or definesDropped / bytesAfter fall outside
      its bounds;
    - the two carves differ (emittedFilesSha or any headerCarve count);
    - a unit of the compile db does not compile against the carved tree with -std=c11 -Wall -Werror (each compiler
      given in -Compilers that is found; -RequireCompilers fails when one is not).
  Exit 0 = pass, 1 = a check failed, 2 = bad input.

.EXAMPLE
  ./tools/firmware-truth/firmware-truth.ps1 -Work C:\t2 -Exe src\CodeCarver.Cli\bin\Release\net8.0\codecarver.exe
#>
param(
    [Parameter(Mandatory)] [string] $Work,          # scratch dir: corpus (unless -Corpus), carves, logs
    [string] $Exe,                                  # codecarver.exe; or -CliDll for `dotnet <dll>`
    [string] $CliDll,
    [string] $Corpus,                               # an already generated corpus (skips gen)
    [double] $Scale = 0.01,
    [string[]] $Compilers = @('gcc', 'clang'),
    [switch] $RequireCompilers,
    [int] $MemoryStopMb = 4000
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$fails = New-Object System.Collections.Generic.List[string]
function Fail($m) { $fails.Add($m); Write-Host "FAIL $m" }
function Run-Carver([string[]] $a) {
    if ($Exe) { & $Exe @a | Out-Host } elseif ($CliDll) { & dotnet $CliDll @a | Out-Host } else { throw "give -Exe or -CliDll" }
    return $LASTEXITCODE
}

New-Item -ItemType Directory -Force $Work | Out-Null
$Work = (Resolve-Path $Work).Path
if (-not $Corpus) {
    $Corpus = Join-Path $Work 'corpus'
    if (Test-Path $Corpus) { Remove-Item -Recurse -Force $Corpus }
    $spawner = Join-Path $repo 'tools/codespawner/codespawner.exe'
    & $spawner gen --out $Corpus --preset firmware --scale $Scale
    if ($LASTEXITCODE -ne 0) { Write-Host "codespawner gen failed ($LASTEXITCODE)"; exit 2 }
}
$Corpus = (Resolve-Path $Corpus).Path
$manifestPath = @((Join-Path $Corpus 'manifest.json'), "$Corpus-manifest.json") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $manifestPath) { Write-Host "no manifest for $Corpus"; exit 2 }
$fwm = (Get-Content -Raw $manifestPath | ConvertFrom-Json).firmware
if (-not $fwm) { Write-Host "$manifestPath has no firmware object"; exit 2 }

# verify recomputes the truth and treeSha256 from the tree: a stale or damaged copy fails here, not as a carve miss.
& (Join-Path $repo 'tools/codespawner/codespawner.exe') verify --corpus $Corpus --manifest $manifestPath | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { Write-Host "codespawner verify failed: the corpus does not match its manifest"; exit 2 }

$root = Join-Path $Corpus $fwm.root
$db = Join-Path $Corpus $fwm.compileDb
$fwd = { param($p) $p -replace '\\', '/' }
$shas = @()
$counts = @()
foreach ($run in 1, 2) {
    $out = Join-Path $Work "carve$run"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    $toml = Join-Path $Work "carve$run.toml"
    @"
outputDirectory = '$(& $fwd $out)'
[common]
entryPoints = ["fu0"]   # any unit function: every file is forced kept, so each is a root anyway
forceKeepFiles = ["*"]
[builds.main]
buildLogs = ['$(& $fwd $db)']
[stages.hdr]
carveHeaderFileContents = true
[advanced]
maxParseBytes = $($fwm.maxParseBytes)
memoryStopMb = $MemoryStopMb
"@ | Set-Content -Encoding ascii $toml
    $code = Run-Carver @('carve', $root, '--config', $toml)
    if ($code -ne 0) { Fail "carve $run exited $code"; continue }
    $sum = @{}
    foreach ($l in Get-Content (Join-Path $out 'hdr/codecarver/summary.txt')) {
        if ($l -match '^(\S+) = (.*)$') { $sum[$Matches[1]] = $Matches[2] }
    }
    $shas += $sum['stage0.emittedFilesSha']
    $counts += (($sum.Keys | Where-Object { $_ -like 'stage0.headerCarve.*' -and $_ -notmatch '(ms|Ms|Mb)$' } | Sort-Object | ForEach-Object { "$_=$($sum[$_])" }) -join ';')
    if ($run -eq 1) { $first = $sum }
}
if ($fails.Count) { Write-Host "T2 FAILED: $($fails.Count) problem(s)"; exit 1 }
if ($shas[0] -ne $shas[1]) { Fail "two carves differ: emittedFilesSha $($shas[0]) vs $($shas[1])" }
if ($counts[0] -ne $counts[1]) { Fail "two carves differ in their headerCarve counts" }

# Counts: exact ones equal, the over-keep inside its bounds.
$c = $fwm.counts
$hc = { param($k) [long]$first["stage0.headerCarve.$k"] }
foreach ($k in 'bigHeadersKept', 'wholeIncludedFromOutside', 'pasteMacros', 'forwardingMacros', 'bytesBefore') {
    if ((& $hc $k) -ne [long]$c.$k) { Fail "count $k = $(& $hc $k), truth $($c.$k)" }
}
$total = (& $hc 'definesKept') + (& $hc 'definesDropped')
if ($total -ne [long]$c.totalDefineLines) { Fail "definesKept + definesDropped = $total, truth totalDefineLines $($c.totalDefineLines)" }
$dropped = & $hc 'definesDropped'
if ($dropped -lt [long]$c.definesDroppedMin -or $dropped -gt [long]$c.definesDroppedMax) { Fail "definesDropped $dropped outside [$($c.definesDroppedMin), $($c.definesDroppedMax)]" }
$after = & $hc 'bytesAfter'
if ($after -lt [long]$c.bytesAfterMin -or $after -gt [long]$c.bytesAfterMax) { Fail "bytesAfter $after outside [$($c.bytesAfterMin), $($c.bytesAfterMax)]" }

# Names: every define in the carved tree against the union lists (Carver keeps the union of its builds).
$carved = Join-Path $Work 'carve1/hdr/carved'
$defined = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
# As the preprocessor reads lines: a lone CR ends one, a splice (with spaces before its newline) joins two, and a
# closed comment around the # is a space (/* c */ #define X, # /* c */ define X).
$rx = [regex]'(?m)^[ \t]*(?:/\*.*?\*/[ \t]*)*(?:#|%:)[ \t]*(?:/\*.*?\*/[ \t]*)*define[ \t]+([A-Za-z_]\w*)'
$latin1 = [Text.Encoding]::GetEncoding(28591)
foreach ($f in Get-ChildItem -Recurse -File -Force $carved) {
    $text = [IO.File]::ReadAllText($f.FullName, $latin1) -replace '\r\n?', "`n" -replace '\\[ \t]*\n', ''
    foreach ($m in $rx.Matches($text)) { [void]$defined.Add($m.Groups[1].Value) }
}
function Read-List($entry) {
    $p = Join-Path $Corpus $entry.path
    if ($p.EndsWith('.gz')) {
        $gz = New-Object IO.Compression.GZipStream ([IO.File]::OpenRead($p)), ([IO.Compression.CompressionMode]::Decompress)
        $t = (New-Object IO.StreamReader $gz).ReadToEnd(); $gz.Dispose()
    } else { $t = [IO.File]::ReadAllText($p) }
    $l = @($t -split "`n" | Where-Object { $_ })
    if ($l.Count -ne [int]$entry.count) { Fail "$($entry.path): $($l.Count) names, manifest says $($entry.count)" }
    return $l
}
$u = $fwm.builds.union
$missK = @(Read-List $u.mustKeepDefines | Where-Object { -not $defined.Contains($_) })
$keptD = @(Read-List $u.mustDropDefines | Where-Object { $defined.Contains($_) })
$missF = @(Read-List $u.mustKeepFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $carved $_)) })
if ($missK.Count) { Fail "$($missK.Count) mustKeep define(s) missing, e.g. $(($missK | Select-Object -First 10) -join ' ')" }
if ($missK.Count) { $missK | Set-Content -Encoding ascii (Join-Path $Work "missing-mustKeep.txt") }
if ($keptD.Count) { Fail "$($keptD.Count) mustDrop define(s) kept, e.g. $(($keptD | Select-Object -First 10) -join ' ')" }
if ($missF.Count) { Fail "$($missF.Count) mustKeep file(s) missing, e.g. $(($missF | Select-Object -First 5) -join ' ')" }

# Compile every unit against the carved tree: the db's flags with the root swapped for the carved copy.
$entries = Get-Content -Raw $db | ConvertFrom-Json
foreach ($cc in $Compilers) {
    if (-not (Get-Command $cc -ErrorAction SilentlyContinue)) {
        if ($RequireCompilers) { Fail "compiler $cc not found" } else { Write-Host "skip  $cc (not found)" }
        continue
    }
    $bad = 0
    foreach ($e in $entries) {
        $a = @($e.arguments | Select-Object -Skip 1 | ForEach-Object { $_.Replace($root, $carved) })
        $a = @($a | Where-Object { $_ -ne '-c' }) + @('-Wall', '-Werror', '-fsyntax-only')
        $o = & $cc @a 2>&1
        if ($LASTEXITCODE -ne 0) {
            $bad++
            if ($bad -le 3) { Write-Host "  $cc $($e.file): $(($o | Select-Object -First 3) -join ' | ')" }
        }
    }
    if ($bad) { Fail "$cc`: $bad of $($entries.Count) unit(s) do not compile against the carved tree" }
    else { Write-Host "ok    $cc compiles all $($entries.Count) units" }
}

Write-Host ("defines kept {0:N0}, dropped {1:N0} (truth: dropped {2:N0}..{3:N0}); mustKeep {4:N0}, mustDrop {5:N0}" -f `
    (& $hc 'definesKept'), $dropped, [long]$c.definesDroppedMin, [long]$c.definesDroppedMax, [long]$c.mustKeep, [long]$c.mustDrop)
if ($fails.Count) { Write-Host "T2 FAILED: $($fails.Count) problem(s)"; exit 1 }
Write-Host "T2 PASSED"
exit 0
