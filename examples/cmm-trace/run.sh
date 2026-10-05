#!/usr/bin/env bash
# Carve this example into out/. Run from anywhere.
set -euo pipefail
cd "$(dirname "$0")"
cli="../../src/CodeCarver.Cli/bin/Debug/net8.0/codecarver.dll"
[ -f "$cli" ] || cli="../../src/CodeCarver.Cli/bin/Release/net8.0/codecarver.dll"
[ -f "$cli" ] || { echo "build the CLI first: dotnet build -c Debug (from the repo root)"; exit 1; }
dotnet "$cli" carve src --config carve.toml
