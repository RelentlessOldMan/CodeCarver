#!/usr/bin/env bash
# Capture a CodeCarver file-access trace on Linux (via strace).
#
# A file-access trace is the list of files the OS actually opened under your repo during a build or a run.
# Feed the output to a carve.toml:  buildTraceFiles = ["build.trace"]  /  runTraceFiles = ["run.trace"].
# You do NOT need to filter it — CodeCarver keeps only paths under the carve root, so a raw capture is fine.
# Run from the repo root so relative opens resolve under the carve root (absolute opens always resolve).
#
# Usage:
#   BUILD trace (wrap your build):      ./capture-file-trace.sh build.trace -- make -n
#   RUN trace (wrap the run/loader):    ./capture-file-trace.sh run.trace   -- ./run-or-flash-tool args
#   RUN trace (attach to a running pid):./capture-file-trace.sh run.trace   --pid 1234     # Ctrl-C to stop
#
# strace isn't installed? On Debian/Ubuntu: sudo apt-get install strace
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "usage: $0 <out> -- <command...>   |   $0 <out> --pid <pid>" >&2
  exit 2
fi
out="$1"; shift
command -v strace >/dev/null 2>&1 || { echo "strace not found — install it (e.g. apt-get install strace)" >&2; exit 2; }

case "${1:-}" in
  --pid)
    pid="${2:-}"; [ -n "$pid" ] || { echo "need a pid: $0 <out> --pid <pid>" >&2; exit 2; }
    echo "attaching strace to pid $pid (do your session; Ctrl-C to stop)"
    # -f follows children; swallow the SIGINT so we still print the footer.
    strace -f -e trace=open,openat -o "$out" -p "$pid" || true
    ;;
  --)
    shift; [ "$#" -ge 1 ] || { echo "need a command after --" >&2; exit 2; }
    echo "capturing: $*"
    strace -f -e trace=open,openat -o "$out" -- "$@"
    ;;
  *)
    echo "usage: $0 <out> -- <command...>   |   $0 <out> --pid <pid>" >&2
    exit 2
    ;;
esac

echo "wrote $out"
echo "add it to carve.toml:  buildTraceFiles = [\"$out\"]   (or runTraceFiles for a run capture)"
