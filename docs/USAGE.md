# Using CodeCarver

CodeCarver carves a repo down to only the code needed for a chosen entry set, and writes out a
minimal tree that still **builds**. This is the practical guide: the commands, the inputs, and how to
dial in how aggressively it carves.

> Build the CLI: `dotnet build CodeCarver.sln -c Release`. The tool is invoked as `codecarver <command>`
> (`carve`, `init`, `scan-log`, `version`); examples below write `carve`/`init` for brevity.

## Quick start

CodeCarver is driven by **one TOML config file**, so the command line stays tiny:

```
codecarver init carve.toml                              # write an annotated config template
#   edit carve.toml: set entryPoints + outputDirectory (and a buildLog if you have one)
codecarver carve <source-dir> --config carve.toml      # carve
codecarver carve <source-dir> --config carve.toml --stage max      # run one named stage
codecarver carve <source-dir> --config carve.toml --why <symbol>   # explain why a symbol is kept/dropped
```

The config names the entry symbols to keep and where output goes; CodeCarver traces every dependency and
writes a **complete buildable project** plus a `report.txt` + `manifest.json`. A missing named entry point
fails the run, and the compiler-free soundness check runs automatically (the `verify` line). For a full
config that exercises every feature, see the worked **`examples/multistage-firmware/`**.

```
  roots   : cJSON_Parse, cJSON_Delete
  nodes   : 76/1943 kept (4%), 1867 carved
  files   : 2/5 kept, 3 dropped
  verify  : OK — every in-scope callee of a kept function is kept
  world   : open-world (both #ifdef branches kept) — no build log or compiler given
  emitted : 2 files -> out/carved  [file-level (whole kept files)]
  size    : 155,085 B -> 100,598 B  (35% smaller, saved 54,487 B)
```

## How aggressively it carves (per stage)

File-level (drop whole unneeded files) is always the sound floor. Two independent config toggles go deeper:

| Config key | What it does | Soundness |
|---|---|---|
| `carveSourceFileContents` | also remove unreached **functions and data tables** within kept `.c` files | aggressive — **always build-verify** |
| `carveHeaderFileContents` | also strip unused `#define`s from kept headers | aggressive — **always build-verify** |

Intra-file carving is where the big wins are (e.g. carving AES to encrypt-only drops the inverse S-box
table) — validated to compile on cJSON, zlib, Lua, SQLite, printf and tiny-AES-c, but treat it as "verify
by building it." Define named **`[stages.NAME]`** blocks to emit several aggressiveness tiers in one run
(`--stage` picks one; omit it to run them all, each into `outputDirectory/<stage>/`).

## The tightness ladder (optional inputs)

Every carve is sound with just `entryPoints`. Each extra input lets it carve **tighter**, never looser.

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

## The CLI

The command line is tiny — everything else lives in the config:

| Command | What it does |
|---|---|
| `carve <source-dir> --config carve.toml` | carve (all stages). `--stage <name>` runs one; `--why <symbol>` explains one symbol |
| `init [carve.toml]` | write the annotated config template (won't overwrite an existing file) |
| `scan-log <build-log>` | preview what a build log scrapes (TUs, defines, includes) |
| `version` | the build's git-stamped version |

## The config file (`carve.toml`)

`init` writes this annotated; every option is a TOML key. (Booleans accept `true`/`false` or `yes`/`no`.)

**Top level**
- `outputDirectory` — where output goes: `<dir>/[<stage>/]carved` (a complete buildable project) + a sibling
  `codecarver/{report.txt, manifest.json, resolved-config.toml}`, always written. Must be **outside** the source.
- `analysisOnly = true` — decide only (plan + report + manifest), don't emit a tree. A fast dry run.

**`[common]`** — the whole carve:

| Key | Effect |
|---|---|
| `entryPoints = ["main","Reset_Handler"]` | the symbols to keep — a missing named one **fails** the run. Or `entryPointsFile = "roots.txt"` (one per line) for a long list. |
| `languages = ["c","cpp"]` | source languages (`c`/`cpp`/`csharp`). A mixed `["c","cpp"]` tree is carved as **one graph** (reachability crosses the C/C++ boundary — a C root reaching an `extern "C"` C++ callee is kept). `csharp` is a separate graph — carve it on its own. asm is auto-scanned for roots; `.cmm` is handled via run traces. |
| `excludeDirectories = ["tests","boards/old"]` | directories to drop (tests, other board/arch variants). Nested paths OK. |
| `forceKeepFiles = ["prebuilt/*.a"]` | globs to **always** keep (even under an excluded dir or auto-excluded as a non-input) |
| `carveSourceFileContents`, `carveHeaderFileContents` | the two aggressiveness toggles (above) |

**`[builds.NAME]`** — how the real compiler sees the code (define several build *steps*; selected builds **union**):

| Key | Effect |
|---|---|
| `buildLogs = ["make-n.log","build.console.txt"]` | scrape real per-file `-D`/`-I` from a `make -n` log, console capture, or `compile_commands.json`. **The** way to pin `#ifdef`s. |
| `compiler = "arm-none-eabi-gcc"` | probe the compiler for its built-in macros |
| `defines = ["CHIP=F4"]` | rare manual override when you have no build log |
| `buildTraceFiles = ["build.csv"]` | files opened while **building** (capture with `tools/capture`) |

**`[runs.NAME]`** — what a real execution touched (optional; tightens + audits, never drops the unobserved):

| Key | Effect |
|---|---|
| `runTraceFiles = ["flash.csv"]` | files opened while **running/flashing** — catches the loader/`.cmm`/data layer |
| `runTraceLogs = ["run.log"]` | functions that actually ran (one name per line, or `name file:line`) → roots |

**`[stages.NAME]`** — aggressiveness tiers; each sets the two carve toggles. `--stage` picks one; omit to run all.
**`[use]`** — `builds = [...]` / `runs = [...]` to select a subset (default: all defined).
**`[advanced]`** — rarely needed escape hatches: `maxParseBytes`, `parseTimeout`, `maxSymbolsPerFile`.

Three things are **automatic**: the open/closed `#ifdef` world is derived (a build log or compiler ⇒ closed-world,
dead branches dropped; otherwise open-world, both kept); the compiler-free **soundness check** runs every carve
(the `verify` line); and non-inputs (VCS/scratch/editor junk) are **auto-excluded** (`forceKeepFiles` un-drops one).

### Output is a complete, buildable project (keep-by-default)

The carved output (`outputDirectory/[<stage>/]carved`) is not just the carved C — it's a **whole project you
can build**. After emitting the reachable code and its `#include` closure, CodeCarver copies **every other
file in the tree verbatim**: Makefiles / CMake, linker scripts and scatter/`.cmd` files, startup assembly,
device trees, register and data tables, TRACE32 `.cmm`, prebuilt `.a`/`.o`, board configs — anything that
isn't a translation unit the carve modelled. The rule is **evidence-based removal only**: the files left out
are (1) the code already emitted, (2) code files the carve **proved unreachable** (dead translation units /
unreferenced headers), and (3) **auto-excluded non-inputs** — files that cannot be a build/run input *by
universal convention* (VCS metadata, compiler/IDE scratch, dep/coverage artifacts, editor/OS junk,
logs/temp). Everything else is kept, because a file the tool didn't model is a file it can't prove you don't
need to build.

Auto-exclusion is deliberately **conservative**: ambiguous binaries that *could* be vendored prebuilts the
build links (`.o`, `.a`, `.so`, `.lib`, `bin/`, `build/`) are **not** auto-dropped — they're kept and flagged
in the report's "look like build outputs" section so you can `excludeDirectories` them if they're generated.
Restore any wrongly-excluded file with `forceKeepFiles`.

The knob for trimming the rest is `excludeDirectories` (drop board/arch variants you don't build, or large
non-build trees like `docs`); `forceKeepFiles` forces a specific file back in from an excluded folder or the
auto-excluded set.

Because passthrough files are copied byte-for-byte, the headline **size reduction reflects only the code
carve** (dead code removed) — the untouched infrastructure is delta-neutral, and auto-excluded files are
reported separately (not folded into the headline %). The `report.txt` shows what landed in each bucket.

### A minimal config

See "The config file" above for the full key reference; `init` writes an annotated template. A minimal
single-stage carve with a build log:

```toml
outputDirectory = "D:/carved/myimage"

[common]
entryPoints = ["main", "Reset_Handler"]
languages   = ["c"]
excludeDirectories = ["tests"]

[builds.main]
buildLogs = ["build.log", "build.console.txt"]   # comments and arrays are fine; list several logs
```

A referenced **input** file that's missing (a build log/trace) **fails fast** with the full list, so a typo'd
path can't silently carve with less config than intended.

### Traces: keep what a real run actually touched

Two optional inputs let an observed run tighten and audit the carve. Both **add** to the sound static carve —
they never silently drop what they didn't see (a trace only proves what *that* run touched).

- **Function trace** (config `[runs.smoke] runTraceLogs = ["run.log", ...]`): the functions a run executed become
  roots, covering dynamic dispatch (function pointers, vtables) static analysis over-approximates. One standard
  format — a function name per line, optionally `name file:line`; write a small per-product converter to it
  (there's no regex knob).
- **File-access trace** (config `[builds.main] buildTraceFiles` / `[runs.smoke] runTraceFiles`): the **files the OS
  actually opened** under the repo. These are a set-once-per-repo input, so they live in the config. Capture two
  ways and list both:
  - **build trace** — ProcMon/strace *while building* → the exact compile/link inputs.
  - **run trace** — ProcMon *while flashing/running* → the loader/orchestration layer (TRACE32 `.cmm` scripts,
    the binaries and data they load) that a function trace can't see and that static analysis can't resolve
    (its `DO`/`Data.LOAD` targets are computed `&var` paths). The OS reports the *concrete* path regardless.

  Observed **code** files become roots (keep the file + its closure); every observed file is kept (never pruned as
  garbage), and the report tags observed infrastructure and flags the **kept-but-unobserved** files as drop
  candidates. The reader **auto-detects** the format — ProcMon CSV, `strace -e trace=openat`, or a plain
  path-per-line list all work; it extracts the path tokens and keeps only those under the carve root that exist.

  ```toml
  # in carve.toml
  [builds.main]
  buildTraceFiles = ["build.csv"]
  [runs.smoke]
  runTraceFiles   = ["flash.csv"]
  ```
  ```
  carve repo/ --config carve.toml
  ```

#### Capturing a file-access trace

The extractor is tolerant and the "under the carve root + file exists" filter drops noise, so you don't have to
produce a clean list — a raw capture works. Any capture tool that records opened paths is fine; the common ones:

**Windows — the RUN trace (TRACE32 flash/debug session) — Process Monitor (Sysinternals ProcMon), GUI:**
1. Launch `Procmon.exe`. Press **Ctrl+E** to stop the initial capture, **Ctrl+X** to clear.
2. **Ctrl+L** (Filter) → add `Path` **begins with** `C:\path\to\repo` → **Include** (optionally also `Operation` **is** `ReadFile` → Include, to shrink it). Apply.
3. **Ctrl+E** to start capturing, then **run your TRACE32 flash/run session** end to end, then **Ctrl+E** to stop.
4. **File → Save** → *Events displayed using current filter* → format **CSV** → `flash.csv`. Put it in `runTraceFiles`.

**Windows — the BUILD trace — ProcMon from the command line (scriptable):**
```
Procmon.exe /AcceptEula /Quiet /Minimized /BackingFile C:\caps\build.pml
<your build>                                   # e.g. make / cmake --build / the IDE build
Procmon.exe /Terminate
Procmon.exe /OpenLog C:\caps\build.pml /SaveAs C:\caps\build.csv   # -> buildTraceFiles
```
(The same GUI steps as above also work for the build — just build instead of flashing between the Ctrl+E's.)

**Linux — the BUILD trace — strace:**
```
strace -f -e trace=open,openat -o build.trace -- make <target>
```
`-f` follows the compiler/sub-make forks; the reader parses strace's `openat(AT_FDCWD, "path", …)` lines directly,
so `buildTraceFiles = ["build.trace"]` just works. **Run it from the repo root** so relative opens resolve under the
carve root (absolute-path opens always resolve). `fatrace -c` or a `bpftrace` openat probe work too. A build on
Linux targeting embedded is the usual case; a Windows build is the ProcMon recipe above.

**Linux — the RUN trace — strace (if you launch the run) or fatrace (if you don't):**
```
# a) you start the run/loader yourself:
strace -f -e trace=open,openat -o run.trace -- ./run-or-flash-tool <args>     # -> runTraceFiles

# b) the run is launched by something you don't control (daemon/debugger) — attach by PID:
strace -f -p <pid> -e trace=open,openat -o run.trace                          # Ctrl+C to stop

# c) system-wide during the run window (no PID needed), with fatrace:
fatrace > run.fatrace        # run the session, then Ctrl+C
```
strace options (a)/(b) use quoted paths, so they feed `runTraceFiles` as-is. `fatrace` lines look like
`comm(pid): R /abs/path` (the path is **not** quoted), so reduce them to a plain path-per-line list first —
the reader auto-detects that:
```
awk '{print $NF}' run.fatrace > run.trace     # -> runTraceFiles = ["run.trace"]
```
(On a desktop/host build the "run" is just your program; for embedded-on-hardware the loader runs on the *host*,
so trace the host loader process — same recipe.)

> Not a function trace — these record *files*, which is the whole point: they catch the orchestration + data layer
> a function trace can't see. Over-capturing (writes, directory scans, un-exercised paths) is harmless: it only
> ever keeps more, and the report shows you exactly what each trace touched.

### Languages

- **C / C++** (`languages = ["c","cpp"]`): full support — calls, macros, globals, `#include` closure, `#ifdef`
  resolution, file-level **and** `carveSourceFileContents` intra-file carving, all compile-verified.
- **C#** (`languages = ["csharp"]`): **file-level** carving (drop unused `.cs` files). Sound intra-file method
  pruning needs semantic analysis (Roslyn), so `carveSourceFileContents` falls back to file-level for C#.
- **TRACE32 `.cmm`**: file-level, trace-driven (keep the `.cmm` a run trace shows were used); handled via
  `runTraceFiles`, not `languages`. Never carved internally.

### `#ifdef` resolution: open vs. closed world

CodeCarver **derives this automatically** (the report's `world:` line says which and why):
- **Open-world** (neither a build log nor a compiler given): a macro that's neither supplied nor defined in
  the file is *unknown*, so its `#ifdef` branch is **kept** (a header might define it). Sound — it only drops
  what it's certain about (`#if 0`, definite conditions, branches after a definitely-taken one).
- **Closed-world** (a `buildLogs` or a `compiler` is given, so the define set is known): an absent macro is
  undefined and its branch is **dropped** — tighter, and correct because the build told us the real set.

So: feed a build log (or set a build's `compiler`) to carve tighter; give neither to stay safe. Example —
carve Lua for Linux, dropping Windows/dyld paths (config pins the build via `buildLogs`):

```
carve lua/ --config carve.toml --stage prune
```

## Getting a build log

Most builds don't emit a `compile_commands.json`. Any verbose build log works — even a dry run:

```
make -n > build.log            # prints the compiler command lines without building
carve scan-log build.log       # shows the translation units, defines, includes it found
# then in carve.toml:  [builds.main] buildLogs = ["build.log"]
carve src/ --config carve.toml
```

`scan-log` recognizes gcc/clang/cc/cl and cross drivers (arm-none-eabi-gcc, …), GNU `-D/-I` and MSVC
`/D //I`, multi-source lines, `cd dir &&` prefixes, and quoted paths.

## Verify the output builds

Intra-file pruning is aggressive, so build what you carved. CodeCarver never *runs* anything; the
guarantee is only ever "it compiles/links":

```
carve src/ --config carve.toml   # with carveSourceFileContents=true (e.g. a [stages.prune])
cc -c out/carved/*.c -Iout/carved/   # or your real build, pointed at the carved tree
```

## First run on a large repo

Recommended workflow the first time you point it at a big, unfamiliar tree:

```
# in carve.toml: analysisOnly=true for the dry run; drop it (and add [stages]) for the real emit.
carve <dir> --config carve.toml           # 1. with analysisOnly=true: stats, dropped files, warnings (no emit)
                                          #    the compiler-free soundness check runs automatically (verify line)
carve <dir> --config carve.toml           # 2. real carve -> outputDirectory/[<stage>/]carved + codecarver/
cc -c out/carved/*.c -Iout/carved/        # 3. build the output (the only real guarantee)
```

On a large tree it prints a **live, self-calibrating ETA** while parsing (the dominant phase) — e.g.
`parsing : 22% (4,376/20,075 files, 1.2 MB/s) -- ETA ~6m 41s`. It's calculated, not guessed: measured
throughput on this run × the known remaining source bytes, refined every few seconds (first estimate after
a ~3 s warmup). Printed to stderr, so it never pollutes stdout. Reachability
and emit are a short tail after parsing; emit scales with how much is *kept*.

It's built to survive a messy real tree: a file it can't read or can't parse is **skipped with a
`warn:` line and kept whole** (never crashes the whole run), multi-GB generated headers are parsed-
skipped (config `maxParseBytes`), and a file that blows the parse budget is kept whole (config `parseTimeout`).

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
carve <dir> --config carve.toml --why sym   # why a symbol was kept (its chain to a root) or that it was carved
```
(Per-symbol keep/drop detail also goes to `codecarver/report.txt` / `manifest.json` each run.)

## What CodeCarver does not do

- It doesn't *run* your program to carve it (no runtime, no hardware in the loop).
- It errs toward keeping code when a reference is ambiguous (soundness over minimality), so pruned
  output can carry a little unused code — but it should always build.
- Correctness is bounded by input fidelity: wrong `defines` or a wrong `buildLogs` give a wrong carve.
  Feed it the real build's config.
