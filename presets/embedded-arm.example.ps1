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
$cli = Join-Path $PSScriptRoot '..\src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release  (missing $cli)" }
# Everything is config now: build a carve.toml from the CONFIG block above, then `carve <repo> --config`.
# (A missing named entry point fails the run; the compiler-free soundness check runs automatically — watch the
# 'verify' line. See WORKREPO.md 0 for curating entryPoints.)
$outDir = Join-Path $Repo '..\carved'
$tl = @()
$tl += 'outputDirectory = "' + ($outDir -replace '\\','/') + '"'
$tl += '[common]'
$tl += 'entryPoints = ["' + (($Roots -split ',') -join '","') + '"]'
$tl += 'languages = ["' + $Lang + '"]'
if ($Exclude) { $tl += 'excludeDirectories = ["' + (($Exclude -split ',') -join '","') + '"]' }
if ($Aux)     { $tl += 'forceKeepFiles = ["' + (($Aux -split ',') -join '","') + '"]' }
if ($BuildLog -or $Defines) {
  $tl += '[builds.main]'
  if ($BuildLog) { $tl += 'buildLogs = ["' + ($BuildLog -replace '\\','/') + '"]' }
  if ($Defines)  { $tl += 'defines = ["' + (($Defines -split ',') -join '","') + '"]' }
}
# Two aggressiveness tiers: the safe file-level floor, then the intra-file carve.
$tl += @('[stages.file-level]', 'carveSourceFileContents = false',
         '[stages.prune]',      'carveSourceFileContents = true')
$cfgPath = Join-Path $PSScriptRoot 'embedded-arm.toml'
($tl -join "`n") | Set-Content -Encoding utf8 $cfgPath
Write-Host "config -> $cfgPath" -ForegroundColor DarkGray

Write-Host "== Carve (both stages) ==" -ForegroundColor Cyan
& dotnet $cli carve $Repo --config $cfgPath 2>&1 |
  Select-String 'roots|nodes|files|implicit|asm:|section:|verify|world|stage|emitted|size|warn|none of the requested'

$outFile  = Join-Path $outDir 'file-level\carved'
$outPrune = Join-Path $outDir 'prune\carved'

if (-not $RunBuild) {
    Write-Host "`n== Build+size skipped (set `$RunBuild=`$true and make BuildCmd/SizeCmd real). ==" -ForegroundColor Yellow
    Write-Host "   carved trees:  $outFile   and   $outPrune"
    return
}

Write-Host "`n== Build each carved tree and compare IMAGE SIZE ==" -ForegroundColor Cyan
foreach ($t in @(@{n='file-level'; o=$outFile}, @{n='prune'; o=$outPrune})) {
    Write-Host "  --- building $($t.n): $($t.o) ---"
    Invoke-Expression ($BuildCmd.Replace('{OUT}', $t.o))
    if ($LASTEXITCODE -ne 0) { Write-Host "  BUILD FAILED for $($t.n) - a dropped symbol/broken structure (see WORKREPO.md)." -ForegroundColor Red; continue }
    Write-Host "  size ($($t.n)):" -ForegroundColor Green
    Invoke-Expression ($SizeCmd.Replace('{OUT}', $t.o))
}
Write-Host "`nCompare against the SAME build+size on the ORIGINAL tree for the true delta." -ForegroundColor Cyan
