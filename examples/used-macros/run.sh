#!/bin/sh
# Prove the used-macros example end to end (docs/USED-MACROS.md): build src/ with the -dU collector, carve it from what
# the build printed, then build and run every carved stage and require the original's output.
#
#   sh run.sh <codecarver>          # codecarver.dll (run with dotnet), or codecarver.exe from WSL
#   sh run.sh <codecarver> --save   # ...and refresh inputs/ (the capture checked in for carve.toml)
#
# Needs gcc and make. Works in a copy under $CODECARVER_EXAMPLE_WORK (default /tmp/codecarver-used-macros).
set -eu

here=$(cd "$(dirname "$0")" && pwd)
tool=${1:?usage: run.sh <codecarver.dll|codecarver.exe> [--save]}
save=${2:-}
work=${CODECARVER_EXAMPLE_WORK:-/tmp/codecarver-used-macros}
canon=/build/used-macros   # the captured paths as checked in: a Linux build machine, mapped by carve.toml's pathMap

rm -rf "$work"
mkdir -p "$work/inputs/du"
cp -R "$here/src" "$work/src"
# The copy's config maps the paths this run captures, not the checked-in ones.
sed "s#$canon#$work#g" "$here/carve.toml" > "$work/carve.toml"

# 1. The real build, with the collector in front of gcc. -w prints the directory each make works in.
CODECARVER_DU_DIR="$work/inputs/du" PATH="$here/collect/bin:$PATH" \
    make -w -C "$work/src" O="$work/obj" > "$work/inputs/build.log" 2>&1 || { cat "$work/inputs/build.log"; exit 1; }
run() { "$1/app"; cat "$1/memmap.txt"; }
expected=$(run "$work/obj")
echo "original: $expected" | tr '\n' ' '; echo

# 2. The carve, from the build log and the collected files.
case $tool in
    *.exe) "$tool" carve "$(wslpath -w "$work/src")" --config "$(wslpath -w "$work/carve.toml")" ;;
    *.dll) dotnet "$tool" carve "$work/src" --config "$work/carve.toml" ;;
    *)     "$tool" carve "$work/src" --config "$work/carve.toml" ;;
esac

failed=0
key() { sed -n "s/^$1 = //p" "$work/out/max/codecarver/summary.txt" | tr -d '\r'; }
hc() { key "stage[0-9]*\\.headerCarve\\.$1"; }
if [ "$(key world.usedMacros.trusted)" != 1 ] || [ "$(hc fromUsedMacros)" != 1 ]; then
    echo "FAIL: the max stage did not carve its headers from the used macros"; failed=1
fi
echo "max: kq_regs.h $(hc bytesBefore) -> $(hc bytesAfter) bytes, $(hc definesKept) defines kept, $(hc definesDropped) dropped"

# 3. Every carved stage builds with the same make and prints the same.
for stage in safe max; do
    got=$(make -C "$work/out/$stage/carved" O="$work/obj-$stage" > "$work/build-$stage.log" 2>&1 && run "$work/obj-$stage") || got="BUILD FAILED"
    if [ "$got" = "$expected" ]; then echo "$stage: ok"; else echo "FAIL $stage: $got"; cat "$work/build-$stage.log"; failed=1; fi
done

if [ -n "$save" ] && [ $failed = 0 ]; then
    rm -rf "$here/inputs"
    mkdir -p "$here/inputs/du"
    sed "s#$work#$canon#g" "$work/inputs/build.log" > "$here/inputs/build.log"
    n=0
    for f in "$work"/inputs/du/*; do
        n=$((n + 1))
        sed "s#$work#$canon#g" "$f" > "$here/inputs/du/$n.du"
    done
    echo "saved inputs/ ($n files)"
fi
exit $failed
