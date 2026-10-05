# mixed-cpp-firmware — C + C++ + TRACE32 in one carve

A self-contained firmware image whose code spans **three languages**, carved through a single `carve.toml`:

- **C entry** (`main.c`) reaches the **C++ application** (`hub.cpp`) through a C-linkage bridge (`hub.h`), and
  the C++ sensors call back **down into the C HAL** (`hal.c`). The carve is *one graph* whose reachability
  crosses the C ↔ C++ boundary in **both directions** (`languages = ["c","cpp"]` — the C++ grammar is a
  superset of C, so both parse together).
- A **TRACE32 `.cmm` loader** is audited by the run trace (observed script + its `DO` closure are reported;
  every script is kept, because this config does not opt in to `dropUnobservedCmm`).

It also demonstrates the two things a single-build/single-run example can't: the **union of multiple builds**
and the **union of multiple runs**.

## Run it

```
# from the repo root, build once:  dotnet build -c Debug
./run.ps1      # Windows
./run.sh       # Linux/macOS
# or directly:
codecarver carve src --config carve.toml            # all stages -> out/<stage>/
codecarver carve src --config carve.toml --stage max
```

Output lands in `out/<stage>/{carved,codecarver}` (git-ignored; regenerate with `run`). Every carved stage is
a complete, buildable project — `./verify-build.sh` compiles & links all three with gcc/g++ (needs a toolchain).

## What each input channel shows

| Config | Input | Demonstrates |
|---|---|---|
| `[builds.hal]` | `hal.compile_commands.json` (gcc) | the C hardware layer's build |
| `[builds.app]` | `app.compile_commands.json` (g++, `-DSENSOR_PRESSURE`) | the C++ app's build — **unioned** with `hal` into one #ifdef world |
| `[runs.flash]` | `flash.procmon.csv` | a **file-access trace** — the loader opened `scripts/flash.cmm` + `data/calib.bin` |
| `[runs.smoke]` | `run.log` | a **function trace** — functions that executed — **unioned** with `flash` |
| `[stages.*]` | — | three aggressiveness tiers (safe / aggressive / max) |

Two builds + two runs, all unioned by default (no `[use]`); the config shows a commented `[use]` to pick a subset.

## What each file demonstrates

| File | Shows |
|---|---|
| `src/main.c` (C) | entry `main` → calls the C++ bridge `hub_init` / `hub_sample` — the **C → C++** edge |
| `src/hub.cpp` (C++) | `extern "C"` bridge + a `Sensor*` registry; `hub_sample` makes a **virtual `sample()` call** |
| `src/sensors.{hpp,cpp}` (C++) | a class hierarchy — the virtual call keeps **both** `TempSensor::sample` and `PressureSensor::sample` by name (sound vtable over-approximation) |
| `src/filter.hpp` (C++) | a **template** in a namespace; `dsp::smooth<4>(x)` is an explicit template-argument call the front-end must see as a call edge |
| `src/hal.c` (C) | `hal_read_adc` called from the C++ sensors — the **C++ → C** edge back down |
| `src/sensors.cpp` `TempSensor::selftest` | an **unused method in a kept file** — kept at `safe`, stripped at `aggressive`/`max` |
| `src/hal.c` `hal_unused_calibration` | an **unused C function in a kept file** — stripped at `aggressive`/`max` |
| `src/unused.cpp` (C++) | **dead translation unit** — `diag::DebugSensor` is never referenced and overrides no called method, so the whole file is dropped |
| `src/scripts/flash.cmm` | the `.cmm` loader — **observed** in the flash file-trace (kept) |
| `src/scripts/common.cmm` | kept via the **`DO` closure** (`flash.cmm` does `DO common`), though the trace never named it directly |
| `src/scripts/debug.cmm` | neither observed nor reached by a `DO` — **kept**, and counted as "neither" on the `cmm` line: one run is one scenario. Adding `dropUnobservedCmm = true` to `[runs.flash]` would drop it (see [`cmm-trace`](../cmm-trace)) |
| `src/Makefile`, `src/data/calib.bin` | infrastructure — copied verbatim so `carved/` is a complete buildable project |

## What a run prints (abridged)

```
  note    : languages [c, cpp] -> one graph via the C++ grammar (superset of C); reachability crosses the C/C++ boundary.
  build   : 2 build-log(s), 4 compile command(s), 4 file(s); per-file #ifdef config (universal 0/1 macro(s); the rest vary per TU -> both branches kept)
  build   : 1 source file(s) appear in no compile command -> open-world for them (both #ifdef branches kept)
  files   : 2 observed in-tree from 0 build + 1 run file-trace(s) (0 code file(s) rooted)
  roots   : main
  trace   : 5 function(s) from 1 trace(s) rooted; 5 resolved in-scope
  nodes   : 15/32 kept (47%), 17 carved
  files   : 8/9 kept, 1 dropped
  dropped : unused.cpp
  cmm     : 3 script(s) kept; 1 observed + 1 via DO/GOSUB closure, 1 neither (kept: one run is one scenario — set [runs.X] dropUnobservedCmm = true to drop them)
  world   : closed-world (dead #ifdef branches dropped) — have 4 compile command(s); macros #defined in the tree and compiler built-ins not probed for the TU stay unknown
  stage   : safe  [source-contents=whole, header-contents=whole]
  verify  : OK — emitted code uses no function defined only in a dropped file (9 file(s) checked)
  size    : 6,228 B -> 5,611 B  (10% smaller, saved 617 B)
  stage   : aggressive  [source-contents=carved, header-contents=whole]
  size    : 6,228 B -> 5,407 B  (13% smaller, saved 821 B)
  stage   : max  [source-contents=carved, header-contents=carved]
  size    : 6,228 B -> 5,407 B  (13% smaller, saved 821 B)
```

`aggressive` beats `safe` by stripping `TempSensor::selftest` and `hal_unused_calibration` from their (kept)
files. `max` equals `aggressive` because header carving only touches big headers (over `maxParseBytes`, or
macro-dense and 1 MB or more). The carved stages compile **and link** with real gcc/g++ (`verify-build.sh`).

> `PressureSensor::sample()` stays kept even though its registration sits behind `#ifdef SENSOR_PRESSURE`:
> the virtual call resolves by name, so the override is kept regardless — the over-approximation errs toward
> keeping. The build log pins `-DSENSOR_PRESSURE`, so the registration itself is in the image too.
