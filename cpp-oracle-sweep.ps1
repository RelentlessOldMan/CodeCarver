# C++ link/compile oracle sweep - carve each C++ corpus repo to --out, then (in WSL, needs g++)
# link that CARVED tree against a driver main() that calls the roots. An `undefined reference` /
# compile error against the carved tree is a real dropped-symbol soundness bug. This is the strongest
# C++ check we have; it caught the template-argument-call drop (simdjson simd8::prev<N>).
#
#   ./cpp-oracle-sweep.ps1                # sweep all repos present under .corpus
# Prereqs: dotnet build -c Release; WSL Ubuntu with g++ (sudo apt install -y g++); .corpus fetched.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cli  = Join-Path $root 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
$c    = Join-Path $root '.corpus'
$o    = Join-Path $root '.oracle-cpp'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release  (missing $cli)" }

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

# repo | carve input (rel to .corpus) | roots | driver | INC (rel to carved out) | EXCLUDE regex | extra carve flags
$cases = @(
  @{ n='tinyxml2'; in='tinyxml2';            roots='LoadFile,SaveFile,Parse,Print,Accept';       drv='tinyxml2_driver.cpp'; inc='';         excl='';           flags=@() },
  @{ n='pugixml';  in='pugixml\src';         roots='load_file,load_string,load_buffer,save';     drv='pugixml_driver.cpp';  inc='';         excl='';           flags=@() },
  @{ n='simdjson'; in='simdjson\singleheader';roots='parse,iterate,load,load_many';              drv='simdjson_driver.cpp'; inc='';         excl='';           flags=@() },
  @{ n='fmt';      in='fmt';                 roots='vformat,vformat_to,vprint,report_error';     drv='fmt_driver.cpp';      inc='/include'; excl='fmt\.cc|fmt-c'; flags=@('--exclude','test,doc,support') },
  @{ n='json';     in='json\single_include'; roots='parse,dump';                                 drv='json_driver.cpp';     inc='';         excl='';           flags=@('--prune-headers') }
)

# The native calls below merge stderr (2>&1) for display. Under $ErrorActionPreference='Stop' a native
# command's stderr line (e.g. a carve `warn:` about an unresolved root) is wrapped in a terminating
# NativeCommandError even on exit 0, aborting the sweep. Drop to 'Continue' for the loop; failures are
# handled explicitly via the SOUND/FAILED check and the $fail counter.
$ErrorActionPreference = 'Continue'
$fail = 0
foreach ($t in $cases) {
    $inPath = Join-Path $c $t.in
    if (-not (Test-Path $inPath)) { Write-Host ("SKIP {0} (not under .corpus)" -f $t.n) -ForegroundColor DarkGray; continue }
    $outPath = Join-Path $o $t.n
    Write-Host ("=== {0} ===" -f $t.n) -ForegroundColor Cyan
    & dotnet $cli carve $inPath --lang cpp --roots $t.roots @($t.flags) --prune --out $outPath 2>&1 |
        Select-String 'nodes|files|UNRESOLVED' | ForEach-Object { Write-Host "  $_" }
    $env:INC = "-I$ow/$($t.n)$($t.inc)"
    if ($t.excl) { $env:EXCLUDE = $t.excl } else { Remove-Item Env:\EXCLUDE -ErrorAction SilentlyContinue }
    $res = wsl -d Ubuntu -- bash -c "INC='$env:INC' EXCLUDE='$($t.excl)' bash $sh $ow/$($t.n) $drv/$($t.drv)"
    $line = ($res | Select-String 'SOUND|FAILED').Line
    if ($line -match 'SOUND') { Write-Host "  $line" -ForegroundColor Green }
    else { Write-Host "  $line" -ForegroundColor Red; $res | Select-Object -Last 12 | ForEach-Object { "    $_" }; $fail++ }
}
Remove-Item Env:\INC,Env:\EXCLUDE -ErrorAction SilentlyContinue
Write-Host ""
if ($fail -eq 0) { Write-Host "ALL C++ CARVES SOUND under the g++ oracle." -ForegroundColor Green }
else { Write-Host ("$fail repo(s) FAILED - dropped-symbol soundness bug(s).") -ForegroundColor Red; exit 1 }
