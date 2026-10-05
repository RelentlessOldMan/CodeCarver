#!/usr/bin/env bash
# Object-symbol oracle (WSL/Linux) - single-file soundness check that needs NO full link.
# Compile ONE hot source both uncarved (baseline) and carved to .o, then compare symbols: any symbol
# DEFINED in the baseline .o but merely REFERENCED-UNDEFINED in the carved .o was pruned while a
# reference to it survives -> the carve would fail to link. This catches prototype-covered static
# drops that a plain `-c` compile misses (the call still parses against the top-of-file prototype),
# and needs none of the project's other TUs (great for amalgamation-ish files: quickjs.c, sqlite3.c).
#
#   wsl-obj-oracle.sh <baseline.o> <carved.o>
# Exit: 0 SOUND, 1 dropped-but-referenced symbols, 3 bad input.
set -u
[ $# -eq 2 ] || { echo "usage: $0 <baseline.o> <carved.o>"; exit 3; }
b="$1"; cv="$2"
for f in "$b" "$cv"; do [ -f "$f" ] || { echo "no object: $f"; exit 3; }; done

work="$(mktemp -d "${TMPDIR:-/tmp}/obj_oracle.XXXXXX")" || { echo "mktemp failed"; exit 3; }
trap 'rm -rf "$work"' EXIT

nm --defined-only "$b" | awk '$2=="T" || $2=="t" { print $3 }' | LC_ALL=C sort -u > "$work/def_base.txt" || exit 3
nm "$cv" | awk '$1=="U" { print $2 }' | LC_ALL=C sort -u > "$work/undef_carved.txt"
nm "$b"  | awk '$1=="U" { print $2 }' | LC_ALL=C sort -u > "$work/undef_base.txt"
[ -s "$work/def_base.txt" ] || { echo "BAD INPUT: baseline object defines no text symbols - nothing to compare"; exit 3; }

# dropped = referenced-undefined in carved, defined in baseline, and NOT already external in baseline
LC_ALL=C comm -12 "$work/undef_carved.txt" "$work/def_base.txt" > "$work/maybe.txt"
LC_ALL=C comm -23 "$work/maybe.txt" "$work/undef_base.txt" > "$work/drops.txt"

echo "baseline defined text syms: $(wc -l < "$work/def_base.txt")    carved undefined refs: $(wc -l < "$work/undef_carved.txt")"
n=$(wc -l < "$work/drops.txt")
if [ "$n" -eq 0 ]; then
    echo "SOUND: no same-file symbol was dropped while still referenced."
    exit 0
fi
echo "!!! $n symbol(s) DROPPED-BUT-REFERENCED (defined in baseline, undefined in carved) - link would fail:"
sed 's/^/    /' "$work/drops.txt" | head -40
exit 1
