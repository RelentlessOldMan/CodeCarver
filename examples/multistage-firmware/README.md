# multistage-firmware — the whole process, end to end

An invented (self-contained) Cortex-M-style firmware image that exercises **every** CodeCarver feature through
the TOML config: a build log, observed build/run traces, a `.cmm` loader, dead code, an excluded board variant,
auto-excluded junk, and **three carve stages** of increasing aggressiveness.

## Run it

```
# from the repo root, build once:  dotnet build -c Debug
./run.ps1      # Windows
./run.sh       # Linux/macOS
# or directly:
codecarver carve src --config carve.toml            # all stages -> out/<stage>/
codecarver carve src --config carve.toml --stage max
```

Output lands in `out/<stage>/{carved,codecarver}` (git-ignored; regenerate with `run`).

## What's in the tree (and what each thing demonstrates)

| File | Shows |
|---|---|
| `src/main.c`, `app.c`, `sensor.c`, `comms.c` | the reachable image (entry `main` → init/run_loop → read_sensor → send_status) |
| `src/isr.c` (`Timer_ISR`) | an **implicit root** — reached only via the vector table in `startup.s` (`.word Timer_ISR`) |
| `src/debug.c` | **dead code** — no entry point reaches it, so the carve drops the whole file |
| `src/app.c` `diagnostic_selftest()` | an **unused function in a KEPT file** — kept whole at `safe`, stripped at `aggressive`/`max` |
| `src/sensor.c` `#ifdef FEATURE_FAST` | **#ifdef resolution** — the build log pins `-DFEATURE_FAST`, so the dead `#else` is dropped (closed-world) |
| `src/config.h` | a register header (its unused `#define`s would be stripped by header-carving on a *big* header) |
| `src/startup.s`, `flash.ld`, `Makefile`, `scripts/flash.cmm`, `data/calib.bin` | **infrastructure** — copied verbatim so `out/.../carved` is a complete buildable project |
| `src/boards/old/variant.c` | a board variant NOT in this image — dropped via `excludeDirectories` |
| `src/.git/`, `src/notes.txt.bak` | **auto-excluded** (VCS/editor junk — never a build/run input) |
| `inputs/compile_commands.json` | the **build log** (pins `-D`/`-I` → `#ifdef` world) |
| `inputs/build.procmon.csv` | a **build file-trace** — files opened while *building*; catches inputs the compile log doesn't list: the assembled `startup.s`, the linker script `flash.ld`, the `config.h` the compiler pulled in (all flip to `[observed]`) |
| `inputs/run.procmon.csv` | a **run file-trace** — the loader opened `scripts/flash.cmm` + `data/calib.bin` (kept + `[observed]`) |
| `inputs/run.log` | a **run function-trace** — the functions that actually executed (become roots) |

The build/run traces here are tiny hand-written samples; on a real project you capture them with
[`tools/capture`](../../tools/capture) (ProcMon on Windows, strace on Linux).

## What a run prints (abridged)

```
  roots   : main, Timer_ISR
  trace   : 7 function(s) from 1 trace(s) rooted; 7 resolved in-scope
  files   : 11 observed in-tree from 1 build + 1 run file-trace(s) (6 code rooted)
  files   : 6/7 kept, 1 dropped
  dropped : debug.c
  verify  : OK — every in-scope callee of a kept function is kept
  world   : closed-world (dead #ifdef branches dropped) — have 1 build log(s)
  stage   : safe        [source-contents=whole,  header-contents=whole]   size 2,761 B -> 2,567 B (7% smaller)
  stage   : aggressive  [source-contents=carved, header-contents=whole]   size 2,761 B -> 2,300 B (17% smaller)
  stage   : max         [source-contents=carved, header-contents=carved]  size 2,761 B -> 2,300 B (17% smaller)
```

`aggressive` beats `safe` because `diagnostic_selftest()` is stripped from the kept `app.c`. `max` matches
`aggressive` here only because `config.h` is too small to trigger header-carving — on a real multi-MB register
header, `max` strips the unused `#define`s too.

The per-stage `codecarver/report.txt` shows the four buckets (required-to-build / infrastructure by role /
removed-dead-code / removed-garbage), tags infrastructure the run `[observed]`, and flags kept-but-unobserved
files as drop candidates:

```
== KEPT - infrastructure / other, not code (5) ==
  (file-trace: 5 of 5 observed being opened; 0 NOT observed - candidates to drop if the trace(s) covered a full build+run)
    ...
      flash.ld           [observed]   # from the BUILD trace (not in the compile log)
      startup.s          [observed]   # from the BUILD trace
      scripts/flash.cmm  [observed]   # from the RUN trace
      data/calib.bin     [observed]   # from the RUN trace
== REMOVED - garbage, not a build/run input (3) ==   (notes.txt.bak, build.log, __pycache__/codegen.pyc)
```

## Other input options (not needed here, but you may want them)

This example resolves its `#ifdef` world from a build log and lists its roots inline. Three config options
cover the cases where that isn't enough — add them to `carve.toml` as needed:

- **No build log?** Give the compiler instead — it's probed for its built-in macros — and/or list defines by
  hand. CodeCarver still resolves `#ifdef`s closed-world:
  ```toml
  [builds.main]
  compiler = "arm-none-eabi-gcc"       # probed for built-in macros (__ARM_ARCH, etc.)
  defines  = ["FEATURE_FAST=1", "CHIP=F4"]   # manual -D, when you have neither a log nor the compiler
  ```
- **Something the carve can't see it needs** (a prebuilt `.a`, a generated file, a resource) — pin it so it's
  always kept, even if it sits under an excluded dir or looks unreferenced:
  ```toml
  [common]
  forceKeepFiles = ["prebuilt/*.a", "gen/version.h"]
  ```
- **A long, curated root list** — keep it in a file (one symbol per line, `#` comments allowed) instead of
  inline:
  ```toml
  [common]
  entryPointsFile = "roots.txt"        # unioned with any inline entryPoints
  ```

Run `codecarver init` for the fully annotated config template documenting every key.
