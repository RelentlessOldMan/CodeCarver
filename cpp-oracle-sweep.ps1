# C++ link/compile oracle sweep - carve each C++ corpus repo to --out, then (in WSL, needs g++)
# link that CARVED tree against a driver main() that calls the roots. An `undefined reference` /
# compile error against the carved tree is a real dropped-symbol soundness bug. This is the strongest
# C++ check we have; it caught the template-argument-call drop (simdjson simd8::prev<N>).
#
#   ./cpp-oracle-sweep.ps1                # sweep all repos present under .corpus
# Prereqs: dotnet build -c Release; WSL Ubuntu with g++ (sudo apt install -y g++); .corpus fetched.
#
# Exit: 0 = every present repo SOUND; 1 = a repo FAILED (or its carve failed); 2 = nothing was checked.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'tools\CarverScriptLib.ps1')
$cli  = Resolve-CarverDll -RepoRoot $root
$c    = Join-Path $root '.corpus'
$o    = Join-Path $root '.oracle-cpp'

# Windows path -> WSL /mnt path, derived from $root so the sweep works from ANY clone location
# (was hardcoded to /mnt/c/Playground/CodeCarver, which broke on any other checkout).
function ToWsl([string]$p) {
    $full = [IO.Path]::GetFullPath($p)
    if ($full -notmatch '^([A-Za-z]):[\\/](.*)$') { throw "not a Windows drive path: $p" }
    return "/mnt/$($Matches[1].ToLower())/$($Matches[2] -replace '\\','/')"
}
$sh  = ToWsl (Join-Path $root 'wsl-cpp-oracle.sh')
$drv = ToWsl (Join-Path $root 'oracle')
$ow  = ToWsl $o

# repo | carve input (rel to .corpus) | roots | driver | INC (rel to carved out) | EXCLUDE regex | per-case config
# (inputs/tuning are config-only now: 'cfg' is written to a temp --config per case)
$cases = @(
  @{ n='tinyxml2'; in='tinyxml2';            roots='LoadFile,SaveFile,Parse,Print,Accept';       drv='tinyxml2_driver.cpp'; inc='';         excl='';           cfg=@{} },
  @{ n='pugixml';  in='pugixml\src';         roots='load_file,load_string,load_buffer,save';     drv='pugixml_driver.cpp';  inc='';         excl='';           cfg=@{} },
  @{ n='simdjson'; in='simdjson\singleheader';roots='parse,iterate,load,load_many';              drv='simdjson_driver.cpp'; inc='';         excl='';           cfg=@{} },
  @{ n='fmt';      in='fmt';                 roots='vformat,vformat_to,vprint,report_error';     drv='fmt_driver.cpp';      inc='/include'; excl='fmt\.cc|fmt-c'; cfg=@{ exclude=@('test','doc','support') } },
  @{ n='json';     in='json\single_include'; roots='parse,dump';                                 drv='json_driver.cpp';     inc='';         excl='';           cfg=@{ pruneHeaders=$true } }
)

# The native calls below merge stderr (2>&1) for display. Under $ErrorActionPreference='Stop' a native
# command's stderr line (e.g. a carve `warn:` about an unresolved root) is wrapped in a terminating
# NativeCommandError even on exit 0, aborting the sweep. Drop to 'Continue' for the loop; failures are
# handled explicitly via the SOUND/FAILED check and the $fail counter.
$ErrorActionPreference = 'Continue'
$fail = 0; $checked = 0
foreach ($t in $cases) {
    $inPath = Join-Path $c $t.in
    if (-not (Test-Path $inPath)) { Write-Host ("SKIP {0} (not under .corpus)" -f $t.n) -ForegroundColor DarkGray; continue }
    $outPath = Join-Path $o $t.n
    Write-Host ("=== {0} ===" -f $t.n) -ForegroundColor Cyan
    # Script-owned scratch (.oracle-cpp is gitignored): delete first so a crashed carve can never leave the
    # previous run's tree to be linked and reported SOUND (review SC-D4).
    if (Test-Path $outPath) { Remove-Item -Recurse -Force $outPath }
    $tl = @()
    $tl += 'outputDirectory = ' + (ConvertTo-TomlPath $outPath)
    $tl += '[common]'
    $tl += 'entryPoints = ' + (ConvertTo-TomlArray $t.roots)
    $tl += 'languages = ["cpp"]'
    $tl += 'carveSourceFileContents = true'
    if ($t.cfg.ContainsKey('exclude'))      { $tl += 'excludeDirectories = ' + (ConvertTo-TomlArray $t.cfg.exclude) }
    if ($t.cfg.ContainsKey('pruneHeaders')) { $tl += 'carveHeaderFileContents = true' }
    $cfgPath = "$outPath.toml"
    ($tl -join "`n") | Set-Content -Encoding utf8 $cfgPath
    & dotnet $cli carve $inPath --config $cfgPath 2>&1 |
        Select-String 'nodes|files|UNRESOLVED|error|fail' | ForEach-Object { Write-Host "  $_" }
    $carveExit = $LASTEXITCODE
    Remove-Item $cfgPath -Force -ErrorAction SilentlyContinue
    # Carved tree is at <outPath>/carved now.
    if ($carveExit -ne 0 -or -not (Test-Path (Join-Path $outPath 'carved'))) {
        Write-Host "  !!! CARVE FAILED (exit $carveExit)" -ForegroundColor Red; $fail++; continue
    }
    # INC / EXCLUDE go to the oracle as environment for that one bash invocation (nothing is left in this
    # process's environment). The values come from the table above; refuse a quote that would break out.
    $inc = "-I$ow/$($t.n)/carved$($t.inc)"
    foreach ($v in @($inc, $t.excl)) { if ("$v" -match "'") { throw "quote in oracle argument: $v" } }
    $res = wsl -d Ubuntu -- bash -c "INC='$inc' EXCLUDE='$($t.excl)' bash '$sh' '$ow/$($t.n)/carved' '$drv/$($t.drv)'"
    $oracleExit = $LASTEXITCODE
    $checked++
    $line = ($res | Select-String '^(SOUND|!!!)' | Select-Object -First 1).Line
    if ($oracleExit -eq 0 -and $line -match '^SOUND') { Write-Host "  $line" -ForegroundColor Green }
    else { Write-Host "  oracle exit $oracleExit : $line" -ForegroundColor Red; $res | Select-Object -Last 12 | ForEach-Object { "    $_" }; $fail++ }
}
Write-Host ""
if ($fail -gt 0) { Write-Host ("$fail repo(s) FAILED - dropped-symbol soundness bug(s) or a failed carve.") -ForegroundColor Red; exit 1 }
if ($checked -eq 0) { Write-Host "NOTHING WAS CHECKED (no corpus repo present) - this is not a pass." -ForegroundColor Red; exit 2 }
Write-Host "ALL $checked C++ CARVES SOUND under the g++ oracle." -ForegroundColor Green
