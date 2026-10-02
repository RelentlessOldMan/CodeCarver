# Capturing file-access traces

A **file-access trace** records which files the OS opened under your repo during a **build** or a **run**.
CodeCarver uses them to tighten + audit a carve — observed code files become roots, observed files are kept
(never auto-excluded), and the report flags kept-but-unobserved infrastructure. The **run** trace is how you
pin the embedded loader layer (TRACE32 `.cmm` scripts, the binaries/data they load) that no function trace sees.

These helpers produce a capture you drop straight into `carve.toml`:

```toml
[builds.main]
buildTraceFiles = ["build.trace"]

[runs.smoke]
runTraceFiles   = ["run.trace"]
```

## They consolidate on capture

A raw build trace is huge and mostly redundant — the compiler probes every include directory (millions of
**failed** opens) and re-opens the same headers once per translation unit (millions of **duplicates**). One real
Linux build measured **286 MB / 1.1M `.h` opens** for a repo of fewer than 100k files.

So both scripts reduce the capture to what a carve actually uses, **as they capture**:

1. keep only **successful** opens (a failed include-path probe is not a dependency);
2. **deduplicate** to the unique set of paths, one per line.

This is lossless for carving — CodeCarver only cares about the *set* of files touched (it de-dups on ingest
anyway) — and typically turns hundreds of MB into a sub-MB list. Paths are **not** restricted to the repo: the
carve filters to its own root, and uses "a source file opened *outside* the root" as a missing-dependency
signal, so the scripts keep system/toolchain paths too. Pass `--raw` / `-Raw` to also keep the full capture.

> CodeCarver still accepts a *raw* strace or ProcMon CSV unchanged (it filters to the carve root on ingest) —
> the checked-in examples use small raw CSVs. Consolidation is about the size of the artifact you have to
> store/move, not correctness.

## Windows — `capture-file-trace.ps1` (Process Monitor)

Needs Sysinternals [Process Monitor](https://learn.microsoft.com/sysinternals/downloads/procmon) (`Procmon.exe`
on PATH, or pass `-Procmon <path>`).

```powershell
# BUILD trace — wrap your REAL build:
.\capture-file-trace.ps1 -Out build.trace -Build 'msbuild /m firmware.sln'

# RUN trace — do your TRACE32 flash/debug session, then press Enter:
.\capture-file-trace.ps1 -Out run.trace

# Keep the full intermediate ProcMon CSV + .pml too:
.\capture-file-trace.ps1 -Out build.trace -Build 'msbuild /m firmware.sln' -Raw
```

## Linux — `capture-file-trace.sh` (strace)

Needs `strace` (`apt-get install strace`). Run from the repo root so relative opens resolve.

```bash
# BUILD trace — wrap your REAL build:
./capture-file-trace.sh build.trace -- make -jN

# RUN trace — wrap the run/loader:
./capture-file-trace.sh run.trace -- ./run-or-flash-tool args

# RUN trace — attach to an already-running process (Ctrl-C to stop):
./capture-file-trace.sh run.trace --pid 1234

# Keep the full raw strace too (-> build.trace.raw):
./capture-file-trace.sh --raw build.trace -- make -jN
```

> Use a **real** build (e.g. `make -jN`), not `make -n` — a dry run prints the commands but opens no headers,
> so it produces an empty file trace. (`make -n` is for a build *log*, a different input.)

The consolidated output is a plain one-path-per-line list; raw strace and ProcMon CSV are also auto-detected —
no format configuration needed. Feed the output via `buildTraceFiles` / `runTraceFiles` in `carve.toml`.
