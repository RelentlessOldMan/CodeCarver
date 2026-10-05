# CodeCarver test gate. Run before pushing.
#   ./check.ps1              fast tests only (engine, front-ends, preprocess, scenarios, emit)
#   ./check.ps1 -Big         also the build-verify tests (compile carved output with the pinned gcc)
#   ./check.ps1 -Big -Fetch  fetch the toolchain + corpus first, then run everything
#   ./check.ps1 -Configuration Release   test the configuration that ships (release.ps1 does this)
#
# Build-verify cases skip cleanly when the toolchain (.toolchains) or a corpus repo (.corpus) is
# absent, so -Big is safe even without -Fetch.
param([switch]$Big, [switch]$Fetch, [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# Clear stray test hosts from an interrupted run of THIS repo - they lock build outputs and cause "file in
# use". Only processes whose executable lives under this checkout (e.g. tests\...\bin\...\testhost.exe) are
# touched: other sessions' test runs on the same machine are left alone (review SC-F3). A process whose path
# cannot be read (another user's, or already exiting) is skipped, never killed blind.
$repoPrefix = ([IO.Path]::GetFullPath($root)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
Get-Process testhost -ErrorAction SilentlyContinue | ForEach-Object {
    $exe = $null
    try { $exe = $_.Path } catch { }
    if ($exe -and ([IO.Path]::GetFullPath($exe)).StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "stopping stray testhost $($_.Id) ($exe)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

if ($Fetch) {
    & (Join-Path $root 'fetch-toolchains.ps1')
    if ($LASTEXITCODE -ne 0) { throw "fetch-toolchains failed" }
    & (Join-Path $root 'fetch-corpus.ps1')
    if ($LASTEXITCODE -ne 0) { throw "fetch-corpus failed" }
}

Write-Host "Building ($Configuration)..."
dotnet build CodeCarver.sln -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "`n== Fast tests (no toolchain needed) =="
dotnet test CodeCarver.sln -c $Configuration --no-build --nologo -v q --filter "FullyQualifiedName!~BuildVerify"
if ($LASTEXITCODE -ne 0) { throw "fast tests failed" }

if ($Big) {
    Write-Host "`n== Build-verify tests (carve real repos, compile the output) =="
    Write-Host "   absent toolchain/corpus cases skip cleanly; SQLite is slow (~20s)."
    dotnet test CodeCarver.sln -c $Configuration --no-build --nologo -v q --filter "FullyQualifiedName~BuildVerify"
    if ($LASTEXITCODE -ne 0) { throw "build-verify tests failed" }
} else {
    Write-Host "`n(skipping build-verify; use './check.ps1 -Big' to compile carved output," `
               "or '-Big -Fetch' to pull the toolchain + corpus first)"
}

Write-Host "`nAll green."
