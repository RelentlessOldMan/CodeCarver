# Using CodeCarver

CodeCarver carves a repo down to only the code needed for a chosen entry set, and writes out a
minimal tree that still **builds**. This is the practical guide: the commands, the inputs, and how to
dial in how aggressively it carves.

> Build the CLI: `dotnet build CodeCarver.sln -c Release`. Examples below use `dotnet run --project
> src/CodeCarver.Cli --`; a published binary exposes the same `carve` / `scan-log` commands.

## Quick start

```
carve <dir> --roots foo,bar               # analyze: what's needed for foo() and bar()
carve <dir> --roots foo,bar --out out/    # write the carved tree (file-level, the sound default)
```

Point it at a source directory and name the entry symbols (functions) you need. It traces every
dependency and reports what's kept vs. dropped, with a size summary.

Every requested root that resolves to nothing is reported (`warn: requested root '<x>' was NOT found`),
and the summary's `roots` line calls out unresolved names — so a typo among a long hand-maintained root
list can't silently carve a real symbol away and look like a bigger win. Add **`--strict-roots`** to
make *any* unresolved root a hard failure (non-zero exit) — recommended for a firmware image whose roots
are a curated ISR/exported-API list (see `docs/WORKREPO.md`).

```
CodeCarver — carve of .corpus/cJSON
  roots   : cJSON_Parse, cJSON_Delete
  nodes   : 76/1943 kept (4%), 1867 carved
  files   : 2/5 kept, 3 dropped
  dropped : cJSON_Utils.c, cJSON_Utils.h, test.c
  size    : 155,085 B -> 100,598 B  (35% smaller, saved 54,487 B)
```

## The two carve levels

| Mode | Flag | What it does | Soundness |
|---|---|---|---|
| **File-level** | *(default)* | Drops whole unneeded files/translation units; keeps kept files verbatim | **Sound** — the safe default |
| **Intra-file** | `--prune` | Also rewrites kept files to remove unreached **functions and data tables** | Aggressive — **always build-verify** |

`--prune` is where the big size wins are (e.g. carving AES to encrypt-only drops the inverse S-box
table). It's been validated to compile on cJSON, zlib, Lua, SQLite, printf and tiny-AES-c, but the C
preprocessor has a long tail of exotica, so treat pruned output as "verify by building it."

## The tightness ladder (optional inputs)

Every carve is sound with just `--roots`. Each extra input lets it carve **tighter**, never looser.

> **Implicit roots are always added.** Symbols the runtime/linker keep regardless of any call —
> `__attribute__((constructor))`/`((destructor))`/`((used))`/`((retain))` and `.init_array`-family
> section placement — are auto-discovered and kept (reported on an `implicit:` line), so a self-registering
> driver or an initcall table isn't silently dropped. Vector-table ISRs are kept the same way (via the
> file-scope address-taken edge). Symbols referenced only from a standalone `.s`/`.S` startup file are
> rooted too (reported on an `asm:` line). And a symbol placed in a **custom section your linker script
> keeps** — `__attribute__((section(".init_calls")))` where the `.ld` has `KEEP(*(.init_calls*))` — is
> rooted from the tree's own linker script (reported on a `section:` line), generalising the hardcoded
> `.init_array` handling to any KEEP'd registration/initcall table. Finally, a symbol called only from a
> generated table `#include`d with a **non-source extension** (`#include "GenTables.inc"`, `.def`, X-macro
> files) is kept too: those includes aren't parsed as C, but every symbol they name is retained and the
> file is copied into the output. This is the sound over-approximation; it never drops these.

| Input | Flag | Effect |
|---|---|---|
| Entry symbols | `--roots a,b,c` | *(required)* what to keep |
| Language | `--lang c` \| `cpp` \| `csharp` | picks the front-end + file extensions (default `c`) |
| Config file | `--config carve.json` | load any of these options from JSON (CLI flags override it) |
| Skip directories | `--exclude tests,vendor` | don't scan those dirs (avoids over-keeping via test-file name clashes) |
| Config macros | `--define X=1,Y` | resolve `#ifdef`s → drop dead-branch code |
| Build log | `--build-log build.txt` | scrape real per-file `-D` flags from a build log (see `scan-log`) |
| Probe compiler | `--probe cc` | run `cc -dM -E` for the compiler's **complete** macro set (predefined + target + `-D`) and resolve `#ifdef`s closed-world against it — accurate, no "is my define list complete?" guess |
| Complete-config | `--assume-defines-complete` | closed-world without probing: trust the supplied defines as complete |
| Write output | `--out DIR` | emit the carved tree |
| Aux build files | `--aux "Makefile,*.cmd"` | extra non-source files to copy verbatim into `--out` (Makefiles, TI `.cmd` linker files, …) on top of the linker scripts + startup assembly copied automatically |
| Aggressive prune | `--prune` | intra-file function/table removal (C/C++ only; other languages carve file-level) |
| Audit | `--manifest m.json` | write a JSON manifest of roots, stats, kept/dropped files, byte counts |
| Explain | `--why sym` | print the keep-chain for a symbol back to its root (or that it was carved) — debugging "why is this still here / why did this drop?" |
| Big-file cutoff | `--max-parse-bytes N` | files larger than `N` bytes (default 20 MB) are **not parsed** — kept whole via `#include`-closure, copied verbatim. Lets a carve survive multi-GB auto-generated register headers that would otherwise blow past .NET's ~2 GB string limit and explode parser memory (C/C++/`.cmm` only) |
| Carve headers | `--prune-headers` | **experimental**: strip unused `#define`s from the big kept headers above (a 1.4 GB register map → the handful of registers you use). Streaming + sound — keeps the transitive closure of needed defines, every non-`#define` line (guards, `#if`, types), all `#if`-referenced names, and token-paste (`##`) candidate families. Always build-verify (C/C++ only) |
| Parse budget | `--parse-timeout N` | per-file parse budget in **seconds** (default 20). A file whose parse blows it is kept whole + warned — a backstop against tree-sitter's super-linear error recovery on invalid `#include` fragments stalling a run |
| Soundness gate | `--verify` | compiler-free check: flag any **kept** function that calls an **in-scope** function the carve dropped (it wouldn't link). Exits non-zero on a violation, so it's usable as a CI/script gate (C/C++). Catches an edge the model missed — the useful check when you have no build |

### Config file

```json
{ "roots": ["main"], "lang": "c", "exclude": ["tests"], "prune": true,
  "defines": ["USE_LINUX"], "assumeDefinesComplete": false }
```

```
carve src/ --config carve.json          # everything from the file
carve src/ --config carve.json --out o/ # ...plus a CLI override
```

### Languages

- **C / C++** (`--lang c` / `cpp`): full support — calls, macros, globals, `#include` closure, `#ifdef`
  resolution, file-level **and** `--prune` intra-file carving, all compile-verified.
- **C#** (`--lang csharp`): **file-level** carving (drop unused `.cs` files). Sound intra-file method
  pruning needs semantic analysis (Roslyn), so `--prune` falls back to file-level for C#.
- **TRACE32 `.cmm`** (`--lang cmm`): file-level carving of PRACTICE scripts — subroutines (`Label:`),
  `GOSUB`/`GOTO` calls, and `DO` includes. Roots are subroutine names. `--prune` falls back to
  file-level (`.cmm` is interpreted, not compiled, and computed `GOSUB &var` is a dispatch blind spot).

### `#ifdef` resolution: open vs. closed world

By default (`--define`/`--build-log` supplied), resolution is **open-world**: a macro that's neither a
supplied define nor defined in the file is treated as *unknown*, so its branch is **kept** (a header
might define it). This is sound — it only drops what it's certain about (`#if 0`, definite conditions,
and branches after a definitely-taken one).

Add `--assume-defines-complete` for **closed-world**: the supplied defines are trusted as the complete
macro set, so absent macros are undefined and their branches are dropped. Powerful for dropping
other-platform code, but only correct when your define set really is complete (e.g. taken from a
preprocessed build). Example — carve Lua for Linux, dropping Windows/dyld paths:

```
carve lua/ --roots luaopen_package --build-log lua.build.log --assume-defines-complete --prune --out out/
```

## Getting a build log

Most builds don't emit a `compile_commands.json`. Any verbose build log works — even a dry run:

```
make -n > build.log            # prints the compiler command lines without building
carve scan-log build.log       # shows the translation units, defines, includes it found
carve src/ --roots main --build-log build.log --prune --out out/
```

`scan-log` recognizes gcc/clang/cc/cl and cross drivers (arm-none-eabi-gcc, …), GNU `-D/-I` and MSVC
`/D //I`, multi-source lines, `cd dir &&` prefixes, and quoted paths.

## Verify the output builds

Intra-file pruning is aggressive, so build what you carved. CodeCarver never *runs* anything; the
guarantee is only ever "it compiles/links":

```
carve src/ --roots main --prune --out out/
cc -c out/*.c -Iout/            # or your real build, pointed at out/
```

## First run on a large repo

Recommended workflow the first time you point it at a big, unfamiliar tree:

```
carve <dir> --roots a,b,c                 # 1. analysis only (no --out): stats, dropped files, warnings
carve <dir> --roots a,b,c --verify        # 2. soundness gate: flags any kept fn calling a dropped one (exit 3)
carve <dir> --roots a,b,c --prune --out o --manifest m.json   # 3. emit + a JSON audit of what was kept
cc -c o/*.c -Io/                          # 4. build the output (the only real guarantee)
```

It's built to survive a messy real tree: a file it can't read or can't parse is **skipped with a
`warn:` line and kept whole** (never crashes the whole run), multi-GB generated headers are parsed-
skipped (`--max-parse-bytes`), and a file that blows the parse budget is kept whole (`--parse-timeout`).
Read the `warn:` lines — they're where the carve was uncertain. For a slow run, `CODECARVER_TIMING=1`
prints a per-phase + slow-file breakdown to stderr.

## Diagnostics

```
carve <dir> --roots foo --dump-spans     # per-definition KEEP/drop with file:line and kind
carve <dir> --roots foo --why sym        # why a symbol was kept (its chain to a root) or that it was carved
```

## What CodeCarver does not do

- It doesn't *run* your program to carve it (no runtime, no hardware in the loop).
- It errs toward keeping code when a reference is ambiguous (soundness over minimality), so pruned
  output can carry a little unused code — but it should always build.
- Correctness is bounded by input fidelity: wrong `--define`s or a wrong `--build-log` give a wrong
  carve. Feed it the real build's config.
