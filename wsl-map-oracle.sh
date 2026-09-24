#!/usr/bin/env bash
# Linker-map oracle (run inside WSL / Linux) — the STRONGEST soundness check.
# Build a repo's full source with --gc-sections and force-keep the ROOTS (-Wl,--undefined), so the
# LINKER discards every function unreachable from those roots. Then assert every function the linker
# KEPT is also kept by CodeCarver. If the linker kept one the carve dropped, that's a real soundness
# bug (the carve would fail to link). CodeCarver may keep MORE (its over-approximation) — that's fine.
#
#   wsl-map-oracle.sh <repoDir> <ccKept.txt> <ccAll.txt> <roots-comma> <cfile...>
#     INC="-I. -Iinc"   LIBS="-lm"    (compile/link flags via env)
# ccKept.txt = functions CodeCarver kept; ccAll.txt = every function it saw (to ignore libc/driver syms).
set -u
repo="$1"; ccKept="$2"; ccAll="$3"; roots="$4"; shift 4
cd "$repo" || { echo "no repo $repo"; exit 3; }

undef=""
IFS=',' read -ra R <<< "$roots"
for r in "${R[@]}"; do undef="$undef -Wl,--undefined=$r"; done
echo 'int main(void){return 0;}' > /tmp/mo_main.c   # empty entry; roots kept via --undefined

if ! gcc -O0 -ffunction-sections -fdata-sections ${INC:-} "$@" /tmp/mo_main.c ${LIBS:-} \
        -Wl,--gc-sections $undef -o /tmp/mo_elf 2>/tmp/mo_err; then
    echo "BUILD FAILED (repo doesn't build standalone here):"; head -8 /tmp/mo_err; exit 2
fi

# Functions the linker kept (survived --gc-sections): defined text symbols, T (global) or t (static).
# Exclude `main` — it's our synthetic entry, not a repo function.
nm --defined-only /tmp/mo_elf | awk '$2 == "T" || $2 == "t" { print $3 }' | grep -vx main | sort -u > /tmp/mo_linker.txt
# Restrict to functions CodeCarver actually saw (drops main/libc), then subtract what it kept.
comm -12 /tmp/mo_linker.txt <(sort -u "$ccAll") > /tmp/mo_linker_repo.txt
comm -23 /tmp/mo_linker_repo.txt <(sort -u "$ccKept") > /tmp/mo_viol.txt

kept=$(wc -l < /tmp/mo_linker_repo.txt); cck=$(wc -l < "$ccKept")
echo "linker kept (repo fns): $kept    carve kept: $cck    (carve keeps >= linker = sound)"
if [ -s /tmp/mo_viol.txt ]; then
    echo "!!! SOUNDNESS VIOLATIONS - linker kept these, carve DROPPED them:"
    sed 's/^/    /' /tmp/mo_viol.txt
    exit 1
fi
echo "SOUND: every function the linker kept is also kept by the carve."
