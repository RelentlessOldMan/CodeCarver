#!/bin/sh
# Builds the eldritch horror. Usage: build.sh <src dir> <out dir> <sdk dir>
# Every compile is echoed first: the echoed lines are the build log a carve reads.
# SKIP_MISSING=1 skips listed files that are absent (a carve made without build inputs writes no placeholders).
set -e
S=$(cd "$1" && pwd); mkdir -p "$2"; O=$(cd "$2" && pwd); K=$(cd "$3" && pwd)
CF="-O0 -w -I$S/include -I $K/include -I$S/include2 -iquote $S/quoted -isystem $S/sys -idirafter $S/after -include force.h -D VERBOSE_LEVEL=2 @$S/flags.rsp"
# EXTRA_CFLAGS / EXTRA_LDFLAGS: the oracle builds an instrumented copy this way to record which functions run.
CF="$CF${EXTRA_CFLAGS:+ $EXTRA_CFLAGS}"
has() { [ -f "$S/$1" ] || [ -z "${SKIP_MISSING:-}" ]; }
cc1() { src=$1; shift; has "$src" || return 0; echo "gcc $CF $* -c \"$S/$src\""; gcc $CF "$@" -c "$S/$src"; }
cd "$O"
for f in main.c macros.c real.c quiet.c weak_default.c board.c aliases.c cfg_users.c shoggoth.c plain.c forced.c \
         rune.c linuxy.c split_heads.c proto.c dispatch.c handlers.c paste.c xmacro.c unity.c ctor.c asm_target.c \
         parens.c bodyinc.c bodyhelp.c definers.c digraph.c generic.c cleanup.c cleanup_fn.c inlhelp.c wrapped.c \
         weird_heads.c cult.c unused_compiled.c asm_fast.S \
         rename.c horrors.c lined.c unicode.c if0.c hdr_user.c \
         beast.c wrap_beast.c wrap.c asmlabel.c deep.c asmdef.c tmpl.c tmpl_user.c rites_a.c rites_b.c rites.c \
         spell.c selfref.c omen.c undef.c world.c world_fns.c world_light.c world_early.c world_late.c \
         ifexpr.c ifexpr_hi.c ifexpr_lo.c ifexpr_uns.c ifexpr_unsw.c ifexpr_misc.c ifexpr_miscw.c crlf.c crlf_helper.c \
         nested.c far.c tri_helper.c curse.c hex.c gate.c gate_helper.c soup.c soup_fns.c attr_fns.c fnref.c rot.c \
         gnuinl.c gnu_twin.c c99inl_user.c c99inl_emit.c ifunc.c ifunc_impl.c ifunc_user.c defsym.c defsym_real.c \
         dren_user.c next.c next_one.c next_two.c dirs.c dirs_q.c dirs_sys.c dirs_after.c asm_caller.S asm_callee.c \
         ucn.c dollar.c ucn_user.c comments.c ghost.c comments_user.c pushpop.c pp_alpha.c pp_beta.c cppkw.c kw_user.c \
         macarg.c macarg_user.c splitp.c splitp_user.c dl.c dl_target.c abyss_one.c abyss_three.c redef.c redef_user.c \
         latin1.c nulbyte.c keywords.c audit_tick.c keywords_user.c pascal.c pascal_user.c priest.c priest_user.c \
         duff.c duff_step.c boss.c boss_helper.c; do
  cc1 "$f" -o "${f%.*}.o"
done
cc1 knr.c -std=gnu89 -o knr.o
cc1 variant.c -DVARIANT=1 -o variant1.o
cc1 variant.c -DVARIANT=2 -o variant2.o
cc1 trigraph.c -trigraphs -o trigraph.o
cc1 attrs.c -std=gnu2x -o attrs.o
cc1 tome.inc -x c -o tome.o
cc1 "spaced out.c" -o spaced_out.o
# Command-line macros that rename what the file defines; the log shows them quoted, as a makefile would.
if has dren.c; then
  echo "gcc $CF -Dsecret_rite=true_rite '-DHIDE(n)=hid_##n' -c \"$S/dren.c\" -o dren.o"
  gcc $CF -Dsecret_rite=true_rite '-DHIDE(n)=hid_##n' -c "$S/dren.c" -o dren.o
fi
# A compile run from another directory, with relative include paths.
if has deep/abyss.c; then
  echo "cd $S/deep && gcc $CF -I. -Iinner -c abyss.c -o $O/abyss.o"
  (cd "$S/deep" && gcc $CF -I. -Iinner -c abyss.c -o "$O/abyss.o")
fi
echo "gcc${EXTRA_LDFLAGS:+ $EXTRA_LDFLAGS} -rdynamic -Wl,--wrap=beast -Wl,--defsym=omen_call=omen_real -o eldritch *.o"
gcc${EXTRA_LDFLAGS:+ $EXTRA_LDFLAGS} -rdynamic -Wl,--wrap=beast -Wl,--defsym=omen_call=omen_real -o eldritch *.o
