#!/usr/bin/env bash
# Linker-map oracle (run inside WSL / Linux) - the STRONGEST soundness check.
# Build a repo's full source with --gc-sections and force-keep the ROOTS (-Wl,--undefined), so the
# LINKER discards every function unreachable from those roots. Then assert every function the linker
# KEPT is also kept by CodeCarver. If the linker kept one the carve dropped, that's a real soundness
# bug (the carve would fail to link). CodeCarver may keep MORE (its over-approximation) - that's fine.
#
#   wsl-map-oracle.sh <repoDir> <ccKept.txt> <ccAll.txt> <roots-comma> <cfile...>
#     INC="-I. -Iinc"   LIBS="-lm"    (compile/link flags via env)
# ccKept.txt = functions CodeCarver kept; ccAll.txt = every function it saw (to ignore libc/driver syms).
# Both lists: one name per line, UTF-8 (a BOM and CRLF line endings are accepted and normalised).
#
# Exit: 0 SOUND, 1 violations, 2 repo does not build standalone, 3 bad input.
set -u
[ $# -ge 5 ] || { echo "usage: $0 <repoDir> <ccKept.txt> <ccAll.txt> <roots-comma> <cfile...>"; exit 3; }
repo="$1"; ccKept="$2"; ccAll="$3"; roots="$4"; shift 4
cd "$repo" || { echo "no repo $repo"; exit 3; }

work="$(mktemp -d "${TMPDIR:-/tmp}/map_oracle.XXXXXX")" || { echo "mktemp failed"; exit 3; }
trap 'rm -rf "$work"' EXIT

# Normalise a name list: reject UTF-16 (NUL bytes) and empty input, strip a UTF-8 BOM and CRs, drop blank
# lines. A CRLF or UTF-16 list used to share no line with nm's output, so "no violations" read as SOUND.
normalise() {  # <in> <out> <label>
    [ -s "$1" ] || { echo "BAD INPUT: $3 '$1' is missing or empty"; exit 3; }
    if tr -d '\000' < "$1" | cmp -s - "$1"; then :; else
        echo "BAD INPUT: $3 '$1' contains NUL bytes (UTF-16?) - write it as UTF-8"; exit 3
    fi
    sed -e '1s/^\xEF\xBB\xBF//' -e 's/\r$//' "$1" | grep -v '^[[:space:]]*$' | LC_ALL=C sort -u > "$2"
    [ -s "$2" ] || { echo "BAD INPUT: $3 '$1' has no names"; exit 3; }
}
normalise "$ccKept" "$work/cc_kept.txt" ccKept
normalise "$ccAll"  "$work/cc_all.txt"  ccAll

undef=()
IFS=',' read -ra R <<< "$roots"
for r in "${R[@]}"; do [ -n "$r" ] && undef+=("-Wl,--undefined=$r"); done
[ ${#undef[@]} -gt 0 ] || { echo "BAD INPUT: no roots"; exit 3; }
echo 'int main(void){return 0;}' > "$work/mo_main.c"   # empty entry; roots kept via --undefined

# -fvisibility=hidden is essential: without it, every global function goes in the dynamic symbol table
# and --gc-sections keeps it (a potential dynamic entry), so the "linker kept" set would include exported
# functions NOT reachable from the roots - false violations. Hidden makes kept == reachable-from-roots,
# matching the carve. -Wl,--undefined re-exposes exactly the roots so they (and their callees) survive.
# $CC, $INC and $LIBS are flag lists: word splitting is intended.
# shellcheck disable=SC2086
if ! ${CC:-gcc} -O0 -fvisibility=hidden -ffunction-sections -fdata-sections ${INC:-} "$@" "$work/mo_main.c" ${LIBS:-} \
        -Wl,--gc-sections "${undef[@]}" -o "$work/mo_elf" 2>"$work/mo_err"; then   # CC=g++ for C++ repos
    echo "BUILD FAILED (repo doesn't build standalone here):"; head -8 "$work/mo_err"; exit 2
fi

# Functions the linker kept (survived --gc-sections): defined text symbols, T (global) or t (static).
# Exclude `main` - it's our synthetic entry, not a repo function.
nm --defined-only "$work/mo_elf" | awk '$2 == "T" || $2 == "t" { print $3 }' | grep -vx main | LC_ALL=C sort -u > "$work/linker.txt"
# Restrict to functions CodeCarver actually saw (drops main/libc), then subtract what it kept.
LC_ALL=C comm -12 "$work/linker.txt" "$work/cc_all.txt" > "$work/linker_repo.txt"
LC_ALL=C comm -23 "$work/linker_repo.txt" "$work/cc_kept.txt" > "$work/viol.txt"

kept=$(wc -l < "$work/linker_repo.txt"); cck=$(wc -l < "$work/cc_kept.txt")
echo "linker kept (repo fns): $kept    carve kept: $cck    (carve keeps >= linker = sound)"
if [ "$kept" -eq 0 ]; then
    # Nothing in common between the linker's set and the carve's universe: the lists do not describe this
    # build (wrong repo, mangled names, wrong encoding). That proves nothing - never report it as SOUND.
    echo "BAD INPUT: no function the linker kept appears in ccAll - nothing was compared"; exit 3
fi
if [ -s "$work/viol.txt" ]; then
    echo "!!! SOUNDNESS VIOLATIONS - linker kept these, carve DROPPED them:"
    sed 's/^/    /' "$work/viol.txt"
    exit 1
fi
echo "SOUND: every function the linker kept is also kept by the carve."
