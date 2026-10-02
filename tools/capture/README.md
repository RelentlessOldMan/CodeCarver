# Capturing file-access traces

A **file-access trace** records which files the OS opened under your repo during a **build** or a **run**.
CodeCarver uses them to tighten + audit a carve — observed code files become roots, observed files are kept
(never auto-excluded), and the report flags kept-but-unobserved infrastructure. The **run** trace is how you
pin the embedded loader layer (TRACE32 `.cmm` scripts, the binaries/data they load) that no function trace sees.

These helpers produce a capture you drop straight into `carve.toml`:

```toml
[builds.main]
buildTraceFiles = ["build.csv"]      # or build.trace on Linux

[runs.smoke]
runTraceFiles   = ["run.csv"]        # or run.trace on Linux
```

You don't have to pre-filter the capture — CodeCarver keeps only paths under the carve root.

## Windows — `capture-file-trace.ps1` (Process Monitor)

Needs Sysinternals [Process Monitor](https://learn.microsoft.com/sysinternals/downloads/procmon) (`Procmon.exe`
on PATH, or pass `-Procmon <path>`).

```powershell
# BUILD trace — wrap your build:
.\capture-file-trace.ps1 -Out build.csv -Build 'make -n'

# RUN trace — do your TRACE32 flash/debug session, then press Enter:
.\capture-file-trace.ps1 -Out run.csv
```

## Linux — `capture-file-trace.sh` (strace)

Needs `strace` (`apt-get install strace`). Run from the repo root so relative opens resolve.

```bash
# BUILD trace — wrap your build:
./capture-file-trace.sh build.trace -- make -n

# RUN trace — wrap the run/loader:
./capture-file-trace.sh run.trace -- ./run-or-flash-tool args

# RUN trace — attach to an already-running process (Ctrl-C to stop):
./capture-file-trace.sh run.trace --pid 1234
```

Both formats (ProcMon CSV, strace) are auto-detected — no format configuration needed. Feed the output via
`buildTraceFiles` / `runTraceFiles` in `carve.toml`.
