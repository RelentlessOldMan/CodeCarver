# CodeCarver preset: embedded ARM firmware image (fill in the CONFIG block, then run).
# Drives docs/WORKREPO.md steps 1-4: smoke test -> compiler-free verify -> carve (file-level AND
# --prune) -> build each carved tree with YOUR toolchain -> report IMAGE SIZE before/after.
# Copy to presets/embedded-arm.ps1 (gitignored) and edit. Nothing here is proprietary; it's a template.

# ============================== CONFIG - EDIT THIS BLOCK ==============================
$Repo    = 'C:\path\to\firmware'                 # <REPO> firmware source root
$Roots   = 'main,Reset_Handler,SysTick_Handler,USART1_IRQHandler,app_entry'  # <ROOTS> see WORKREPO.md 0
$Lang    = 'c'                                   # 'c' or 'cpp'
$Exclude = 'tests,tools,bootloader'              # <EXCLUDE> dirs NOT in this image (other variants!)
$Aux     = '*.ld,*.lds,*.s,*.S,*.icf'            # <AUX> linker/startup files copied verbatim
$BuildLog= ''                                    # optional: path to `make -n` output (-> buildLogs). '' to skip
$Defines = ''                                    # optional: 'CHIP=X,FEATURE_Y' (-> defines). '' to skip

# Your real build + size probe. {OUT} is replaced with the carved tree path.
# Must produce the image from the carved sources and print/emit it so <SizeCmd> can measure it.
$BuildCmd = 'make -C {OUT} IMAGE=firmware.elf'   # <BUILD> pointed at the carved tree
$SizeCmd  = 'arm-none-eabi-size {OUT}\firmware.elf'   # <SIZE> image-size probe (or measure the .bin)
$RunBuild = $false                               # set $true once BuildCmd/SizeCmd are real for your setup
# =====================================================================================

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'tools\CarverScriptLib.ps1')
$cli = Resolve-CarverDll -RepoRoot $repoRoot
# Everything is config now: build a carve.toml from the CONFIG block above, then `carve <repo> --config`.
# (A missing named entry point fails the run; the compiler-free soundness check runs automatically - watch the
# 'verify' line. See WORKREPO.md 0 for curating entryPoints.)
$outDir = Join-Path $Repo '..\carved'
$tl = @()
$tl += 'outputDirectory = ' + (ConvertTo-TomlPath $outDir)
$tl += '[common]'
$tl += 'entryPoints = ' + (ConvertTo-TomlArray $Roots)
$tl += 'languages = ' + (ConvertTo-TomlArray $Lang)
if ($Exclude) { $tl += 'excludeDirectories = ' + (ConvertTo-TomlArray $Exclude) }
if ($Aux)     { $tl += 'forceKeepFiles = ' + (ConvertTo-TomlArray $Aux) }
if ($BuildLog -or $Defines) {
  $tl += '[builds.main]'
  if ($BuildLog) { $tl += 'buildLogs = [' + (ConvertTo-TomlPath $BuildLog) + ']' }
  if ($Defines)  { $tl += 'defines = ' + (ConvertTo-TomlArray $Defines) }
}
# Two aggressiveness tiers: the safe file-level floor, then the intra-file carve.
$tl += @('[stages.file-level]', 'carveSourceFileContents = false',
         '[stages.prune]',      'carveSourceFileContents = true')
$cfgPath = Join-Path $PSScriptRoot 'embedded-arm.toml'
($tl -join "`n") | Set-Content -Encoding utf8 $cfgPath
Write-Host "config -> $cfgPath" -ForegroundColor DarkGray

Write-Host "== Carve (both stages) ==" -ForegroundColor Cyan
# stderr carries progress and warnings: under 'Stop', Windows PowerShell 5.1 turns a merged (2>&1) native
# stderr line into a terminating error even on exit 0 (review SC-D2). Continue for the call; gate on the exit code.
$ErrorActionPreference = 'Continue'
& dotnet $cli carve $Repo --config $cfgPath 2>&1 |
  Select-String 'roots|nodes|files|implicit|asm:|section:|verify|world|stage|emitted|size|warn|error|none of the requested'
$carveExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($carveExit -ne 0) { throw "carve failed (exit $carveExit) - see the output above" }

$outFile  = Join-Path $outDir 'file-level\carved'
$outPrune = Join-Path $outDir 'prune\carved'

if (-not $RunBuild) {
    Write-Host "`n== Build+size skipped (set `$RunBuild=`$true and make BuildCmd/SizeCmd real). ==" -ForegroundColor Yellow
    Write-Host "   carved trees:  $outFile   and   $outPrune"
    return
}

Write-Host "`n== Build each carved tree and compare IMAGE SIZE ==" -ForegroundColor Cyan
$buildFailures = 0
foreach ($t in @(@{n='file-level'; o=$outFile}, @{n='prune'; o=$outPrune})) {
    Write-Host "  --- building $($t.n): $($t.o) ---"
    $global:LASTEXITCODE = 0
    Invoke-Expression ($BuildCmd.Replace('{OUT}', $t.o))
    if ($LASTEXITCODE -ne 0) { Write-Host "  BUILD FAILED for $($t.n) - a dropped symbol/broken structure (see WORKREPO.md)." -ForegroundColor Red; $buildFailures++; continue }
    Write-Host "  size ($($t.n)):" -ForegroundColor Green
    Invoke-Expression ($SizeCmd.Replace('{OUT}', $t.o))
}
Write-Host "`nCompare against the SAME build+size on the ORIGINAL tree for the true delta." -ForegroundColor Cyan
if ($buildFailures -gt 0) { exit 1 }
