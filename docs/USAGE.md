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

**The command line is short on purpose.** It carries the carve verbs, the output targets, and behaviour/diag
toggles you flip per run. **Every input and tuning knob lives in the `--config` file** (run `emit-config` for an
annotated template) — one place, so the CLI doesn't sprawl. A CLI flag that remains still *overrides* the config.

**CLI flags (operational):**

| Input | Flag | Effect |
|---|---|---|
| Entry symbols | `--roots a,b,c` | *(required unless in config)* what to keep |
| Language | `--lang c` \| `cpp` \| `csharp` \| `cmm` | picks the front-end + file extensions (default `c`) |
| Config file | `--config carve.json` | **where all inputs/tuning live** (see the config table below). `emit-config <file>` writes an annotated template |
| Write output | `--out DIR` | emit the carved tree. Written atomically (staged, then swapped) so a crash mid-emit can't leave a half-written tree. Must be **outside** the source tree; a **non-empty** `--out` CodeCarver didn't create is refused (re-carving a prior CodeCarver output is seamless) |
| Replace output | `--clean` | permit replacing a non-empty `--out` CodeCarver did **not** create |
| Carve report | `--report r.txt` | write the carve report (KEPT–required-to-build / REMOVED–dead-code / KEPT–infrastructure grouped by role / REMOVED–garbage; flags look-like-build-outputs). Paths only, no contents. Works with or without `--out` |
| Audit | `--manifest m.json` | JSON manifest of roots, stats, and the kept/dropped/infra/garbage/observed file lists |
| Aggressive prune | `--prune` | intra-file function/table removal (C/C++ only; other languages carve file-level). Always build-verify |
| Soundness gate | `--verify` | compiler-free check: flag any **kept** function that calls an **in-scope** dropped function. Non-zero exit on a violation (CI gate; C/C++) |
| Strict roots | `--strict-roots` | fail the run if any requested root is unresolved (a typo'd ISR/API name would otherwise silently carve the real symbol away) |
| Lenient inputs | `--ignore-missing-inputs` | a missing input file in the config warns + is skipped instead of failing fast |
| Explain | `--why sym` | print the keep-chain for a symbol back to its root (or that it was carved) |
| Dump spans | `--dump-spans` | list every symbol's KEEP/drop decision (debugging) |
| Diagnostics | `--diag report.zip` | ONE **source-free**, shareable diagnostic package (version, env, params, stats, warnings, timings, any failure). Home paths redacted; **no source ever**. Also auto-written to a temp path on an unexpected crash |
| Repro bundle | `--diag-repro` | also attach `repro.graph.json`: the graph with **every name/path replaced by an opaque token** — replay a wrong keep/drop with none of your IP |
| Verbose diag | `--diag-verbose` | also attach `keepdrop.txt`: per-file keep/drop + keep-reason histogram. **Includes NAMES** (never contents); the manifest flags this |

**Config keys (inputs & tuning — JSON only; `emit-config` documents each inline):**

| Key | Was | Effect |
|---|---|---|
| `roots`, `lang`, `out`, `report`, `manifest`, `prune` | — | the CLI flags above can also be set here |
| `exclude: ["tests","vendor"]` | `--exclude` | don't scan those dirs (avoids over-keeping via name clashes) |
| `defines: ["X=1","Y"]` | `--define` | resolve `#ifdef`s → drop dead-branch code |
| `buildLogs: ["build.txt","build.console.txt"]` | `--build-log` | scrape real per-file `-D`/`-I` flags from build log(s) + console capture (see `scan-log`) |
| `probe: "cc"` | `--probe` | run `cc -dM -E` for the compiler's **complete** macro set → closed-world `#ifdef` resolution |
| `assumeDefinesComplete: true` | `--assume-defines-complete` | closed-world without probing: trust the supplied defines as complete |
| `aux: ["board/*.ld"]` | `--aux` | force-keep files even under an `exclude`'d dir or classified as garbage |
| `keepGarbage: true` | `--keep-garbage` | disable default garbage pruning (VCS/scratch/editor/coverage) |
| `traces: ["run.trace"]`, `traceFormat` | `--trace` | function-execution trace(s) → roots (dynamic dispatch) |
| `buildFileTraces`, `runFileTraces`, `fileTraceFormat` | *(new)* | observed file-access traces (build + run) — see the Traces section |
| `maxParseBytes: 20000000` | `--max-parse-bytes` | files bigger than this are kept whole via `#include`-closure, not parsed (multi-GB register headers). Rarely needed — dense headers under the cap are auto-detected |
| `pruneHeaders: true` | `--prune-headers` | **experimental**: strip unused `#define`s from big kept headers. Always build-verify (C/C++) |
| `parseTimeout: 20` | `--parse-timeout` | per-file parse budget in **seconds** (0 disables); a file that blows it is kept whole + warned |
| `maxSymbolsPerFile`, `ignoreMissingInputs` | — | node-explosion backstop; lenient-inputs (also a CLI toggle) |

### Output is a complete, buildable project (keep-by-default)

`--out` is not just the carved C — it's a **whole project you can build**. After emitting the reachable
code and its `#include` closure, CodeCarver copies **every other file in the tree verbatim**: Makefiles /
CMake, linker scripts and scatter/`.cmd` files, startup assembly, device trees, register and data tables,
TRACE32 `.cmm`, prebuilt `.a`/`.o`, board configs — anything that isn't a translation unit the carve
modelled. The rule is **evidence-based removal only**: the files left out are (1) the code already
emitted, (2) code files the carve **proved unreachable** (dead translation units / unreferenced headers),
and (3) **garbage** — files that cannot be a build/run input *by universal convention* (VCS metadata,
compiler/IDE scratch, dep/coverage artifacts, editor/OS junk, logs/temp). Everything else is kept, because
a file the tool didn't model is a file it can't prove you don't need to build.

Garbage pruning is deliberately **conservative**: ambiguous binaries that *could* be vendored prebuilts the
build links (`.o`, `.a`, `.so`, `.lib`, `bin/`, `build/`) are **not** auto-dropped — they're kept and
flagged in the report's "look like build outputs" section so you can `--exclude` them if they're generated.
Disable garbage pruning entirely with `--keep-garbage`, or restore one file with `--aux`.

The knob for trimming the rest is `--exclude DIR` (drop board/arch variants you don't build, or large
non-build trees like `docs`); `--aux GLOB` forces a specific file back in from an excluded folder or the
garbage set.

Because passthrough files are copied byte-for-byte, the headline **size reduction reflects only the code
carve** (dead code removed) — the untouched infrastructure is delta-neutral, and dropped garbage is reported
separately (not folded into the headline %). Use `--report` to see exactly what landed in each bucket.

### Config file — put the inputs in JSON, keep the command line short

Rather than remember a dozen flags, put every input in one JSON file. Generate an **annotated template**
with every feedable slot (blank by default, with fill-in examples, ordered most-common first):

```
carve emit-config carve.json     # write the template  (or: emit-config  -> prints to stdout)
```

Fill in the files you have, delete the rest, and run:

```
carve src/ --config carve.json          # everything from the file
carve src/ --config carve.json --out o/ # ...any CLI flag OVERRIDES the file, so one config drives many stages
```

Key points:
- **Comments and trailing commas are allowed**; the template documents each field inline.
- **Arrays take as many entries as you like** — multiple build logs, multiple traces, etc. (one per line). A
  blank string or empty array means "not set", so emitting the template and running it as-is is a no-op carve.
- **Fail-fast by default:** every referenced *input* file (build logs, traces) is checked up front; a missing
  one stops the run with the full list. Set `"ignoreMissingInputs": true` (or pass `--ignore-missing-inputs`)
  to downgrade that to a warning and continue — handy for a shared config across machines.

```json
{ "roots": ["main"], "lang": "c", "exclude": ["tests"], "prune": true,
  "defines": ["USE_LINUX"], "buildLogs": ["build.log", "build.console.txt"],
  "report": "carve-report.txt", "ignoreMissingInputs": false }
```

### Traces: keep what a real run actually touched

Two optional inputs let an observed run tighten and audit the carve. Both **add** to the sound static carve —
they never silently drop what they didn't see (a trace only proves what *that* run touched).

- **Function trace** (`--trace FILE`, repeatable; `--trace-format REGEX` with a named `fn` group): the functions
  a run executed become roots, covering dynamic dispatch (function pointers, vtables) static analysis
  over-approximates.
- **File-access trace** (**config-only** — `buildFileTraces` / `runFileTraces`, and `fileTraceFormat` for an
  unusual format): the **files the OS actually opened** under the repo. These are a set-once-per-repo input, so
  they live in the `--config` file rather than adding command-line flags. Capture two ways and list both:
  - **build trace** — ProcMon/strace *while building* → the exact compile/link inputs.
  - **run trace** — ProcMon *while flashing/running* → the loader/orchestration layer (TRACE32 `.cmm` scripts,
    the binaries and data they load) that a function trace can't see and that static analysis can't resolve
    (its `DO`/`Data.LOAD` targets are computed `&var` paths). The OS reports the *concrete* path regardless.

  Observed **code** files become roots (keep the file + its closure); every observed file is kept (never pruned as
  garbage), and the report tags observed infrastructure and flags the **kept-but-unobserved** files as drop
  candidates. The reader is format-tolerant — ProcMon CSV, `strace -e trace=openat`, or a plain path-per-line list
  all work; set `fileTraceFormat` only for an unusual format.

  ```jsonc
  // in carve.json — Windows: Process Monitor, filter Path to your repo, export each capture to CSV:
  "buildFileTraces": ["build.csv"],
  "runFileTraces":   ["flash.csv"]
  ```
  ```
  carve repo/ --config carve.json --out o/ --report r.txt
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

On a large tree it prints a **live, self-calibrating ETA** while parsing (the dominant phase) — e.g.
`parsing : 22% (4,376/20,075 files, 1.2 MB/s) -- ETA ~6m 41s`. It's calculated, not guessed: measured
throughput on this run × the known remaining source bytes, refined every few seconds (first estimate after
a ~3 s warmup). Printed to stderr, so it never pollutes `--dump-spans`/`--manifest` output. Reachability
and emit are a short tail after parsing; emit scales with how much is *kept*.

It's built to survive a messy real tree: a file it can't read or can't parse is **skipped with a
`warn:` line and kept whole** (never crashes the whole run), multi-GB generated headers are parsed-
skipped (`--max-parse-bytes`), and a file that blows the parse budget is kept whole (`--parse-timeout`).

**Macro-dense register headers are handled automatically** — no flag, no magic number. A big chip/register
map (a few MB of almost-nothing-but-`#define`s, transitively `#include`d) sits *under* the byte cap but
would still explode parser memory (a graph node per `#define` + retained AST). CodeCarver samples the first
256 KB of each ≥1 MB file; if it's overwhelmingly `#define`s it's routed to the same keep-whole path as
oversized files (no parse, stream-copied verbatim) and reported on a `dense:` line. This is sound — the
header is kept whole regardless — and a normal large `.c` (mostly code) is unaffected and still parsed.

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
