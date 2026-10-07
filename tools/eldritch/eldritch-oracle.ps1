<#
.SYNOPSIS
  The eldritch horror oracle: carve examples/eldritch every way, then BUILD and RUN each carved tree with real gcc
  (WSL) and require the exact output of the original.

.DESCRIPTION
  examples/eldritch is small but packs every nasty C construct we know of: macros hiding same-named decoy functions,
  weak/strong pairs, four kinds of alias, a function written in assembly, #if worlds decided by an out-of-root
  header / -include / @response file / the C library / a built-in, split heads, K&R, PROTO((...)), tables,
  token pasting, X-macros, a unity include, a constructor, a symbol named only by inline asm, a body from an
  #include, digraphs, _Generic, an attribute-only reference, a file compiled twice with different -D, ...

  1. The original is built in WSL under tools/capture (strace): build.log (echoed compiles) + build.trace.
     Its output is the expected output.
  2. It is carved with four input sets (log+trace, log, trace, none) at four stages (safe, headers, aggressive, max:
     every combination of carveSourceFileContents x carveHeaderFileContents).
  3. Each carved tree is built with its own build.sh and run. Carve exit must be 0, the build must succeed and the
     output must match. With log+trace the decoys must be dropped and compiled-but-unreached files must be placeholders.

  -Example hellbuild runs the same oracle on examples/hellbuild: plain code, nightmare build (make -j with interleaved
  sub-makes, generated sources, configure, wrappers, sh -c, quiet rules). Each example's oracle.txt names its binary and
  what a log+trace carve must drop or turn into placeholders.

  Not ComputeWarden-gated: a few dozen tiny compiles.

.EXAMPLE
  ./tools/eldritch/eldritch-oracle.ps1
  ./tools/eldritch/eldritch-oracle.ps1 -Example hellbuild
#>
[CmdletBinding()]
param(
    # examples\<name>: src\build.sh <src> <out> <sdk> builds it, oracle.txt names its binary and its log+trace checks
    [string]$Example = 'eldritch',
    [string]$CliDll = (Join-Path $PSScriptRoot '..\..\src\CodeCarver.Cli\bin\Debug\net8.0\codecarver.dll'),
    [switch]$SaveInputs,   # also refresh examples/eldritch/inputs (the captured log + trace CI carves with)
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) ('eldritch-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ex = Join-Path $repo "examples\$Example"
$src = Join-Path $ex 'src'
$sdk = Join-Path $ex 'sdk'
# oracle.txt: "binary <name>", "dropped <rel>" and "placeholder <rel>" (checked with log + trace, every stage).
$checks = @(Get-Content -Encoding utf8 (Join-Path $ex 'oracle.txt') | Where-Object { $_ -match '^\w+ ' } | ForEach-Object { $k, $v = $_ -split ' ', 2; [pscustomobject]@{ Kind = $k; Rel = $v } })
$bin = ($checks | Where-Object Kind -eq 'binary' | Select-Object -First 1).Rel
if (-not $bin) { throw "$ex\oracle.txt names no binary" }
if (-not (Test-Path $CliDll)) { throw "CLI not built: $CliDll (dotnet build src/CodeCarver.Cli)" }
New-Item -ItemType Directory -Force $Work | Out-Null
$Work = (Resolve-Path $Work).Path

function ToWsl([string]$p) { $f = [IO.Path]::GetFullPath($p); '/mnt/' + $f.Substring(0, 1).ToLowerInvariant() + ($f.Substring(2) -replace '\\', '/') }
function Invoke-Wsl([string]$cmd) { $ErrorActionPreference = 'Continue'; $o = & wsl.exe -e bash -lc $cmd 2>&1; return @{ Code = $LASTEXITCODE; Out = ($o | Out-String) } }

$srcW = ToWsl $src; $sdkW = ToWsl $sdk; $workW = ToWsl $Work
$capture = ToWsl (Join-Path $repo 'tools\capture\capture-file-trace.sh')

# 1. The original, traced.
$r = Invoke-Wsl "set -e; cd '$workW'; rm -rf /tmp/$Example-orig; bash '$capture' build.trace -- sh '$srcW/build.sh' '$srcW' /tmp/$Example-orig '$sdkW' > build.log 2>&1; /tmp/$Example-orig/$bin > expected.txt"
if ($SaveInputs -and $r.Code -eq 0) {
    # examples/eldritch/inputs: CI carves with these (no gcc there). They name only /mnt/c/... repo paths, /tmp and /usr.
    $in = Join-Path $ex 'inputs'
    New-Item -ItemType Directory -Force $in | Out-Null
    foreach ($n in 'build.log', 'build.trace', 'expected.txt') { Copy-Item (Join-Path $Work $n) (Join-Path $in $n) -Force }
    Write-Host "saved build.log, build.trace, expected.txt to $in"
}
if ($r.Code -ne 0) { throw "original build failed:`n$($r.Out)`n$(Get-Content (Join-Path $Work 'build.log') -Raw)" }
$expected = Get-Content (Join-Path $Work 'expected.txt') -Raw
Write-Host "original: $((Get-Content (Join-Path $Work 'expected.txt')).Count) output line(s); trace $((Get-Content (Join-Path $Work 'build.trace')).Count) path(s)"

$fail = 0
$rows = @()
foreach ($mode in 'log+trace', 'log', 'trace', 'none') {
    $out = Join-Path $Work ("out-" + ($mode -replace '\+', '-'))
    $builds = ''
    if ($mode -ne 'none') {
        $builds = "[builds.b]`n"
        if ($mode -match 'log') { $builds += "buildLogs = [`"$(($Work -replace '\\','/'))/build.log`"]`n" }
        if ($mode -match 'trace') { $builds += "buildTraceFiles = [`"$(($Work -replace '\\','/'))/build.trace`"]`n" }
    }
    $cfg = Join-Path $Work "carve-$($mode -replace '\+','-').toml"
    @"
outputDirectory = "$($out -replace '\\','/')"
[common]
entryPoints = ["main"]
languages = ["c"]
$builds
[advanced]
pathMap = [{ from = "$srcW", to = "." }, { from = "$sdkW", to = "../sdk" }]
[stages.safe]
carveSourceFileContents = false
carveHeaderFileContents = false
[stages.headers]
carveSourceFileContents = false
carveHeaderFileContents = true
[stages.aggressive]
carveSourceFileContents = true
carveHeaderFileContents = false
[stages.max]
carveSourceFileContents = true
carveHeaderFileContents = true
"@ | Set-Content -Encoding utf8 $cfg
    $ErrorActionPreference = 'Continue'
    $carve = @(& dotnet $CliDll carve $src --config $cfg 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    foreach ($stage in 'safe', 'headers', 'aggressive', 'max') {
        $carved = Join-Path $out "$stage\carved"
        $row = [ordered]@{ mode = $mode; stage = $stage; carve = $code; verify = '-'; build = '-'; output = '-'; notes = '' }
        # Exit 3 = verify failed: the tree was still emitted, so build it anyway. A verify FAIL that builds is a
        # false alarm; a verify OK that does not build is a silent miss - the worst kind. Both count.
        if ($code -ne 0 -and $code -ne 3) {
            $row.notes = 'carve failed'; $fail++
            $rows += [pscustomobject]$row
            continue
        }
        $vlog = Join-Path $out "$stage\codecarver\verify.txt"
        $vfails = if (Test-Path $vlog) { @(Get-Content $vlog | Where-Object { $_ -match '^FAIL ' }).Count } else { 0 }
        if ($vfails -gt 0) { $row.verify = "FAIL $vfails"; $fail++ } else { $row.verify = 'ok' }
        $tag = ($mode -replace '\+', '-') + "-$stage"
        $cW = ToWsl $carved
        $b = Invoke-Wsl "cd '$workW'; rm -rf 'b-$tag'; sh '$cW/build.sh' '$cW' 'b-$tag' '$sdkW' > 'b-$tag.log' 2>&1 && ./b-$tag/$bin > 'b-$tag.txt'"
        if ($b.Code -ne 0) {
            $row.build = 'FAIL'; $fail++
            $errs = @(Get-Content (Join-Path $Work "b-$tag.log") | Where-Object { $_ -match 'error|undefined reference' } | Select-Object -First 4)
            $row.notes = ($errs -join ' | ')
        }
        else {
            $row.build = 'ok'
            $got = Get-Content (Join-Path $Work "b-$tag.txt") -Raw
            if ($got -eq $expected) { $row.output = 'same' }
            else {
                $row.output = 'DIFFERENT'; $fail++
                $diff = @(Compare-Object ($expected -split "`n") ($got -split "`n") | Where-Object SideIndicator -eq '=>' | ForEach-Object { $_.InputObject.Trim() })
                $row.notes = 'got: ' + ($diff -join ' | ')
            }
        }
        if ($mode -eq 'log+trace') {
            $bad = @()
            foreach ($d in ($checks | Where-Object Kind -eq 'dropped').Rel) { if (Test-Path -LiteralPath (Join-Path $carved $d)) { $bad += "$d kept" } }
            foreach ($p in ($checks | Where-Object Kind -eq 'placeholder').Rel) {
                $f = Join-Path $carved $p
                if (-not (Test-Path -LiteralPath $f) -or -not ((Get-Content -LiteralPath $f -Raw) -match 'Placeholder written by CodeCarver')) { $bad += "$p not a placeholder" }
            }
            if ($bad.Count -gt 0) { $row.notes = ($row.notes + ' ' + ($bad -join ', ')).Trim(); $fail++ }
        }
        $rows += [pscustomobject]$row
    }
    if ($code -ne 0 -and $code -ne 3) { Write-Host "--- carve ($mode) exit ${code}:"; $carve | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" } }
}
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if ($fail -gt 0) { Write-Host "ELDRITCH: FAIL ($fail problem(s)); work dir $Work"; exit 1 }
Write-Host "ELDRITCH: PASS - every carve of the horror builds and prints the original's output"
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
exit 0
