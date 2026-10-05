# Carve this example at all three stages into out/<stage>/. Run from anywhere.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$cli = Join-Path $PSScriptRoot '..\..\src\CodeCarver.Cli\bin\Debug\net8.0\codecarver.dll'
if (-not (Test-Path $cli)) { $cli = Join-Path $PSScriptRoot '..\..\src\CodeCarver.Cli\bin\Release\net8.0\codecarver.dll' }
if (-not (Test-Path $cli)) { throw "build the CLI first:  dotnet build -c Debug  (from the repo root)" }
& dotnet $cli carve src --config carve.toml
