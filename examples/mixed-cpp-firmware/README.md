# mixed-cpp-firmware — C + C++ + TRACE32 in one carve

A self-contained firmware image whose code spans **three languages**, carved through a single `carve.toml`:

- **C entry** (`main.c`) reaches the **C++ application** (`hub.cpp`) through a C-linkage bridge (`hub.h`), and
  the C++ sensors call back **down into the C HAL** (`hal.c`). The carve is *one graph* whose reachability
  crosses the C ↔ C++ boundary in **both directions** (`languages = ["c","cpp"]` — the C++ grammar is a
  superset of C, so both parse together).
- A **TRACE32 `.cmm` loader** is kept and tightened via the run trace (observed script + its `DO` closure).

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
| `src/scripts/debug.cmm` | neither observed nor reached by a `DO` — **dropped** by the `.cmm` closure |
| `src/Makefile`, `src/data/calib.bin` | infrastructure — copied verbatim so `carved/` is a complete buildable project |

## What a run prints (abridged)

```
  note    : languages [c, cpp] -> one graph via the C++ grammar (superset of C); reachability crosses the C/C++ boundary.
  build   : 2 build-log(s), 4 compile command(s), 4 file(s)
  roots   : main
  trace   : 5 function(s) from 1 trace(s) rooted; 5 resolved in-scope
  files   : 2 observed in-tree from 0 build + 1 run file-trace(s)
  nodes   : 15/32 kept (47%), 17 carved
  files   : 8/9 kept, 1 dropped
  dropped : unused.cpp
  cmm     : 2/3 script(s) kept (1 observed + 1 via DO/GOSUB closure), 1 dropped
  verify  : OK — every in-scope callee of a kept function is kept
  world   : closed-world (dead #ifdef branches dropped) — have 2 build log(s)
  stage   : safe        size 6,062 B -> 5,445 B  (10% smaller)
  stage   : aggressive  size 6,062 B -> 5,241 B  (14% smaller)
  stage   : max         size 6,062 B -> 5,241 B  (14% smaller)
```

`aggressive` beats `safe` by stripping `TempSensor::selftest` and `hal_unused_calibration` from their (kept)
files. All three carved stages compile **and link** with real gcc/g++ (verified: `image.elf` ≈ 17 KB).

> `PressureSensor::sample()` stays kept even though its registration sits behind `#ifdef SENSOR_PRESSURE`:
> the virtual call resolves by name, so the override is kept regardless — the over-approximation errs toward
> keeping. The build log pins `-DSENSOR_PRESSURE`, so the registration itself is in the image too.
