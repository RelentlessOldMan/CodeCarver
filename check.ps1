# CodeCarver test gate. Run before pushing.
#   ./check.ps1              fast tests only (engine, front-ends, preprocess, scenarios, emit)
#   ./check.ps1 -Big         also the build-verify tests (compile carved output with the pinned gcc)
#   ./check.ps1 -Big -Fetch  fetch the toolchain + corpus first, then run everything
#
# Build-verify cases skip cleanly when the toolchain (.toolchains) or a corpus repo (.corpus) is
# absent, so -Big is safe even without -Fetch.
param([switch]$Big, [switch]$Fetch)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# Clear stray test hosts from an interrupted run — they lock build outputs and cause "file in use".
Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if ($Fetch) {
    & (Join-Path $root 'fetch-toolchains.ps1')
    & (Join-Path $root 'fetch-corpus.ps1')
}

Write-Host "Building..."
dotnet build CodeCarver.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "`n== Fast tests (no toolchain needed) =="
dotnet test CodeCarver.sln -c Debug --no-build --nologo -v q --filter "FullyQualifiedName!~BuildVerify"
if ($LASTEXITCODE -ne 0) { throw "fast tests failed" }

if ($Big) {
    Write-Host "`n== Build-verify tests (carve real repos, compile the output) =="
    Write-Host "   absent toolchain/corpus cases skip cleanly; SQLite is slow (~20s)."
    dotnet test CodeCarver.sln -c Debug --no-build --nologo -v q --filter "FullyQualifiedName~BuildVerify"
    if ($LASTEXITCODE -ne 0) { throw "build-verify tests failed" }
} else {
    Write-Host "`n(skipping build-verify; use './check.ps1 -Big' to compile carved output," `
               "or '-Big -Fetch' to pull the toolchain + corpus first)"
}

Write-Host "`nAll green."
