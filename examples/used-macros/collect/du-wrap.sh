#!/bin/sh
# CodeCarver used-macros collector (docs/USED-MACROS.md), a worked example to adapt, not a drop-in.
#
#   du-wrap.sh <driver> <args...>
#
# Runs the real <driver> with <args> exactly as the build asked; its exit status is the build's. Then, if that was a
# compile or a preprocessor run and CODECARVER_DU_DIR is set, runs the same command again as `-E -dU` and writes what
# it printed to a new file in CODECARVER_DU_DIR:
#
#   #codecarver-cwd <the directory the compile ran in>
#   <every line of the -E -dU output that starts with #: line markers, #define and #undef of each macro used>
#   #codecarver-ok           (only when the -E -dU run exited 0)
#
# GCC only: Clang ignores -dU. The second run costs about one more preprocess per compile.
set -u

driver=$1
shift

# ADAPT: how to find the real compiler. Here: the next <driver> on PATH that isn't our own bin/ stand-in.
here=$(cd "$(dirname "$0")" && pwd)
real=${CODECARVER_REAL_CC:-}
if [ -z "$real" ]; then
    old_ifs=$IFS
    IFS=:
    for d in $PATH; do
        [ "$d" = "$here/bin" ] && continue
        if [ -x "$d/$driver" ]; then real="$d/$driver"; break; fi
    done
    IFS=$old_ifs
fi
[ -n "$real" ] || { echo "du-wrap.sh: no real $driver on PATH" >&2; exit 127; }

"$real" "$@" || exit $?
[ -n "${CODECARVER_DU_DIR:-}" ] || exit 0

# Rebuild the arguments for a preprocess-only run: drop what selects the output, the dependency files and -P (the
# carve needs the line markers); note whether this run compiled or preprocessed at all (a link does neither).
# ADAPT: one source per command is assumed; a build that passes several sources to one command needs one -E run each.
# ADAPT: other options that write files (-save-temps, -fdump-*, a vendor's listing flags) belong in the drop list.
pre=
skip=
first=1
for a do
    if [ -n "$first" ]; then set --; first=; fi
    if [ -n "$skip" ]; then skip=; continue; fi
    case $a in
        -c|-S|-E) pre=1; continue ;;
        -o|-MF|-MT|-MQ) skip=1; continue ;;
        -o*|-MD|-MMD|-MP|-M|-MM|-MG|-MF*|-MT*|-MQ*|-Wp,-M*|-P) continue ;;
    esac
    set -- "$@" "$a"
done
[ -n "$pre" ] || exit 0

mkdir -p "$CODECARVER_DU_DIR"
out=$(mktemp "$CODECARVER_DU_DIR/du.XXXXXXXX") || exit 0
tmp=$out.i
printf '#codecarver-cwd %s\n' "$PWD" > "$out"
if "$real" "$@" -E -dU -o "$tmp" 2>/dev/null; then
    grep '^#' "$tmp" >> "$out"
    echo '#codecarver-ok' >> "$out"
fi
rm -f "$tmp"
exit 0
