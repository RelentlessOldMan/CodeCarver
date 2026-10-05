# Fetches pinned build toolchains into .toolchains/ (gitignored), used ONLY by the build-verification
# regression tests - the product itself needs no compiler. Pinned versions => reproducible on any box
# and in CI. Re-running is a no-op once a toolchain is present.
$ErrorActionPreference = 'Stop'
# GitHub release downloads require TLS 1.2+; Windows PowerShell 5.1 on older boxes still defaults to
# 1.0/1.1 and the download fails with an opaque handshake error. Force 1.2/1.3. Also silence the
# Invoke-WebRequest progress bar - rendering it makes large downloads many times slower in PS 5.1.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13 }
catch { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 }  # Tls13 absent on older .NET
$ProgressPreference = 'SilentlyContinue'

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $root '.toolchains'
New-Item -ItemType Directory -Force -Path $tools | Out-Null

# Download to a temp name, verify its SHA-256 against the pinned value, then move into place. A truncated,
# tampered or substituted download is deleted and the script FAILS - nothing unverified is ever extracted or
# executed (review SC-F4). The SFX fallback below RUNS the downloaded .exe, so this check is load-bearing.
function Download($uri, $outFile, [string]$sha256) {
    if ($sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "no valid pinned SHA-256 for $uri - refusing to download" }
    $tmp = "$outFile.partial"
    try {
        Invoke-WebRequest -Uri $uri -OutFile $tmp
        $got = (Get-FileHash -Algorithm SHA256 -LiteralPath $tmp).Hash
        if ($got -ne $sha256.ToUpperInvariant()) {
            throw "SHA-256 MISMATCH for $uri`n  expected $sha256`n  got      $got"
        }
        Move-Item -Force $tmp $outFile
    }
    catch { Remove-Item -Force $tmp -ErrorAction SilentlyContinue; throw }
}

# Pinned SHA-256 values. Source of each pin (re-check when bumping a version):
#   w64devkit : the sha256 "digest" GitHub records for the release asset
#               (gh api repos/skeeto/w64devkit/releases/tags/v<ver> -> assets[].digest)
#   xPack ARM : the publisher's <asset>.sha file next to the release asset
# Both were read from the publisher's release metadata on 2026-10-04, not computed from a local download.

# --- w64devkit: a portable, self-contained GCC for Windows (host C/C++ build verification) ---
$ver  = '2.10.0'
$sha  = '18d0a4c71a166f8401ab6305781bec5882b40b5e06ba9807c61cb5f3b3c6325e'   # w64devkit-x64-2.10.0.7z.exe
$url   = "https://github.com/skeeto/w64devkit/releases/download/v$ver/w64devkit-x64-$ver.7z.exe"
$sfx   = Join-Path $tools 'w64devkit.7z.exe'
$dest  = Join-Path $tools 'w64devkit'
$gcc   = Join-Path $dest 'bin\gcc.exe'

if (Test-Path $gcc) {
    Write-Host "w64devkit already present ($((& $gcc --version | Select-Object -First 1)))."
} else {
    Write-Host "Downloading w64devkit $ver (~67 MB)..."
    Download $url $sfx $sha
    $sevenZip = 'C:\Program Files\7-Zip\7z.exe'
    if (Test-Path $sevenZip) {
        & $sevenZip x $sfx "-o$tools" -y | Out-Null           # extract the appended 7z payload (no GUI)
    } else {
        & $sfx -y "-o$tools" | Out-Null                        # 7-Zip SFX self-extract fallback (hash-verified above)
    }
    if ($LASTEXITCODE -ne 0) { throw "w64devkit extraction failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $gcc)) { throw "w64devkit extraction produced no $gcc" }
    Remove-Item $sfx -Force
    Write-Host "Installed: $((& $gcc --version | Select-Object -First 1))"
}

# --- arm-none-eabi-gcc: portable ARM cross-toolchain for embedded ELF / vector-table verification ---
# The real target is a Cortex-class image loaded via TRACE32, so build-verify the carve on ACTUAL ARM
# firmware (vector table, weak-alias handlers, linker script) - not just host x64. xPack ships a clean
# portable zip (no installer). ~278 MB download.
$armVer = '13.3.1-1.1'
$armSha  = '49cda1bf01215f3df0613972d21f9fec3eb79dca82506365e7da2ff74b921733'   # ...-win32-x64.zip
$armUrl  = "https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/releases/download/v$armVer/xpack-arm-none-eabi-gcc-$armVer-win32-x64.zip"
$armZip  = Join-Path $tools 'arm-none-eabi-gcc.zip'
$armDir  = Join-Path $tools "xpack-arm-none-eabi-gcc-$armVer"
$armGcc  = Join-Path $armDir 'bin\arm-none-eabi-gcc.exe'

if (Test-Path $armGcc) {
    Write-Host "arm-none-eabi-gcc already present ($((& $armGcc --version | Select-Object -First 1)))."
} else {
    Write-Host "Downloading arm-none-eabi-gcc $armVer (~278 MB)..."
    Download $armUrl $armZip $armSha
    Expand-Archive -Path $armZip -DestinationPath $tools -Force
    Remove-Item $armZip -Force
    Write-Host "Installed: $((& $armGcc --version | Select-Object -First 1))"
}

# Future pins go here (LLVM/Clang for the -E preprocessing engine and the semantic tightener) - same
# download-once, verify pattern.

Write-Host "Toolchains ready in $tools"
exit 0
