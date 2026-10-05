#!/usr/bin/env bash
# Capture a CodeCarver file-access trace on Linux (via strace), then consolidate it.
#
# A file-access trace is the list of files the OS actually opened under your repo during a build or a run.
# strace logs EVERY open syscall, so a raw build capture is enormous and mostly redundant: the compiler
# probes each -I directory for each header (millions of FAILED opens), and re-opens the same headers once
# per translation unit (millions of DUPLICATES). A real build easily hits hundreds of MB / millions of lines.
#
# strace writes the full raw capture to <out>.raw first; when the command ends it is reduced to what a carve
# uses, and the raw file is deleted (keep it with --raw):
#   1. only SUCCESSFUL opens for READING, plus programs EXECUTED (a failed probe or a written output is not a
#      dependency; an in-tree tool that is only executed is)
#   2. every path ABSOLUTE: strace -y reports the file behind the returned descriptor, so an open relative to a
#      sub-make's working directory (make -C dir, recursive make) is attributed to the right file
#   3. DEDUPLICATED to the unique set of paths, one per line
# That is lossless for carving — CodeCarver only cares about the SET of files touched — and typically turns
# hundreds of MB into a sub-MB list.
#
# Paths are NOT restricted to the repo: CodeCarver filters to the carve root itself, and treats "a source file
# opened OUTSIDE the root" as a missing-dependency signal. Paths are the REAL paths (symlinks resolved): if the
# repo is reached through a symlink, give CodeCarver the real path of the carve root.
#
# Usage:
#   BUILD trace (wrap your REAL build):   ./capture-file-trace.sh build.trace -- make -jN
#   RUN trace (wrap the run/loader):      ./capture-file-trace.sh run.trace   -- ./run-or-flash-tool args
#   RUN trace (attach to a running pid):  ./capture-file-trace.sh run.trace   --pid 1234     # Ctrl-C to stop
#   Also keep the full raw strace:        ./capture-file-trace.sh build.trace --raw -- make -jN   # -> build.trace.raw
#   Re-consolidate a kept raw capture:    ./capture-file-trace.sh --consolidate-only build.trace.raw build.trace
#
# NOTE: capture a CLEAN, FULL build (e.g. `make clean && make -jN`), not an incremental or ccache-served one
# and not `make -n` — those open almost nothing. If the build fails, the trace is still written (it may be
# partial) and the script exits with the build's status.
# strace isn't installed? On Debian/Ubuntu: sudo apt-get install strace
set -uo pipefail

usage() {
  echo "usage: $0 <out> [--raw] -- <command...>" >&2
  echo "       $0 <out> [--raw] --pid <pid>" >&2
  echo "       $0 --consolidate-only <raw> <out>" >&2
  exit 2
}

# Reduce a raw strace log (written with -f -y) to the unique, absolute, successfully-read paths.
consolidate() {
  local raw="$1" out="$2"
  # Successful open/openat/openat2/creat: the path is the one strace -y prints behind the returned fd,
  # "= 3</abs/path>". This form also appears on "<... openat resumed>" lines, so a call split across
  # unfinished/resumed lines is handled. Write-only and directory opens are not dependencies.
  # execve: "execve("/usr/bin/x", ...) = 0" — the executed program.
  {
    grep -E '(open|openat|openat2|creat)\(|(open|openat|openat2|creat) resumed>' "$raw" \
      | grep -v -E 'O_WRONLY|O_DIRECTORY' \
      | sed -n -E 's/.*\) = [0-9]+<(.*)>$/\1/p'
    grep -E 'execve\(' "$raw" | grep -E '\) = 0$' | sed -n -E 's/.*execve\("((\\.|[^"\\])*)".*/\1/p'
  } | decode | sed -e 's/ (deleted)$//' | LC_ALL=C sort -u > "$out"
}

# strace escapes non-ASCII and special bytes: caf\303\251.c, \", \\ . Decode them back to the real bytes.
decode() {
  if command -v perl >/dev/null 2>&1; then
    perl -pe 's/\\([0-7]{3})/chr(oct($1))/ge; s/\\(["\\])/$1/g'
  else
    echo "warning: perl not found — paths with escaped (non-ASCII) characters are left escaped" >&2
    cat
  fi
}

finish() {
  local out="$1" status="$2"
  local n
  n=$(wc -l < "$out" | tr -d ' ')
  if [ "$n" -eq 0 ]; then
    echo "error: the capture produced 0 paths — nothing was traced (did the command run? was the attach refused?)" >&2
    exit 3
  fi
  echo "wrote $out ($n unique path(s))" >&2
  [ "$status" -ne 0 ] && echo "warning: the traced command exited with status $status — the trace may be PARTIAL" >&2
  echo "add it to carve.toml:  buildTraceFiles = [\"$out\"]   (or runTraceFiles for a run capture)" >&2
}

if [ "${1:-}" = "--consolidate-only" ]; then
  [ "$#" -eq 3 ] || usage
  [ -s "$2" ] || { echo "error: raw capture '$2' is missing or empty" >&2; exit 3; }
  consolidate "$2" "$3"
  finish "$3" 0
  exit 0
fi

raw_keep=0
if [ "${1:-}" = "--raw" ]; then raw_keep=1; shift; fi     # also accepted before <out> (older usage)
[ "$#" -ge 2 ] || usage
out="$1"; shift
if [ "${1:-}" = "--raw" ]; then raw_keep=1; shift; fi
command -v strace >/dev/null 2>&1 || { echo "strace not found — install it (e.g. apt-get install strace)" >&2; exit 2; }

raw="$out.raw"
cleanup() { [ "$raw_keep" -eq 1 ] || rm -f "$raw"; }
trap cleanup EXIT

# Trace the syscalls this strace and kernel know; probe each quietly so older hosts still work.
calls="open,openat,execve"
for c in openat2 creat; do
  if strace -f -e trace="$c" -o /dev/null true >/dev/null 2>&1; then calls="$calls,$c"; fi
done
# Drop failed opens at the source if this strace supports it (>= 5.2); consolidation filters them otherwise.
opts=(-f -y -e "trace=$calls")
if strace -f -e trace=openat -e status=successful -o /dev/null true >/dev/null 2>&1; then
  opts+=(-e status=successful)
fi
if strace -f --seccomp-bpf -e trace=openat -o /dev/null true >/dev/null 2>&1; then
  opts+=(--seccomp-bpf)
fi

status=0
case "${1:-}" in
  --pid)
    pid="${2:-}"
    [[ "$pid" =~ ^[0-9]+$ ]] || { echo "need a numeric pid: $0 <out> --pid <pid>" >&2; exit 2; }
    [ -d "/proc/$pid" ] || { echo "error: no process with pid $pid" >&2; exit 2; }
    echo "attaching strace to pid $pid (do your session; Ctrl-C to stop)" >&2
    # Ctrl-C stops strace; this shell ignores it so the capture is still consolidated.
    trap : INT
    strace "${opts[@]}" -o "$raw" -p "$pid" || true
    trap - INT
    ;;
  --)
    shift; [ "$#" -ge 1 ] || usage
    echo "capturing: $*" >&2
    strace "${opts[@]}" -o "$raw" -- "$@"
    status=$?
    ;;
  *) usage ;;
esac

if [ ! -s "$raw" ]; then
  echo "error: strace wrote no capture ($raw is missing or empty) — attach refused (ptrace_scope?) or the command did not start" >&2
  exit 3
fi
consolidate "$raw" "$out"
[ "$raw_keep" -eq 1 ] && echo "kept raw strace -> $raw" >&2
finish "$out" "$status"
exit "$status"
