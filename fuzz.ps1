# fuzz.ps1 - the carve-fuzz loop used to harden CodeCarver against real repos.
#
# For a target repo it: (1) baseline-compiles each source UNCARVED with the pinned gcc/g++ and records
# which compile; (2) carves the repo with --prune --out; (3) re-compiles the carved version of each
# baseline-passing file. A file that compiled uncarved but FAILS after carving is a carve bug: a
# dropped symbol, a broken class/try-catch, an orphaned brace. This is exactly how the fmt / simdjson /
# pugixml / wren / janet / capstone bugs were found; every fix in git history has a matching invocation
# in docs/REPRODUCE.md so anyone can replay it.
#
#   ./fuzz.ps1 -Repo .corpus/fmt   -Roots "vformat,report_error" -Lang cpp -Inc include,src
#   ./fuzz.ps1 -Repo .corpus/cJSON -Roots "cJSON_Parse,cJSON_Delete"
#
# Requires the pinned toolchain (./fetch-toolchains.ps1) and the target repo (./fetch-corpus.ps1).
# Exit code = number of carve bugs found (0 = CLEAN, 2 = repo not compilable standalone here).
param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Roots,
    [ValidateSet('c', 'cpp')][string]$Lang = 'c',
    [string[]]$Inc = @(),           # include dirs relative to the repo (e.g. include,src)
    [string]$Std = '',              # override the C/C++ standard (default: c++17 for cpp)
    [string]$Exclude = 'test,tests,example,examples,docs,fuzz,bench,scripts'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
if (-not (Test-Path $Repo)) { throw "repo '$Repo' not found. Run ./fetch-corpus.ps1 (or pass a path)." }

$bin = Join-Path $root '.toolchains\w64devkit\bin'
$cc = if ($Lang -eq 'cpp') { Join-Path $bin 'g++.exe' } else { Join-Path $bin 'gcc.exe' }
if (-not (Test-Path $cc)) { throw "toolchain missing. Run ./fetch-toolchains.ps1" }
if (-not $Std -and $Lang -eq 'cpp') { $Std = '-std=c++17' }

$patterns = if ($Lang -eq 'cpp') { '*.cpp', '*.cc', '*.cxx' } else { '*.c' }
$out = "$Repo-carved"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$err = Join-Path $env:TEMP 'codecarver_cc_err.txt'
$outNull = Join-Path $env:TEMP 'codecarver_cc_out.txt'
$stdArg = if ($Std) { @($Std) } else { @() }

# Start-Process (not `& ... 2>file`) so g++'s stderr is captured without PowerShell 5.1 wrapping each
# line in a terminating NativeCommandError. Exit code, not $?, decides success.
function Compile($file, $incDirs) {
    $ccArgs = @($stdArg) + '-fsyntax-only' + '-w'
    foreach ($d in $incDirs) { $ccArgs += "-I$d" }
    $ccArgs += $file
    $p = Start-Process -FilePath $cc -ArgumentList $ccArgs -NoNewWindow -Wait -PassThru `
        -RedirectStandardError $err -RedirectStandardOutput $outNull
    return $p.ExitCode -eq 0
}

# 1. Baseline: which sources compile UNCARVED? Only these can reveal a carve regression.
$srcIncs = @($Inc | ForEach-Object { Join-Path $Repo $_ }) + $Repo
$baseline = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -Recurse -Path $Repo -Include $patterns -File |
    Where-Object { $_.FullName -notmatch '[\\/](test|tests|example|examples|docs|fuzz)[\\/]' } |
    ForEach-Object { if (Compile $_.FullName $srcIncs) { $baseline.Add($_.Name) } }
$baseline = $baseline | Select-Object -Unique
Write-Host "baseline: $($baseline.Count) file(s) compile uncarved"
if ($baseline.Count -eq 0) {
    Write-Host "  (0 baseline: this repo needs its own build config; not a usable fuzz target here)"
    exit 2
}

# 2. Carve + prune + emit.
Write-Host "carving..."
dotnet run --project src/CodeCarver.Cli -c Release -- carve $Repo --lang $Lang --roots $Roots `
    --prune --out $out --exclude $Exclude 2>&1 | Select-String 'emitted|none of|support' | ForEach-Object { "  $_" }

# 3. Re-compile the carved version of each baseline-passing file; a regression is a carve bug.
$carvedIncs = @($Inc | ForEach-Object { Join-Path $out $_ }) + $out
$bugs = 0
foreach ($name in $baseline) {
    $cf = Get-ChildItem -Recurse -Path $out -Filter $name -File | Select-Object -First 1
    if (-not $cf) { continue }   # the carve dropped the whole file - sound, not a bug
    if (-not (Compile $cf.FullName $carvedIncs)) {
        Write-Host "  !!! BUG: $name" -ForegroundColor Red
        Get-Content $err | Select-Object -First 6 | ForEach-Object { Write-Host "      $_" }
        $bugs++
    }
}
if ($bugs -eq 0) { Write-Host "  CLEAN" -ForegroundColor Green }
exit $bugs
