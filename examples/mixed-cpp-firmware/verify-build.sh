#!/usr/bin/env bash
# Build-verify every carved stage with gcc/g++ (via the Makefile). Not committed.
set -u
base="$(cd "$(dirname "$0")" && pwd)/out"
rc=0
for s in safe aggressive max; do
  d="$base/$s/carved"
  echo "=== $s ==="
  if [ ! -d "$d" ]; then echo "  (no carved tree — run the carve first)"; rc=1; continue; fi
  ( cd "$d" && make clean >/dev/null 2>&1; if make; then
      sz=$(stat -c %s image.elf)
      echo "  LINK OK: image.elf = ${sz} bytes"
    else
      echo "  BUILD FAILED"; exit 1
    fi ) || rc=1
done
exit $rc
