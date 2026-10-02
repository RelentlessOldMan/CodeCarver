#!/usr/bin/env bash
# Capture a CodeCarver file-access trace on Linux (via strace) — CONSOLIDATED as it captures.
#
# A file-access trace is the list of files the OS actually opened under your repo during a build or a run.
# strace logs EVERY open syscall, so a raw build capture is enormous and mostly redundant: the compiler
# probes each -I directory for each header (millions of FAILED opens), and re-opens the same headers once
# per translation unit (millions of DUPLICATES). A real build easily hits hundreds of MB / millions of lines.
#
# So this script reduces the capture to what a carve actually uses:
#   1. keep only SUCCESSFUL opens   (a failed probe is not a dependency)
#   2. DEDUPLICATE to the unique set of paths, one per line
# That is lossless for carving — CodeCarver only cares about the SET of files touched (it already de-dups on
# ingest) — and typically turns hundreds of MB into a sub-MB list.
#
# Paths are NOT restricted to the repo: CodeCarver filters to the carve root itself, and treats "a source
# file opened OUTSIDE the root" as a missing-dependency signal — so keeping system/toolchain paths is useful.
# Run from the repo root so relative opens resolve under the carve root (absolute opens always resolve).
#
# Usage:
#   BUILD trace (wrap your REAL build):   ./capture-file-trace.sh build.trace -- make -jN
#   RUN trace (wrap the run/loader):      ./capture-file-trace.sh run.trace   -- ./run-or-flash-tool args
#   RUN trace (attach to a running pid):  ./capture-file-trace.sh run.trace   --pid 1234     # Ctrl-C to stop
#   Also keep the full raw strace:        ./capture-file-trace.sh --raw build.trace -- make -jN   # -> build.trace.raw
#
# NOTE: use a REAL build (e.g. `make -jN`), not `make -n` — a dry run prints commands but opens no headers.
# strace isn't installed? On Debian/Ubuntu: sudo apt-get install strace
set -euo pipefail

raw_keep=0
if [ "${1:-}" = "--raw" ]; then raw_keep=1; shift; fi

if [ "$#" -lt 2 ]; then
  echo "usage: $0 [--raw] <out> -- <command...>   |   $0 [--raw] <out> --pid <pid>" >&2
  exit 2
fi
out="$1"; shift
command -v strace >/dev/null 2>&1 || { echo "strace not found — install it (e.g. apt-get install strace)" >&2; exit 2; }

raw="$out.raw"
cleanup() { [ "$raw_keep" -eq 1 ] || rm -f "$raw"; }
trap cleanup EXIT

# Drop failed opens at the source if this strace supports it (>= 5.2, e.g. Ubuntu 20.04+); otherwise the
# consolidation step below filters them out. Probe once, quietly, so older strace still works.
status_opt=()
if strace -f -e trace=openat -e status=successful -o /dev/null true >/dev/null 2>&1; then
  status_opt=(-e status=successful)
fi

case "${1:-}" in
  --pid)
    pid="${2:-}"; [ -n "$pid" ] || { echo "need a pid: $0 [--raw] <out> --pid <pid>" >&2; exit 2; }
    echo "attaching strace to pid $pid (do your session; Ctrl-C to stop)"
    # -f follows children; swallow SIGINT so we still consolidate + print the footer.
    strace -f -e trace=open,openat "${status_opt[@]}" -o "$raw" -p "$pid" || true
    ;;
  --)
    shift; [ "$#" -ge 1 ] || { echo "need a command after --" >&2; exit 2; }
    echo "capturing: $*"
    strace -f -e trace=open,openat "${status_opt[@]}" -o "$raw" -- "$@"
    ;;
  *)
    echo "usage: $0 [--raw] <out> -- <command...>   |   $0 [--raw] <out> --pid <pid>" >&2
    exit 2
    ;;
esac

# Consolidate: keep successful open/openat lines, pull the path (the single quoted token on an open line),
# and DEDUPLICATE. `grep -v '= -1'` drops any failure that slipped through (older strace without
# status=successful). An unfinished/resumed line carries no result; we keep its path (permissive) — a path
# that isn't a real file is discarded later by CodeCarver's existence check, so this never drops a real input.
( grep -E 'open(at)?\(' "$raw" \
    | grep -v '= -1' \
    | grep -oE '"[^"]*"' \
    | sed -e 's/^"//' -e 's/"$//' \
    | sort -u > "$out" ) || true

n=$(wc -l < "$out" | tr -d ' ')
echo "wrote $out ($n unique path(s))"
[ "$raw_keep" -eq 1 ] && echo "kept raw strace -> $raw"
echo "add it to carve.toml:  buildTraceFiles = [\"$out\"]   (or runTraceFiles for a run capture)"
