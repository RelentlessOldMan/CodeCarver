#!/usr/bin/env bash
# Object-symbol oracle (WSL/Linux) — single-file soundness check that needs NO full link.
# Compile ONE hot source both uncarved (baseline) and carved to .o, then compare symbols: any symbol
# DEFINED in the baseline .o but merely REFERENCED-UNDEFINED in the carved .o was pruned while a
# reference to it survives -> the carve would fail to link. This catches prototype-covered static
# drops that a plain `-c` compile misses (the call still parses against the top-of-file prototype),
# and needs none of the project's other TUs (great for amalgamation-ish files: quickjs.c, sqlite3.c).
#
#   wsl-obj-oracle.sh <baseline.o> <carved.o>
set -u
b="$1"; cv="$2"
for f in "$b" "$cv"; do [ -f "$f" ] || { echo "no object: $f"; exit 3; }; done

nm --defined-only "$b" | awk '$2=="T" || $2=="t" { print $3 }' | sort -u > /tmp/obj_def_base.txt
nm "$cv" | awk '$1=="U" { print $2 }' | sort -u > /tmp/obj_undef_carved.txt
nm "$b"  | awk '$1=="U" { print $2 }' | sort -u > /tmp/obj_undef_base.txt

# dropped = referenced-undefined in carved, defined in baseline, and NOT already external in baseline
comm -12 /tmp/obj_undef_carved.txt /tmp/obj_def_base.txt > /tmp/obj_maybe.txt
comm -23 /tmp/obj_maybe.txt /tmp/obj_undef_base.txt > /tmp/obj_drops.txt

echo "baseline defined text syms: $(wc -l < /tmp/obj_def_base.txt)    carved undefined refs: $(wc -l < /tmp/obj_undef_carved.txt)"
n=$(wc -l < /tmp/obj_drops.txt)
if [ "$n" -eq 0 ]; then
    echo "SOUND: no same-file symbol was dropped while still referenced."
    exit 0
fi
echo "!!! $n symbol(s) DROPPED-BUT-REFERENCED (defined in baseline, undefined in carved) — link would fail:"
sed 's/^/    /' /tmp/obj_drops.txt | head -40
exit 1
