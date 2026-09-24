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
$BuildLog= ''                                    # optional: path to `make -n` output (--build-log). '' to skip
$Defines = ''                                    # optional: 'CHIP=X,FEATURE_Y' (--define). '' to skip

# Your real build + size probe. {OUT} is replaced with the carved tree path.
# Must produce the image from the carved sources and print/emit it so <SizeCmd> can measure it.
$BuildCmd = 'make -C {OUT} IMAGE=firmware.elf'   # <BUILD> pointed at the carved tree
$SizeCmd  = 'arm-none-eabi-size {OUT}\firmware.elf'   # <SIZE> image-size probe (or measure the .bin)
$RunBuild = $false                               # set $true once BuildCmd/SizeCmd are real for your setup
# =====================================================================================

$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot '..\src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release  (missing $cli)" }
# --strict-roots: a typo'd ISR/API name among a curated root list fails the run instead of silently
# carving the real symbol away (see WORKREPO.md 0). Drop it only if you accept unresolved roots.
$common = @('--roots', $Roots, '--lang', $Lang, '--exclude', $Exclude, '--aux', $Aux, '--strict-roots')
if ($BuildLog) { $common += @('--build-log', $BuildLog) }
if ($Defines)  { $common += @('--define', $Defines) }

function Carve([string[]]$extra) { & dotnet $cli carve $Repo @common @extra 2>&1 }

Write-Host "== 1. Smoke test (check implicit/asm/section counts match your ISRs/ctors) ==" -ForegroundColor Cyan
Carve @() | Select-String 'roots|nodes|files|implicit|asm:|section:|warn|none of the requested'

Write-Host "`n== 2. Compiler-free soundness gate (--prune --verify) ==" -ForegroundColor Cyan
$v = Carve @('--prune','--verify')
$v | Select-String 'verify|violation|->'
if ($LASTEXITCODE -ne 0) { Write-Host "  VERIFY FAILED - fix before trusting a --prune image (run --why on the callee)." -ForegroundColor Red }

Write-Host "`n== 3. Carve file-level (safe floor) and --prune (aggressive) ==" -ForegroundColor Cyan
$outFile  = Join-Path $Repo '..\carved-file'
$outPrune = Join-Path $Repo '..\carved-prune'
Write-Host "  file-level -> $outFile";  Carve @('--out', $outFile,  '--manifest', "$outFile.json")  | Select-String 'files|size'
Write-Host "  --prune    -> $outPrune"; Carve @('--prune','--out', $outPrune, '--manifest', "$outPrune.json") | Select-String 'files|size'

if (-not $RunBuild) {
    Write-Host "`n== 4. Build+size skipped (set `$RunBuild=`$true and make BuildCmd/SizeCmd real). ==" -ForegroundColor Yellow
    Write-Host "   Then this builds each carved tree with your toolchain and prints the image-size delta."
    return
}

Write-Host "`n== 4. Build each carved tree and compare IMAGE SIZE ==" -ForegroundColor Cyan
foreach ($t in @(@{n='file-level'; o=$outFile}, @{n='--prune'; o=$outPrune})) {
    Write-Host "  --- building $($t.n): $($t.o) ---"
    $b = $BuildCmd.Replace('{OUT}', $t.o)
    Invoke-Expression $b
    if ($LASTEXITCODE -ne 0) { Write-Host "  BUILD FAILED for $($t.n) - a dropped symbol/broken structure (see WORKREPO.md 7)." -ForegroundColor Red; continue }
    Write-Host "  size ($($t.n)):" -ForegroundColor Green
    Invoke-Expression ($SizeCmd.Replace('{OUT}', $t.o))
}
Write-Host "`nCompare against the SAME build+size on the ORIGINAL tree for the true delta." -ForegroundColor Cyan
