#!/usr/bin/env bash
# Makes TO an exact copy of FROM, copying only the files whose size or modification time differ and deleting the
# ones FROM no longer has: the Linux/macOS counterpart of mirror.ps1 (see there, and USAGE.md "Carving a local copy").
#
#   tools/mirror/mirror.sh FROM TO [--exclude DIR]...
#
# Uses rsync. -a keeps modification times: they are how the next copy and the carve's mirrorOf check tell an
# unchanged file. The first copy into a non-empty TO needs FORCE=1; it then leaves TO.mirror-of.txt beside it.
set -euo pipefail
[ $# -ge 2 ] || { echo "usage: $0 FROM TO [--exclude DIR]..."; exit 1; }
from="${1%/}"; to="${2%/}"; shift 2
[ -d "$from" ] || { echo "FROM '$from' is not a directory (or can't be reached)."; exit 1; }
case "$to/" in "$from"/*) echo "FROM and TO overlap; they must be separate trees."; exit 1;; esac
case "$from/" in "$to"/*) echo "FROM and TO overlap; they must be separate trees."; exit 1;; esac
marker="$to.mirror-of.txt"
if [ -d "$to" ] && [ ! -f "$marker" ] && [ "${FORCE:-0}" != 1 ] && [ -n "$(ls -A "$to")" ]; then
  echo "'$to' is not empty and was not made by this script: its files not in '$from' would be deleted."
  echo "Choose another TO, or set FORCE=1 if that is what you want."
  exit 1
fi
mkdir -p "$to"
echo "Mirror of $from, made by tools/mirror/mirror.sh. Last copied $(date -u +%FT%TZ)." > "$marker"
excludes=()
while [ $# -gt 0 ]; do
  [ "$1" = --exclude ] && [ $# -ge 2 ] || { echo "unexpected argument: $1"; exit 1; }
  excludes+=(--exclude "$2/"); shift 2
done
start=$(date +%s)
rsync -a --delete --modify-window=2 ${excludes[@]+"${excludes[@]}"} "$from/" "$to/"
echo "mirror: $(( $(date +%s) - start )) s"
