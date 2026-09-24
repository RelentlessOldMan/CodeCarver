#!/usr/bin/env bash
# C++ link oracle (run inside WSL / Linux, needs g++: sudo apt install -y g++).
# The most robust soundness check for a C++ carve: LINK the CARVED --out tree against a tiny
# driver main() that calls the roots, with --gc-sections. If the linker reports an
# `undefined reference`, the carve DROPPED a symbol something reachable from a root needs —
# a genuine soundness bug (this is exactly how the C-side adler32/Z_PREFIX bug surfaced).
#
#   wsl-cpp-oracle.sh <carvedDir> <driver.cpp> [extra g++ flags...]
#     INC="-I<carvedDir>"   (defaults to -I<carvedDir>)   LIBS="..."   STD="-std=c++17"
#
# Example (after carving to .oracle-cpp/tinyxml2 on the Windows side):
#   INC="-I/mnt/c/Playground/CodeCarver/.oracle-cpp/tinyxml2" \
#   bash wsl-cpp-oracle.sh /mnt/c/Playground/CodeCarver/.oracle-cpp/tinyxml2 \
#        /mnt/c/Playground/CodeCarver/oracle/tinyxml2_driver.cpp
set -u
carved="$1"; driver="$2"; shift 2
[ -d "$carved" ] || { echo "no carved dir: $carved"; exit 3; }
[ -f "$driver" ] || { echo "no driver: $driver"; exit 3; }
command -v g++ >/dev/null 2>&1 || { echo "g++ not installed — run: sudo apt install -y g++"; exit 4; }

inc="${INC:--I$carved}"
std="${STD:--std=c++17}"
# Only the carved translation units (top-level .cpp/.cc in the carved tree), plus the driver.
mapfile -t units < <(find "$carved" -maxdepth 2 \( -name '*.cpp' -o -name '*.cc' -o -name '*.cxx' \) \
                     ! -name '*test*' ! -name '*demo*' ! -name 'html5-printer*')
echo "linking ${#units[@]} carved unit(s) + driver with g++ $std ..."
if g++ $std -O0 -ffunction-sections -fdata-sections $inc "$@" \
       "${units[@]}" "$driver" ${LIBS:-} -Wl,--gc-sections -o /tmp/cpp_oracle_elf 2>/tmp/cpp_oracle_err; then
    echo "SOUND: carved C++ tree links + the driver's root calls all resolve."
    exit 0
fi
echo "!!! LINK/COMPILE FAILED against the CARVED tree:"
# Surface the highest-signal lines: undefined references (dropped symbol) and compile errors.
grep -E 'undefined reference|error:|expected|has no member|was not declared' /tmp/cpp_oracle_err | head -25
echo "--- (full log: /tmp/cpp_oracle_err) ---"
exit 1
