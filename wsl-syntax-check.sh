#!/usr/bin/env bash
# Syntax-check every source file under a dir with gcc/g++ -fsyntax-only, print "OK <relpath>" for each
# that compiles (warnings suppressed). Used by corpus-compile-sweep.ps1 to diff a repo's baseline
# (uncarved) compilable set against the carved set: a file that compiled before but not after is a carve
# soundness bug (dropped symbol / broken structure). Standalone-uncompilable files (need build config)
# simply never appear as OK in either set, so they're ignored -- exactly what we want.
#
#   wsl-syntax-check.sh <dir> <c|cpp> <comma-separated-include-dirs-relative-to-dir>
set -u
dir="$1"; lang="$2"; incs="${3:-}"
[ -d "$dir" ] || { echo "no dir: $dir" >&2; exit 3; }
cc=gcc; std=""; pat=( -name '*.c' )
if [ "$lang" = cpp ]; then cc=g++; std="-std=c++17"; pat=( -name '*.cpp' -o -name '*.cc' -o -name '*.cxx' ); fi

incflags=""
IFS=',' read -ra ID <<< "$incs"
for d in "${ID[@]}"; do [ -n "$d" ] && incflags="$incflags -I$dir/$d"; done
incflags="$incflags -I$dir"

cd "$dir" || exit 3
# skip the usual non-buildable trees
find . \( "${pat[@]}" \) 2>/dev/null | grep -viE '/(test|tests|example|examples|doc|docs|bench|fuzz|tool|tools|sample|samples)/' | while read -r f; do
    if $cc $std -fsyntax-only -w $incflags "$f" >/dev/null 2>&1; then
        echo "OK ${f#./}"
    fi
done
