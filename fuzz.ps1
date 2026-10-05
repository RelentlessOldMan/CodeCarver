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
# Exit code: 0 = CLEAN, 1 = carve bug(s) found (count printed), 2 = repo not compilable standalone here,
#            3 = the carve itself failed.
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
. (Join-Path $root 'tools\CarverScriptLib.ps1')
if (-not (Test-Path $Repo)) { throw "repo '$Repo' not found. Run ./fetch-corpus.ps1 (or pass a path)." }
$Repo = (Resolve-Path $Repo).Path.TrimEnd('\', '/')

$bin = Join-Path $root '.toolchains\w64devkit\bin'
$cc = if ($Lang -eq 'cpp') { Join-Path $bin 'g++.exe' } else { Join-Path $bin 'gcc.exe' }
if (-not (Test-Path $cc)) { throw "toolchain missing. Run ./fetch-toolchains.ps1" }
if (-not $Std -and $Lang -eq 'cpp') { $Std = '-std=c++17' }
$cli = Resolve-CarverDll -RepoRoot $root

$patterns = if ($Lang -eq 'cpp') { '*.cpp', '*.cc', '*.cxx' } else { '*.c' }
$out = "$Repo-carved"
# Per-run scratch for compiler output and the config (no fixed names shared between concurrent runs).
$work = Join-Path ([IO.Path]::GetTempPath()) ("cc-fuzz-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$err = Join-Path $work 'cc_err.txt'
$outNull = Join-Path $work 'cc_out.txt'
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

try {
    # 1. Baseline: which sources compile UNCARVED? Only these can reveal a carve regression. Keyed by path
    #    RELATIVE to the repo (review SC-D6): two files with the same name in different dirs are distinct.
    $srcIncs = @($Inc | ForEach-Object { Join-Path $Repo $_ }) + $Repo
    $baseline = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -Recurse -Path $Repo -Include $patterns -File |
        Where-Object { $_.FullName -notmatch '[\\/](test|tests|example|examples|docs|fuzz)[\\/]' } |
        ForEach-Object { if (Compile $_.FullName $srcIncs) { $baseline.Add($_.FullName.Substring($Repo.Length).TrimStart('\', '/')) } }
    Write-Host "baseline: $($baseline.Count) file(s) compile uncarved"
    if ($baseline.Count -eq 0) {
        Write-Host "  (0 baseline: this repo needs its own build config; not a usable fuzz target here)"
        exit 2
    }

    # 2. Carve + prune + emit. Everything goes in a TOML config; the carved tree lands at $out/carved.
    #    The output is deleted first so a failed carve can never leave an old tree to be re-checked.
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "carving..."
    $fuzzCfg = Join-Path $work 'fuzz.toml'
    @(
        "outputDirectory = $(ConvertTo-TomlPath $out)"
        '[common]'
        "entryPoints = $(ConvertTo-TomlArray $Roots)"
        "languages = $(ConvertTo-TomlArray $Lang)"
        "excludeDirectories = $(ConvertTo-TomlArray $Exclude)"
        'carveSourceFileContents = true'
    ) -join "`n" | Set-Content -Encoding utf8 $fuzzCfg
    # stderr carries carve warnings: Continue so PowerShell 5.1 does not turn them into a terminating error.
    $ErrorActionPreference = 'Continue'
    & dotnet $cli carve $Repo --config $fuzzCfg 2>&1 |
        Select-String 'emitted|none of|support|error' | ForEach-Object { "  $_" }
    $carveExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $carvedRoot = Join-Path $out 'carved'   # the carved tree is under <outputDirectory>/carved
    if ($carveExit -ne 0 -or -not (Test-Path $carvedRoot)) { Write-Host "  CARVE FAILED (exit $carveExit)" -ForegroundColor Red; exit 3 }

    # 3. Re-compile the carved version of each baseline-passing file (same relative path); a regression is
    #    a carve bug.
    $carvedIncs = @($Inc | ForEach-Object { Join-Path $carvedRoot $_ }) + $carvedRoot
    $bugs = 0
    foreach ($rel in $baseline) {
        $cf = Join-Path $carvedRoot $rel
        if (-not (Test-Path $cf)) { continue }   # the carve dropped the whole file - sound, not a bug
        if (-not (Compile $cf $carvedIncs)) {
            Write-Host "  !!! BUG: $rel" -ForegroundColor Red
            Get-Content $err | Select-Object -First 6 | ForEach-Object { Write-Host "      $_" }
            $bugs++
        }
    }
    if ($bugs -eq 0) { Write-Host "  CLEAN" -ForegroundColor Green; exit 0 }
    Write-Host "  $bugs carve bug(s)" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
