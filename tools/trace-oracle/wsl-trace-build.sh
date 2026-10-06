#!/usr/bin/env bash
# A REAL clean build of a chosen subset of a corpus's translation units. Used by carver-groundtruth-oracle.ps1 -TraceBuild.
#
#   wsl-trace-build.sh trace <corpus dir> <list> <out dir> <capture-file-trace.sh>
#       The original build, captured with tools/capture: <out>/build.log (each compile command echoed, like a console
#       log) and <out>/build.trace, which a carve takes as [builds.X] buildLogs / buildTraceFiles. Files that can't
#       compile (a fixture may include a header that isn't in the tree) are checked OUTSIDE the capture and left out
#       of the build; they go to <out>/failed.txt.
#   wsl-trace-build.sh link <tree dir> <list> <out dir>
#       The same file list compiled from another tree (the carved one): every listed file must exist and compile.
#
# Both end by combining every object into one (ld -r) and writing its undefined symbols to <out>/undefined.txt, so
# the carved build can be checked to need nothing the original build didn't. Objects go to <out>/obj.
set -uo pipefail
mode="$1"; tree="$2"; list="$3"; out="$4"; capture="${5:-}"
rm -rf "$out/obj"; mkdir -p "$out/obj"

if [ "$mode" = "trace" ]; then
  : > "$out/failed.txt"
  ok="$out/list.ok"; : > "$ok"
  while IFS= read -r f || [ -n "$f" ]; do
    f="${f%$'\r'}"
    [ -z "$f" ] && continue
    if gcc -fsyntax-only -w "$tree/$f" 2>/dev/null; then echo "$f" >> "$ok"; else echo "$f" >> "$out/failed.txt"; fi
  done < "$list"
  list="$ok"
fi

gen="$out/build.sh"
{
  echo '#!/usr/bin/env bash'
  echo 'set -e'
  i=0
  while IFS= read -r f || [ -n "$f" ]; do
    f="${f%$'\r'}"
    [ -z "$f" ] && continue
    i=$((i + 1))
    cmd="gcc -c -O0 -w -DCARVE_TRACE_ORACLE=1 $tree/$f -o $out/obj/o$i.o"
    echo "echo '$cmd'"
    echo "$cmd"
  done < "$list"
} > "$gen"

if [ "$mode" = "trace" ]; then
  bash "$capture" "$out/build.trace" -- bash "$gen" > "$out/build.log" 2>&1
else
  bash "$gen" > "$out/build.log" 2>&1
fi
status=$?
if [ $status -eq 0 ]; then
  ld -r -o "$out/all.o" "$out"/obj/*.o && nm -u "$out/all.o" | awk '{print $NF}' | sort -u > "$out/undefined.txt" || status=$?
fi
echo "$mode: compiled $(grep -c '^gcc ' "$out/build.log") file(s); exit $status"
exit $status
