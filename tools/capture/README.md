# Capturing file-access traces

A **file-access trace** records which files the OS opened under your repo during a **build** or a **run**.
CodeCarver uses them to tighten + audit a carve — observed code files are kept, observed files are never
auto-excluded, and the report flags kept-but-unobserved infrastructure. The **run** trace is how you pin the
embedded loader layer (TRACE32 `.cmm` scripts, the binaries/data they load) that no function trace sees.

These helpers produce a capture you drop straight into `carve.toml`:

```toml
[builds.main]
buildTraceFiles = ["build.trace"]

[runs.smoke]
runTraceFiles   = ["run.trace"]
```

## Before you capture

- Capture a **clean, full** build (`make clean && make -jN`, a full rebuild in your IDE). An incremental or
  ccache/sccache-served build opens almost nothing, and `make -n` opens nothing at all.
- If the build **fails**, the trace is still written, but it may be partial. The script warns and exits with
  the build's exit code.
- A capture that yields **zero** paths is an error (exit 3), not an empty success.

## What the scripts keep

The raw capture is written first (it is large: one real Linux build measured **286 MB / 1.1M `.h` opens**).
When the command ends it is reduced to what a carve uses, and the raw file is deleted unless you ask to keep it:

1. only **successful** opens **for reading**, plus programs **executed** / images **loaded** (failed include
   probes, written outputs, attribute probes and directory opens are not dependencies);
2. on Linux, every path **absolute** — taken from the file behind the returned descriptor, so opens relative to
   a sub-make's directory (`make -C`, recursive make) land on the right file. These are *real* paths (symlinks
   resolved): if your repo is reached through a symlink, carve its real path;
3. **deduplicated**, one path per line.

Paths are **not** restricted to the repo: the carve filters to its own root, and uses "a source file opened
*outside* the root" as a missing-dependency signal.

> CodeCarver also accepts a raw strace log or ProcMon CSV unchanged. Consolidating first is strongly
> recommended: it fixes relative paths (Linux) and limits the capture to the build's processes (Windows).

## Windows — `capture-file-trace.ps1` (Process Monitor)

Needs Sysinternals [Process Monitor](https://learn.microsoft.com/sysinternals/downloads/procmon) (`Procmon.exe`
on PATH, or pass `-Procmon <path>`) and an **elevated** (administrator) PowerShell.

ProcMon records **every process on the machine**. The script keeps only the build's process tree (the build
runs under `cmd.exe` and every process it starts), so an indexer, virus scan, IDE or `git status` touching the
repo during the build does not count as "observed". For a run capture, name the program(s) whose activity to
keep with `-ProcessName`; their children are included. While it exists, the `.pml` backing file holds every
process's activity and command line — it is deleted at the end unless you pass `-Raw`.

```powershell
# BUILD trace — wrap your REAL build:
.\capture-file-trace.ps1 -Out build.trace -Build 'msbuild /m firmware.sln'

# RUN trace — do your TRACE32 flash/debug session, then press Enter:
.\capture-file-trace.ps1 -Out run.trace -ProcessName t32marm.exe

# Keep the full ProcMon CSV + .pml too (<Out>.full.csv, <Out>.pml):
.\capture-file-trace.ps1 -Out build.trace -Build 'msbuild /m firmware.sln' -Raw

# Re-consolidate a kept CSV:
.\capture-file-trace.ps1 -Out build.trace -ConsolidateOnly build.trace.full.csv -RootPid 1234
```

ProcMon's column layout must be the default (Time of Day, Process Name, PID, Operation, Path, Result, Detail);
the script checks the CSV header and stops otherwise. It starts ProcMon with `/NoFilter`, so a filter left over
from an interactive session cannot silently empty the capture.

## Linux — `capture-file-trace.sh` (strace)

Needs `strace` (`apt-get install strace`). Tracing another process (`--pid`) may need
`/proc/sys/kernel/yama/ptrace_scope` = 0 or root.

```bash
# BUILD trace — wrap your REAL build:
./capture-file-trace.sh build.trace -- make -jN

# RUN trace — wrap the run/loader:
./capture-file-trace.sh run.trace -- ./run-or-flash-tool args

# RUN trace — attach to an already-running process (Ctrl-C to stop; the capture is still consolidated):
./capture-file-trace.sh run.trace --pid 1234

# Keep the full raw strace too (-> build.trace.raw), and re-consolidate it later:
./capture-file-trace.sh build.trace --raw -- make -jN
./capture-file-trace.sh --consolidate-only build.trace.raw build.trace
```

The consolidated output is a plain one-path-per-line list. Feed it via `buildTraceFiles` / `runTraceFiles` in
`carve.toml`.
