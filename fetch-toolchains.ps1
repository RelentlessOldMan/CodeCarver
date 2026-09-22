# Fetches pinned build toolchains into .toolchains/ (gitignored), used ONLY by the build-verification
# regression tests — the product itself needs no compiler. Pinned versions => reproducible on any box
# and in CI. Re-running is a no-op once a toolchain is present.
$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $root '.toolchains'
New-Item -ItemType Directory -Force -Path $tools | Out-Null

# --- w64devkit: a portable, self-contained GCC for Windows (host C/C++ build verification) ---
$ver  = '2.10.0'
$url   = "https://github.com/skeeto/w64devkit/releases/download/v$ver/w64devkit-x64-$ver.7z.exe"
$sfx   = Join-Path $tools 'w64devkit.7z.exe'
$dest  = Join-Path $tools 'w64devkit'
$gcc   = Join-Path $dest 'bin\gcc.exe'

if (Test-Path $gcc) {
    Write-Host "w64devkit already present ($((& $gcc --version | Select-Object -First 1)))."
} else {
    Write-Host "Downloading w64devkit $ver (~67 MB)..."
    Invoke-WebRequest -Uri $url -OutFile $sfx
    $sevenZip = 'C:\Program Files\7-Zip\7z.exe'
    if (Test-Path $sevenZip) {
        & $sevenZip x $sfx "-o$tools" -y | Out-Null           # extract the appended 7z payload (no GUI)
    } else {
        & $sfx -y "-o$tools" | Out-Null                        # 7-Zip SFX self-extract fallback
    }
    Remove-Item $sfx -Force
    Write-Host "Installed: $((& $gcc --version | Select-Object -First 1))"
}

# --- arm-none-eabi-gcc: portable ARM cross-toolchain for embedded ELF / vector-table verification ---
# The real target is a Cortex-class image loaded via TRACE32, so build-verify the carve on ACTUAL ARM
# firmware (vector table, weak-alias handlers, linker script) — not just host x64. xPack ships a clean
# portable zip (no installer). ~278 MB download.
$armVer = '13.3.1-1.1'
$armUrl  = "https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/releases/download/v$armVer/xpack-arm-none-eabi-gcc-$armVer-win32-x64.zip"
$armZip  = Join-Path $tools 'arm-none-eabi-gcc.zip'
$armDir  = Join-Path $tools "xpack-arm-none-eabi-gcc-$armVer"
$armGcc  = Join-Path $armDir 'bin\arm-none-eabi-gcc.exe'

if (Test-Path $armGcc) {
    Write-Host "arm-none-eabi-gcc already present ($((& $armGcc --version | Select-Object -First 1)))."
} else {
    Write-Host "Downloading arm-none-eabi-gcc $armVer (~278 MB)..."
    Invoke-WebRequest -Uri $armUrl -OutFile $armZip
    Expand-Archive -Path $armZip -DestinationPath $tools -Force
    Remove-Item $armZip -Force
    Write-Host "Installed: $((& $armGcc --version | Select-Object -First 1))"
}

# Future pins go here (LLVM/Clang for the -E preprocessing engine and the semantic tightener) — same
# download-once, verify pattern.

Write-Host "Toolchains ready in $tools"
