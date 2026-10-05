#!/usr/bin/env bash
# C++ link oracle (run inside WSL / Linux, needs g++: sudo apt install -y g++).
# The most robust soundness check for a C++ carve: LINK the CARVED --out tree against a tiny
# driver main() that calls the roots, with --gc-sections. If the linker reports an
# `undefined reference`, the carve DROPPED a symbol something reachable from a root needs -
# a genuine soundness bug (this is exactly how the C-side adler32/Z_PREFIX bug surfaced).
#
#   wsl-cpp-oracle.sh <carvedDir> <driver.cpp> [extra g++ flags...]
#     INC="-I<carvedDir>"   (defaults to -I<carvedDir>)   LIBS="..."   STD="-std=c++17"
#     EXCLUDE="<regex>"     extended regex matched against each unit's BASENAME (e.g. 'fmt\.cc|fmt-c')
#     MAXDEPTH=2            how deep under <carvedDir> to collect units (deeper ones are listed, not linked)
#
# Exit: 0 SOUND, 1 link/compile failed, 3 bad arguments, 4 g++ missing.
#
# Example (after carving to .oracle-cpp/tinyxml2 on the Windows side):
#   INC="-I/mnt/c/Playground/CodeCarver/.oracle-cpp/tinyxml2/carved" \
#   bash wsl-cpp-oracle.sh /mnt/c/Playground/CodeCarver/.oracle-cpp/tinyxml2/carved \
#        /mnt/c/Playground/CodeCarver/oracle/tinyxml2_driver.cpp
set -u
[ $# -ge 2 ] || { echo "usage: $0 <carvedDir> <driver.cpp> [g++ flags...]"; exit 3; }
carved="$1"; driver="$2"; shift 2
[ -d "$carved" ] || { echo "no carved dir: $carved"; exit 3; }
[ -f "$driver" ] || { echo "no driver: $driver"; exit 3; }
command -v g++ >/dev/null 2>&1 || { echo "g++ not installed - run: sudo apt install -y g++"; exit 4; }

inc="${INC:--I$carved}"
std="${STD:--std=c++17}"
excl="${EXCLUDE:-}"
maxdepth="${MAXDEPTH:-2}"
work="$(mktemp -d "${TMPDIR:-/tmp}/cpp_oracle.XXXXXX")" || { echo "mktemp failed"; exit 3; }
keep=0
cleanup() { [ "$keep" -eq 1 ] || rm -rf "$work"; }
trap cleanup EXIT

# Only the carved translation units, plus the driver. EXCLUDE applies to the basename only, so a regex can
# never accidentally match a directory component of the carved path.
units=()
while IFS= read -r -d '' u; do
    b="$(basename "$u")"
    case "$b" in *test*|*demo*|html5-printer*) continue ;; esac
    if [ -n "$excl" ] && printf '%s\n' "$b" | grep -qE -- "$excl"; then continue; fi
    units+=("$u")
done < <(find "$carved" -maxdepth "$maxdepth" \( -name '*.cpp' -o -name '*.cc' -o -name '*.cxx' \) -print0)

# Units below MAXDEPTH are not linked; say so instead of ignoring them silently.
deeper="$(find "$carved" -mindepth "$((maxdepth + 1))" \( -name '*.cpp' -o -name '*.cc' -o -name '*.cxx' \) | head -5)"
[ -n "$deeper" ] && { echo "note: units deeper than MAXDEPTH=$maxdepth are NOT linked, e.g.:"; printf '  %s\n' "$deeper"; }

echo "linking ${#units[@]} carved unit(s) + driver with g++ $std ..."
# $inc, $std and $LIBS are flag lists: word splitting is intended.
# shellcheck disable=SC2086
if g++ $std -O0 -ffunction-sections -fdata-sections $inc "$@" \
       ${units[@]+"${units[@]}"} "$driver" ${LIBS:-} -Wl,--gc-sections -o "$work/elf" 2>"$work/err"; then
    echo "SOUND: carved C++ tree links + the driver's root calls all resolve."
    exit 0
fi
keep=1
echo "!!! LINK/COMPILE FAILED against the CARVED tree:"
# Surface the highest-signal lines: undefined references (dropped symbol) and compile errors.
grep -E 'undefined reference|error:|expected|has no member|was not declared' "$work/err" | head -25
echo "--- (full log: $work/err) ---"
exit 1
