# Toolchain & platform notes

CodeCarver is toolchain-generic by design: the engine has no vendor knowledge, and everything
that differs between compilers/platforms is confined to three adapter seams. This document is the
spec those adapters implement. It also records the Windows-dev / Linux-build reality: **the tool
runs on Windows but must understand code built by a Linux (GNU/ELF) toolchain.**

## What actually varies (and what doesn't)

Parsing C/C++/C# is OS- and vendor-independent — the *language* is standardized. Only a narrow band
is toolchain-specific:

| Concern | Varies by | Where it lives |
|---|---|---|
| Which `#ifdef` branches are live | the `-D` defines + `-I` paths of the build | **config provider** |
| Predefined macros (`__GNUC__`, `_MSC_VER`, target macros) | the compiler | **config provider** (probe) |
| Which functions are *actually* in the image (ground truth) | the linker | **oracle** (map file) |
| Implicit roots (vector table, init/ctor arrays, KEEP, exports) | platform + linker | **root provider** |
| Symbol/section container (ELF vs PE/COFF) | platform | **root provider / oracle** |

The reachability core and graph model never touch any of this.

## Seam 1 — Config provider: getting per-file defines + includes

The one thing the front-end needs to resolve the preprocessor deterministically: for each source
file, its exact `-D` macros and `-I` include paths. Sources, in priority order of fidelity:

1. **`compile_commands.json`** if the build happens to emit one (CMake `-DCMAKE_EXPORT_COMPILE_COMMANDS=ON`,
   Ninja, or Bear-wrapped make). Convenient, but many builds (custom make/batch, proprietary embedded
   IDEs) never produce one — do not assume it.
2. **Verbose build-log scrape** — the build-system-agnostic path. Whatever compiler is invoked, its
   full command line carries every `-D`/`-I`. Get a verbose log and parse the compile lines:
   - GNU make: `make V=1` or `make VERBOSE=1`; many trees echo the raw `cc ...` lines already.
   - MSBuild: `msbuild /v:detailed` or a **binlog** (`/bl`) — richest, fully structured.
   - Proprietary/embedded: usually a make/batch build with a verbose switch; the compile lines are
     what we scrape regardless of vendor.
3. **Probe the compiler** for the parts a per-file command line omits — its *built-in* predefined
   macros and default include search paths:
   - GNU (gcc/clang): `cc -dM -E -` (all predefined macros), `cc -E -v -` (default include dirs).
   - MSVC (`cl`): predefined macros via a documented probe (`cl /Bx`-style / `/Zc:preprocessor` +
     an empty `/EP`); default includes come from the environment (`INCLUDE`) set by the VS dev shell.

Flag-syntax differences (GNU `-D FOO=1 -I dir` vs MSVC `/D FOO=1 /I dir`, response/`@file` args) are
normalized by the config provider into one internal `CompileCommand` model.

## Seam 2 — Front-end: source → graph

Preprocess with the **real compiler** (`<cc> -E` with the file's flags) so every `#ifdef` collapses
exactly and generically — any compiler can preprocess. Then extract symbols and edges (including the
conservative ones: address-taken, vtable, inline-asm) from the resolved translation unit. Parser tech
is a separate decision (clang semantic vs. tree-sitter on preprocessed text); either way the
preprocessing step is compiler-driven and vendor-neutral.

## Seam 3 — Root provider: implicit roots by platform

The roots a from-`main` closure silently drops. Discovery is platform-specific:

- **ELF / GNU (Linux target — our first-class case):**
  - `.init_array`, `.preinit_array`, `.fini_array`; `__attribute__((constructor/destructor))`.
  - `__attribute__((used))` / `retain`; symbols named in linker-script `KEEP( )` directives (parse the `.ld`).
  - Exported/dynamic symbols (default visibility, `.dynsym`) for a shared object.
- **ARM Cortex-M / embedded (the target's likely shape):**
  - The **vector table** — an array of ISR addresses (usually in `startup_*.s/.c`) placed at the
    image base (or wherever VTOR points). Every entry is a root; the ISRs are never called in source.
  - Scatter/linker files (`.scf`, `.ld`) with `KEEP`/`ENTRY`/root regions.
- **PE/COFF / MSVC (when building on Windows):**
  - CRT init tables (`.CRT$XCU` static initializers), `ENTRY`, `/INCLUDE:` forced symbols,
    `__declspec(dllexport)` / `.def` exports.

## The regression oracle — the real linker as ground truth

To test the carve on real code without hand-labeling, let the actual toolchain compute the answer:

```
# GNU
cc -ffunction-sections -fdata-sections *.c -o img \
   -Wl,--gc-sections -Wl,--print-gc-sections -Wl,-Map=img.map
```

The **map file** (and `--print-gc-sections` output) lists exactly which functions/sections landed in
the image after the linker's own dead-strip. The contract we test:

- **Soundness (hard pass/fail):** CodeCarver's kept set ⊇ the linker's kept set. We must never drop
  something the real image contains. A violation is a bug.
- **Precision (measured, not pass/fail):** how much *extra* we keep beyond the linker. This is the
  fat that indirection forces us to conservatively retain; we track it and try to shrink it, but
  over-keeping is safe (must-build).

MSVC equivalent: `link /OPT:REF /MAP /MAPINFO:EXPORTS`. ARM: `armlink --map --info=sizes,unused`.

This oracle needs a compiler present on the dev box (GNU via MSYS2/mingw or clang on Windows, or
WSL). It validates our own small buildable fixtures; large fetched repos that won't build on Windows
are used for **parse-robustness** (front-end must extract a graph without choking), not the map oracle.
