#!/bin/sh
# Builds the eldritch horror. Usage: build.sh <src dir> <out dir> <sdk dir>
# Every compile is echoed first: the echoed lines are the build log a carve reads.
# SKIP_MISSING=1 skips listed files that are absent (a carve made without build inputs writes no placeholders).
set -e
S=$(cd "$1" && pwd); O=$2; K=$(cd "$3" && pwd)
mkdir -p "$O"
CF="-O0 -w -I$S/include -I $K/include -include force.h -D VERBOSE_LEVEL=2 @$S/flags.rsp"
cc1() { src=$1; shift; if [ ! -f "$S/$src" ] && [ -n "${SKIP_MISSING:-}" ]; then return 0; fi; echo "gcc $CF $* -c $S/$src"; gcc $CF "$@" -c "$S/$src"; }
cd "$O"
for f in main.c macros.c real.c quiet.c weak_default.c board.c aliases.c cfg_users.c shoggoth.c plain.c forced.c \
         rune.c linuxy.c split_heads.c proto.c dispatch.c handlers.c paste.c xmacro.c unity.c ctor.c asm_target.c \
         parens.c bodyinc.c bodyhelp.c definers.c digraph.c generic.c cleanup.c cleanup_fn.c inlhelp.c wrapped.c \
         weird_heads.c cult.c unused_compiled.c asm_fast.S \
         rename.c horrors.c lined.c unicode.c if0.c hdr_user.c; do
  cc1 "$f" -o "${f%.*}.o"
done
cc1 knr.c -std=gnu89 -o knr.o
cc1 variant.c -DVARIANT=1 -o variant1.o
cc1 variant.c -DVARIANT=2 -o variant2.o
echo "gcc -o eldritch *.o"
gcc -o eldritch *.o
