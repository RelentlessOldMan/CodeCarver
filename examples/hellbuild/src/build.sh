#!/bin/sh
# The hellbuild. Usage: build.sh <src dir> <out dir> <sdk dir>. Everything it prints is the build log.
# configure, then a few compiles make cannot express, then a recursive parallel make from the build directory.
set -e
S=$(cd "$1" && pwd); mkdir -p "$2"; O=$(cd "$2" && pwd); K=$(cd "$3" && pwd)
sh "$S/configure" "$O"
CF="-O0 -w -I$S/common/inc -I $K/include"
# Through a symlink in the build directory: the log names a path outside the tree.
ln -sfn "$S/linked" "$O/alias"
echo "gcc $CF -DLNK=1 -c $O/alias/lnk.c -o $O/lnk.o"
gcc $CF -DLNK=1 -c "$O/alias/lnk.c" -o "$O/lnk.o"
# A response file written into the build directory: the log names it, and it is gone with the build.
printf '%s\n' "-DRSP_MODE=3" "-c" "$S/rsp.c" "-o" "$O/rsp.o" > "$O/rsp.args"
echo "gcc $CF @$O/rsp.args"
gcc $CF @"$O/rsp.args"
# File names a shell and a makefile fight over.
echo "gcc $CF -c \"$S/café.c\" -o $O/cafe.o"
gcc $CF -c "$S/café.c" -o "$O/cafe.o"
echo "gcc $CF -c \"$S/it's here.c\" -o $O/its_here.o"
gcc $CF -c "$S/it's here.c" -o "$O/its_here.o"
make -w -j4 -C "$O" -f "$S/Makefile" S="$S" K="$K"
