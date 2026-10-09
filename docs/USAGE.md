# Using CodeCarver

CodeCarver carves a repo down to only the code needed for a chosen entry set, and writes out a
minimal tree that still **builds**. This is the practical guide: the commands, the inputs, and how to
dial in how aggressively it carves.

> Build the CLI: `dotnet build CodeCarver.sln -c Release` (output: `src/CodeCarver.Cli/bin/Release/net8.0/`).
> The tool is `codecarver` — `codecarver.exe` on Windows, or `dotnet codecarver.dll` anywhere. It runs on
> Windows and Linux. The command examples below use `codecarver <command>`.

## Quick start

CodeCarver is driven by **one TOML config file**, so the command line stays tiny:

```
codecarver init                                          # write an annotated carve.toml template
#   edit carve.toml: set entryPoints + outputDirectory (and a buildLog if you have one)
codecarver carve <source-dir> --config carve.toml                  # carve
codecarver carve <source-dir> --config carve.toml --stage max      # run one named stage
codecarver carve <source-dir> --config carve.toml --why <symbol>   # explain why a symbol is kept/dropped
```

The config names the entry symbols to keep and where output goes; CodeCarver traces every dependency and
writes a **complete buildable project** plus a `codecarver/` folder of reports (see [Output](#output)). A
named entry point that resolves to nothing fails the run, and an independent check of the emitted tree runs
automatically (the `verify` line). For a full config that exercises every feature, see the worked
**`examples/multistage-firmware/`**. One stage of its output:

```
CodeCarver 1.0.142+3065c25b8 — carve of src
  roots   : main, Timer_ISR
  asm     : 1 symbol(s) referenced from .s startup auto-kept: Timer_ISR
  trace   : 7 function(s) from 1 trace(s) rooted; 7 resolved in-scope
  nodes   : 15/40 kept (38%), 25 carved
  files   : 6/7 kept, 1 dropped
  dropped : debug.c
  world   : closed-world (dead #ifdef branches dropped) — have 5 compile command(s); macros #defined in the tree and compiler built-ins not probed for the TU stay unknown
  stage   : safe  [source-contents=whole, header-contents=whole]
  emitted : 6 files -> out/safe/carved  [file-level (whole kept files)]
  verify  : OK — emitted code uses no function or variable defined only in a dropped file (7 file(s) checked)
  passthru: 5 non-code file(s) copied verbatim (541 B) — complete buildable project
  excluded: 3 non-input file(s) NOT copied (53 B) — VCS/scratch/editor (forceKeepFiles to keep)
  size    : 2,761 B -> 2,567 B  (7% smaller, saved 194 B)
  buckets : 6 required-to-build + 5 infrastructure kept, 1 dead-code + 3 auto-excluded file(s) removed
  report  : out/safe/codecarver/report.txt
  ...
```

## How aggressively it carves (per stage)

File-level (drop whole unneeded files) is always the sound floor. Two independent config toggles go deeper:

| Config key | What it does | Soundness |
|---|---|---|
| `carveSourceFileContents` | also remove unreached **functions and data tables** within kept C/C++ files | aggressive — **always build-verify** |
| `carveHeaderFileContents` | also strip unused `#define`s from **big** kept headers: those over `maxParseBytes`, or macro-dense headers of 1 MB or more. Ordinary headers are never rewritten. It runs after the infrastructure copy, and keeps every `#define` that assembly, linker scripts or other text files in the output use | aggressive — **always build-verify** |

Intra-file carving is where the big wins are (e.g. carving AES to encrypt-only drops the inverse S-box
table) — validated to compile on cJSON, zlib, Lua, SQLite, printf and tiny-AES-c, but treat it as "verify
by building it." Define named **`[stages.NAME]`** blocks to emit several aggressiveness tiers in one run
(`--stage` picks one; omit it to run them all, each into `outputDirectory/<stage>/`).

**File-level output is closed over what it emits.** A kept file is written whole, so functions in it that no
root reaches are still in the output, and whatever *they* call must be there too or the tree won't link.
CodeCarver roots that unreached code before emitting (per stage, to a fixpoint), so the carved tree links
without `--gc-sections`. When that adds files you see a line like
`closure : +2 file(s) kept because code this stage writes uses them (the output must link)`.

## The tightness ladder (optional inputs)

Every carve is sound with just `entryPoints`. Each extra input lets it carve **tighter**, never looser.

> **Implicit roots are always added.** Symbols the runtime/linker keep regardless of any call —
> `__attribute__((constructor))`/`((destructor))`/`((used))`/`((retain))` and `.init_array`-family
> section placement — are auto-discovered and kept (reported on an `implicit:` line), so a self-registering
> driver or an initcall table isn't silently dropped. The same holds when those attributes are hidden in a
> macro (`#define INITCALL(f) ... __attribute__((section(".initcall"))) ...`): a use of the macro roots what it
> decorates. Vector-table ISRs are kept the same way (via the file-scope address-taken edge). Symbols
> referenced from a standalone `.s`/`.S` startup file are rooted too (reported on an `asm:` line); when the
> build log or a build trace names assembly files, only those files count, so another target's startup file
> can't root its handlers. A symbol placed in a **custom section your linker script keeps** —
> `__attribute__((section(".init_calls")))` where the `.ld` has `KEEP(*(.init_calls*))` — is rooted from the
> tree's own linker script (reported on a `section:` line). Finally, a symbol called only from a generated
> table `#include`d with a **non-source extension** (`#include "GenTables.inc"`, `.def`, X-macro files) is kept
> too: those includes aren't parsed as C, but every symbol they name is retained and the file is copied into
> the output. This is the sound over-approximation; it never drops these.

## The CLI

The command line is tiny — everything else lives in the config:

| Command | What it does |
|---|---|
| `codecarver carve <source-dir> --config carve.toml` | carve (all stages). `--stage <name>` runs one; `--why <symbol>` explains one symbol |
| `codecarver init [path]` | write the annotated config template (default `carve.toml`; won't overwrite an existing file) |
| `codecarver scan-log <build-log>` | preview what a build log scrapes (compile commands, TUs, defines, includes) |
| `codecarver version` | the build's git-stamped version |
| `codecarver demo` | a narrated toy carve of an in-memory graph |
| `codecarver help` | usage and exit codes (also what you get with no arguments) |

**Exit codes:** `0` ok · `1` runtime failure (including unresolved entry points) · `2` usage or configuration
error (bad config, missing input file, unusable build log or trace, unprobeable compiler, unsafe output
directory) · `3` the emitted tree failed `verify` (it would not link).

`--stage` with a config that has no `[stages]`, or with `analysisOnly = true`, is an error.

**`--emit-from <analysis output>`** writes the tree an earlier `analysisOnly = true` run decided on, without
parsing again — on a large tree the parse is most of the run, so a dry run followed by the real emit no longer
costs two full runs. The analysis run saves `codecarver/emit-plan.json`; `--emit-from` refuses (exit 2) when
the CodeCarver version, a setting, a build log or trace it names, or any file under the source root (size or
time) changed since, and for a stage that carves inside files (that needs the parsed graph — use a file-level
stage). The source fingerprint is taken when the analysis run starts, so a file edited while it ran also makes
`--emit-from` refuse. The link check is not re-run: its result is carried over from the analysis run, which
checked exactly the files the emit writes (the kept files plus their in-tree include closure), so a failed
analysis still exits 3. The usual flow keeps one config: run it with `analysisOnly = true`, review, then run the
same command again with `--emit-from <its outputDirectory>`.

## The config file (`carve.toml`)

`init` writes this annotated; every option is a TOML key, and an unknown key is an error. Booleans accept
`true`/`false` (or `yes`/`no`). **Relative paths** (`outputDirectory`, `entryPointsFile`, build logs, traces, a
path-like `compiler`) resolve against the **config file's directory**, not the shell's; the globs
(`excludeDirectories`, `forceKeepFiles`) are relative to the source directory. Build, run and stage names may
use only letters, digits, `_` and `-`.

**Top level**
- `outputDirectory` — where output goes (see [Output](#output)). Must be **outside** the source tree.
- `analysisOnly = true` — decide only (plan + reports), don't emit a tree. A fast dry run.

**`[common]`** — the whole carve:

| Key | Effect |
|---|---|
| `entryPoints = ["main","Reset_Handler"]` | the symbols to keep. A name that resolves to nothing **fails** the run (exit 1), with "did you mean" suggestions; `--why` still works so you can investigate. Qualify a name by file to pick one of several: `"app/a/main.c:main"` (the path matches the end of the file's path). Or `entryPointsFile = "roots.txt"` (one per line, `#` comments) for a long list. |
| `languages = ["c","cpp"]` | source languages: `c`, `cpp` (also `c++`, `cxx`), `csharp` (also `cs`, `c#`). A mixed `["c","cpp"]` tree is carved as **one graph** (reachability crosses the C/C++ boundary — a C root reaching an `extern "C"` C++ callee is kept). `csharp` is a separate graph — carve it on its own. Assembly is auto-scanned for roots; `.cmm` is handled via run traces. |
| `excludeDirectories = ["tests","boards/old"]` | directories to drop (tests, other board/arch variants). Each entry matches whole path segments of the path **relative to the carve root**, so `tests` does not match `mytests/`, and a carve root that itself sits under a `tests/` directory is unaffected. |
| `forceKeepFiles = ["prebuilt/*.a"]` | globs to **always** keep, even under an excluded directory, auto-excluded, or dropped by the carve. A forced code file is a root, so its callees and includes come with it; a forced `.cmm` seeds the `.cmm` closure. Each glob prints how many files it matched (`force   : forceKeepFiles '...' -> N file(s) kept`), and a glob that matches nothing is a warning. A bare pattern like `*.inc` searches all subdirectories; a glob with `..` or an absolute path is refused (exit 2). |
| `carveSourceFileContents`, `carveHeaderFileContents` | the two aggressiveness toggles (above), used when there are no `[stages]` |

**`[builds.NAME]`** — how the real compiler sees the code (define several build *steps*; selected builds **union**):

| Key | Effect |
|---|---|
| `buildLogs = ["make-n.log","build.console.txt"]` | scrape real per-file `-D`/`-I` from a `make -n` log, console capture, or `compile_commands.json`. **The** way to pin `#ifdef`s. A log with no recognised compile command, or none naming a file under the carve root, is exit 2. |
| `compiler = "arm-none-eabi-gcc"` | probe the compiler (`-dM -E`) for its built-in macros, and with `buildLogs` **run** every GCC/Clang compile command again with `-E` so the build's own preprocessor decides each `#if` (see **Exact** below; uses most cores). A compiler that can't be probed is exit 2. |
| `compilerNames = ["armcc","iccarm"]` | extra compiler driver names to recognise in a **text** build log (gcc/clang/cl and cross drivers are known) |
| `defines = ["CHIP=F4"]` | manual defines, applied to every file. They do **not** by themselves make the world closed (see below). |
| `buildTraceFiles = ["build.trace"]` | files opened while **building** (capture with [`tools/capture`](../tools/capture/README.md)) |

**`[runs.NAME]`** — what a real execution touched (optional; tightens + audits):

| Key | Effect |
|---|---|
| `runTraceFiles = ["run.trace"]` | files opened while **running/flashing** — catches the loader/`.cmm`/data layer |
| `runTraceLogs = ["run.log"]` | functions that actually ran → roots (format below) |
| `dropUnobservedCmm = true` | opt in to dropping `.cmm` scripts the run neither opened nor reaches by `DO`/`GOSUB` (see Languages). Needed on **every** selected run that has a file trace. |

**`[stages.NAME]`** — aggressiveness tiers; each sets the two carve toggles. `--stage` picks one; omit to run all.
**`[use]`** — `builds = [...]` / `runs = [...]` to select a subset (default: all defined).
**`[advanced]`** — rarely needed:

| Key | Default | Effect |
|---|---|---|
| `maxParseBytes` | `20000000` (bytes) | files larger than this are not parsed; they are kept whole via `#include` closure |
| `parseTimeout` | `20` (seconds) | per-file parse budget; a file that exceeds it is kept whole. `0` disables the budget |
| `maxSymbolsPerFile` | `50000` | a file with more symbols than this is kept whole instead of exploding the graph |
| `pathMap = [{ from = "/build/agent/repo", to = "." }]` | none | rewrites a path prefix captured elsewhere (CI agent, other drive, WSL vs Windows) onto the carve root. `to` is relative to the carve root. Applies to trace paths and to compile commands (directory, file, `-I`, response files) |
| `allowUnmatchedTraces = true` | `false` | a file trace with no path under the carve root becomes a warning instead of exit 2 |
| `pruneGarbage = false` | `true` | keep every non-input too (VCS metadata, IDE scratch, logs, editor backups, make `.d` dependency files) |

Integer keys are range-checked: a negative or oversized value is a config error (exit 2).

Three things are **automatic**: the open/closed `#ifdef` world is derived (below); the emitted-tree `verify`
check runs on every carve; and non-inputs (VCS/scratch/editor junk, logs, make `.d` dependency files — a `.d`
is judged by its content, so D source and DTrace scripts are kept) are **auto-excluded** (`forceKeepFiles`
un-drops one, `pruneGarbage = false` all).

### A minimal config

A single-stage carve with a build log:

```toml
outputDirectory = "D:/carved/myimage"

[common]
entryPoints = ["main", "Reset_Handler"]
languages   = ["c"]
excludeDirectories = ["tests"]

[builds.main]
buildLogs = ["build.log", "build.console.txt"]   # comments and arrays are fine; list several logs
```

A referenced **input** file that's missing (a build log/trace) **fails fast** (exit 2) with the full list, so a
typo'd path can't silently carve with less config than intended.

## Output

```
<outputDirectory>/[<stage>/]
  carved/                  the complete buildable project
  codecarver/
    report.txt             human-readable: roots, kept/dropped files by bucket, sizes, observed-file tags
    manifest.json          the same decision, structured: keptFiles, includeClosureFiles (written only because kept
                           code #includes them), infrastructureFiles, droppedFiles, droppedCmm, removedGarbageFiles,
                           stats, observed files — every emitted file is in exactly one kept list
    resolved-config.toml   the configuration actually used: selected builds/runs merged, paths resolved
    decisions.txt          per-symbol KEPT/CARVED ledger with the chain back to a root
    repro.graph.json       the dependency graph, anonymized (opaque tokens) — safe to share
    verify.txt             the emitted-tree link check (see Verify)
    warnings.txt           every repeated warning in full (the console shows the first 20 per category); names paths
    summary.txt / .json    numbers only — no path, file name or symbol
  .codecarver-output       marker: this directory was written by CodeCarver
```

With `analysisOnly = true` there is no `carved/` and no `resolved-config.toml`; the other reports are written
under `<outputDirectory>/codecarver/`, and `verify` checks the kept files as they would be emitted.

`summary.txt` / `summary.json` hold only counts, booleans and fixed category names (world, graph, roots by
kind, traces, `.cmm`, per-stage sizes and verify counts, warnings by category, exit code). They are what you
send back when the source must not leave the machine. Repeated warnings (unresolved `#include`s, front-end notes) are capped at 20 per category on the console;
the full list is in `warnings.txt` and the count per category in the summary. `decisions.txt` is replaced by a short note above
500,000 graph nodes (use `--why` or `manifest.json` then).

**Output safety.** An existing, non-empty `carved/` or `codecarver/` is replaced only if its parent carries the
`.codecarver-output` marker. Otherwise the run stops before doing any work (exit 2) rather than delete a
directory CodeCarver didn't create. Each stage's `carved/` tree is built in a private staging directory and
moved into place only when the stage completes.

### Output is a complete, buildable project (keep-by-default)

The carved output (`outputDirectory/[<stage>/]carved`) is not just the carved C — it's a **whole project you
can build**. After emitting the reachable code and its `#include` closure, CodeCarver copies **every other
file in the tree verbatim**: Makefiles / CMake, linker scripts and scatter/`.cmd` files, startup assembly,
device trees, register and data tables, TRACE32 `.cmm`, prebuilt `.a`/`.o`, board configs, dot-files such as
`.config` — anything that isn't a translation unit the carve modelled. The rule is **evidence-based removal
only**: the files left out are (1) the code already emitted, (2) code files the carve **proved unreachable**
(dead translation units / unreferenced headers), (3) `.cmm` scripts dropped under `dropUnobservedCmm`, and
(4) **auto-excluded non-inputs** — files that cannot be a build/run input *by universal convention* (VCS
metadata, compiler/IDE scratch, dep/coverage artifacts, editor/OS junk, logs/temp). `.git`, `.svn`, `.hg` and
`.bzr` directories are never walked. Everything else is kept, because a file the tool didn't model is a file
it can't prove you don't need to build.

Auto-exclusion is deliberately **conservative**: ambiguous binaries that *could* be vendored prebuilts the
build links (`.o`, `.a`, `.so`, `.lib`, `bin/`, `build/`) are **not** auto-dropped — they're kept and flagged
in the report's "look like build outputs" section so you can `excludeDirectories` them if they're generated.
Restore any wrongly-excluded file with `forceKeepFiles`.

Because passthrough files are copied byte-for-byte, the headline **size reduction reflects only the code
carve** (dead code removed) — the untouched infrastructure is delta-neutral, and auto-excluded files are
reported separately (not folded into the headline %). The `report.txt` shows what landed in each bucket.

## Traces: keep what a real run actually touched

Two optional inputs let an observed build or run tighten, speed up and audit the carve. A function trace and a
run's file trace **add** to the static carve; a build's file trace limits what is read and says what the build
compiled.

- **Function trace** (`[runs.smoke] runTraceLogs = ["run.log", ...]`): the functions a run executed become
  roots, covering dynamic dispatch (function pointers, vtables) static analysis can't resolve. One format — a
  function name per line, optionally followed by `file:line`. A leading hex address (`0x0800a1c4`), timestamp
  (`12.345`) or `[tag]` is skipped, and a C++ qualified name (`ns::Class::method`) uses its last component.
  Lines with no readable name are counted in a warning. Write a small converter from your tracer's output if
  it differs.
- **File-access trace** (`[builds.main] buildTraceFiles` / `[runs.smoke] runTraceFiles`): the **files the OS
  actually opened**. Capture them with the scripts in [`tools/capture`](../tools/capture/README.md) (ProcMon on
  Windows, strace on Linux); they keep only the build's own processes, make Linux paths absolute, and reduce
  the capture to one path per line. A raw ProcMon CSV or strace log is also accepted, but consolidating first
  is strongly recommended.
  - **build trace** — captured *while building* → the exact compile/link inputs.
  - **run trace** — captured *while flashing/running* → the loader/orchestration layer (TRACE32 `.cmm`
    scripts, the binaries and data they load) that a function trace can't see and that static analysis can't
    resolve (computed `&var` paths).

  A **build** trace says what the build *compiled*, not what the program needs:
  - **Files the build never opened are not read.** When *every* selected build has a build trace, a code file
    none of them opened can't be part of the build. It's dropped without being read or parsed, which on a big
    tree is most of the carve time (and most of the round trips on a network drive). The `build :` line says how
    many; `parse.filesNotBuiltSkipped` counts them. `[advanced] skipFilesNotBuilt = false` turns this off.
  - **Give the build log too: it checks the trace.** The log and the trace describe the same build, so every
    in-root source the log compiled must appear in the trace as opened. If any don't, the trace is incomplete
    (strace without `-f`, a build in a container or a build server started before tracing, a compiler cache, an
    incremental build, or paths that need `pathMap`): the run says so (`build : WARNING`), counts them in
    `build.traceMissedCompiledFiles`, and reads every file instead of skipping. With or without a log, a traced build
    that links an object (`helper.o`) whose source in the tree it never opened was incremental, and is treated the
    same way (`build.traceLinkedObjectsWithUnopenedSource`). When the trace checks out, kept code
    that uses a name defined **only** in files the build never compiled is a **note**, not a failure
    (`<stage>.verify.notBuiltDefinitions`): the real build linked without those files, so the name comes from
    somewhere else (another build or a prebuilt library, an alias, a macro). With a trace but no log the check
    can't run, and such a name fails verify with cause `definedInFileNotBuilt`. The exception is a use inside an
    `#if` branch nothing decides (no log means no `-D`), which is a note: that is the branch the build didn't take.
  - **Compiled files nothing reachable calls are dropped, as placeholders.** A build compiles files the image
    never calls into. Static reachability drops them. Build files that list sources (a Makefile, a `.vcxproj`)
    still name them, so each is written as a **placeholder**: a few lines that define nothing (an empty file is
    not valid ISO C). The unchanged build finds every file it lists, and the binary loses the code (unless the
    linker already discarded it, via `--gc-sections` or a static library). "Compiled" means named by a compile
    command in `buildLogs` or opened by a build trace, so a build log alone gets placeholders too. Unless a
    complete build trace says exactly what was compiled, a source the tree's build files name (by file name, or by
    stem as in `$(addsuffix .o,q1 q2)`) counts as compiled too, even with no build inputs at all. The count is
    `<stage>.placeholderFiles`, and `manifest.json` lists them under `placeholderFiles`. With
    `[advanced] placeholderFiles = false` they are removed outright (edit the build's file list to match).

  A code file a **run** opened roots its **file** only: the file is kept (and, file-level, emitted whole with its
  closure), but at a `carveSourceFileContents` stage its functions that no root reaches are still removed.
  Every observed file is kept (never auto-excluded); the report tags observed infrastructure `[observed]` and
  flags **kept-but-unobserved** files as drop candidates. Only paths under the carve root that exist count;
  source files read *outside* the root are reported as a possible missing dependency.

  A trace that can't be read is exit 2. A trace whose paths include **none** under the carve root (captured in
  another checkout location) is exit 2, with the prefix it appears to use and the line to add:
  `[advanced] pathMap = [{ from = "/build/agent/repo", to = "." }]`. Set `allowUnmatchedTraces = true` to make
  that a warning.

  ```toml
  # in carve.toml
  [builds.main]
  buildTraceFiles = ["build.trace"]
  [runs.smoke]
  runTraceFiles   = ["run.trace"]
  ```

> Over-capturing (un-exercised paths, extra tools) only ever keeps more, and the report shows you exactly what
> each trace touched. A **clean, full** build is required for a build trace: an incremental build opens almost
> nothing, and every file it didn't open would be skipped. With the build log given too, that is caught up front
> (the trace misses files the log compiled, so nothing is skipped); without it, verify fails with `definedInFileNotBuilt`.
> Give every build's trace: a selected build without one turns the skip off, since its files aren't covered.

## Languages

- **C / C++** (`languages = ["c","cpp"]`): calls, macros, globals, `#include` closure, `#ifdef` resolution,
  file-level **and** `carveSourceFileContents` intra-file carving. `#include "x.h"` resolves beside the
  including file first, then through the translation unit's own `-I` order (a basename match is only a
  fallback). A file-scope `static` function is not reached from other translation units. A parameter or local
  variable named like a function does not keep that function. Definitions the parser can't see directly are
  still recognised: pre-C99 implicit-`int` and K&R definitions (`helper(a, b) int a; { ... }`, in C files), a
  function head split across `#if`/`#else`/`#endif` with one shared body (every branch's name is defined), and
  a symbol a macro use defines (`FW_DECLARE(uart, ...)` defining `uart_desc` via `n##_desc`, including through
  wrapper macros and head macros like `DEFINE_TASK(blink) { ... }`), a name wrapped in a macro
  (`int EXPORT(f)(void)` with `#define EXPORT(n) n`), digraphs (`<% %>`), and code after junk inside `#if 0`.
  Aliases are definitions that keep their target: `__attribute__((alias("impl")))` (also through a macro whose
  body is `alias(#arg)`), `#pragma weak a = b`, `_Pragma("weak a = b")` and asm `.set`/`.equ`. A call written
  `name(...)` where a function-like macro `name` is visible goes through the macro, not to a same-named function.
  See [`examples/eldritch`](../examples/eldritch/README.md) for all of these in one program.
- **C#** (`languages = ["csharp"]`): always **file-level** — a `.cs` file is kept or dropped whole;
  `carveSourceFileContents` is ignored with a note. The file set is closed over identifier and type
  references: a kept file that mentions a name keeps every type and method of that name. Files with top-level
  statements are entry points. Reflection and string-based dependency injection are invisible to it — list
  such files in `forceKeepFiles`. `verify` does not run for C#.
- **TRACE32 `.cmm`**: file-level, handled via run traces (not `languages`); never carved internally. With
  **no** run trace, or a trace that opened no `.cmm`, every script is kept. With a run trace, the observed
  scripts **seed a `DO`/`GOSUB` closure** (those scripts plus every script they `DO`, transitively). A `DO`
  target resolves as a path first (beside the calling script, then from the carve root, including `../` and
  quoted paths); otherwise it binds to **every** script with that basename. By default the scripts outside the
  closure are still **kept** — one run is one scenario — and the `cmm:` line reports how many the trace
  accounts for. Set `[runs.X] dropUnobservedCmm = true` to drop them. Even then, nothing is dropped while a
  kept script has a dynamic `DO &var` (it could run anything) or could not be read; the run says why. Dropped
  scripts are listed in `manifest.json` under `droppedCmm`.

## `#ifdef` resolution: open vs. closed world

CodeCarver **derives this automatically**; the `world:` line says which and why.

- **Open-world** (no build log and no compiler): a macro that's neither supplied nor defined in the file is
  *unknown*, so both branches of its `#ifdef` are kept. Sound — it only drops what it's certain about
  (`#if 0`, definite conditions, branches after a definitely-taken one). Manual `defines` are applied as
  known-defined, but an absent macro stays unknown.
- **Closed-world** (a `buildLogs` with at least one parsed compile command, or a probed `compiler`): an absent
  macro is treated as undefined and its branch is **dropped**. Exactly what counts as known:
  - the `-D`/`-U` flags of each translation unit's own compile command (including response files and
    `-Wp,-D`). A macro defined in some of a file's commands but not others is unknown, and so is a name that
    a forced include (`-include`/`-imacros`) `#define`s;
  - the probed compiler's built-in macros. They are probed with the build's target flags only when every
    compile command shares them; otherwise flag-dependent built-ins (`__ARM_*`, `__OPTIMIZE__`,
    `__STDC_VERSION__`, …) are unknown, and they are always unknown in a C++ carve;
  - **nothing else.** A macro `#define`d or `#undef`d anywhere the build reads is unknown unless the build defines
    it (include guards excepted). "Anywhere the build reads" is the tree (excluded directories too), the headers
    **outside** the carve root the build uses (an SDK, generated config: every file a build trace opened, or
    without a trace every header under the log's `-I`/`-isystem`/`-iquote`/`-idirafter` dirs and every quoted
    `#include` that leaves the tree), and forced includes (searched like `#include "..."`, then `-I`). A reserved
    name (`__x`, `_X`) the probe didn't report is unknown, and so are the C library's macros (`INT_MAX`,
    `SIZE_MAX`, `EOF`, `bool`, …) and non-underscore built-ins (`linux`, `unix`, `i386`). A `#define` under an
    uncertain condition makes the name unknown.

  When a header the build reads **can't be seen** (a log's include dir that doesn't exist here, a quoted
  `#include` that resolves to nothing, a computed `#include MACRO`, or a header a trace from another machine
  opened), every name an `#if` tests that nothing visible defines and no `-D` names is unknown: the `config :`
  line says how many and why. A build trace, or `pathMap` for the include dirs, makes it exact.

  A translation unit that no compile command covers, or whose command was incomplete (unreadable response
  file or forced include), is resolved open-world; the `build:` lines report how many.
- **Exact** (a `buildLogs` **and** a `compiler`): the build's own preprocessor decides. **The carve runs the build's
  compile commands**: each is run again in its directory with `-E -dD` in place of `-c`, `-o`, dependency-file and
  `-save-temps` flags, and its line markers say which lines of the source and of every header came through. A
  conditional block is live when any of its lines came through in any compile, and dead otherwise, so macros a config
  header defines, `#undef`s and include order all count.
  - **What runs.** Only GCC/Clang-style drivers (`gcc`, `g++`, `cc`, `c++`, `clang`, `clang++`, cross-prefixed or
    versioned like `arm-none-eabi-gcc`, `clang-17`): one named like the `compiler` runs as it, another only from the
    full path the log gives. MSVC-style and other tools never run, nor does a command carrying `-wrapper`, `-specs`,
    `-fplugin`, `-fpass-plugin`, `-iplugindir`, `-load` or `-B` (they load or run other code). A command whose
    response file was unreadable (or whose directory had to be guessed), or that names an include directory, forced
    include or source missing here (gcc would skip a missing `-I` and find the same name elsewhere), doesn't run
    either. `pathMap` rewrites the paths in the arguments too. A long command goes through a temporary response file.
  - **What is decided.** Headers only when every command ran **and** every translation unit the build compiles (all
    the tree's, or with a build trace the ones it opened) has a command that ran: a compile the carve didn't see
    could take a block the others skipped. Short of that, a source file whose own commands all ran and that no other
    file includes. Never decided dead: a block inside an unclosed parenthesis (the arguments of a multi-line macro
    call print blank, the whole expansion on the call's first line) or one holding an `#include`. Files with `#line`
    directives, and everything not decided, keep the rules above; so does verify's view of a source file whose
    content carving removed lines.
  - **Cost.** One preprocess per command, on all cores but one, up to 5 minutes each. The `config :` line and
    `world.preprocessed.commands` / `.failed` / `.failedMissingPaths` / `.refused` / `.filesDecided` say how it went.

**Resolution affects reachability only.** It decides which calls count; the emitted text still contains both
branches of every `#if`. So feed a build log (or set a build's `compiler`) to carve tighter; give neither to
stay safe.

## Getting a build log

Most builds don't emit a `compile_commands.json`. Any verbose build log works — even a dry run:

```
make -n > build.log                  # prints the compiler command lines without building
codecarver scan-log build.log        # shows the compile commands, units, defines, includes it found
# then in carve.toml:  [builds.main] buildLogs = ["build.log"]
codecarver carve src/ --config carve.toml
```

The scraper recognises gcc/clang/cc/cl and cross drivers (`arm-none-eabi-gcc`, …), behind `VAR=x`
assignments, a ninja `[n/m]` prefix or wrappers such as `ccache`/`distcc`. A line with several commands
joined by `&&`, `||`, `;` or `|` is split so each compile gets only its own flags, and a leading `cd dir`
carries forward. `@response` files are read (relative to the command's directory). GNU `-D/-U/-I` are parsed,
and MSVC `/D /U /I` for `cl`-like drivers. For a vendor compiler in a text log, name it in
`[builds.X] compilerNames`; a `compile_commands.json` must be a JSON array of
`{directory, file, command|arguments}`. (`scan-log` previews with the built-in driver list only.)

Messy logs are expected:
- **Compiles behind a shell.** `sh -c '...'` is read inside the quotes, and a launcher such as
  `/bin/sh ../libtool --mode=compile gcc ...` is read from the compiler on.
- **Recursive `make -w`.** `Entering`/`Leaving directory` lines set the directory for relative paths. Under
  `make -j`, sibling sub-makes run at once and their lines interleave, so the directory entered last is only a
  guess. Every directory still open is tried, and the file is kept where it exists. When it exists in more than
  one, each copy gets the command but is resolved open-world: both `#ifdef` branches are kept, since the log
  can't say which copy got which flags.
- **Gaps in the log** cost precision, never soundness:
  - a quiet `  CC foo.o` line;
  - a lost `@response` file;
  - a source reached through a symlink outside the tree;
  - a configure probe's `conftest.c`.
  The file is resolved open-world. A compiled file the tree no longer has doesn't count against the build trace.
- **Generated sources** live in the build directory, outside the tree, so their calls are read where they come
  from:
  - every call in a C source template (`table.c.in`, `config.h.in`, `.tmpl`, `.j2`);
  - calls inside the string literals and here-documents (quoted or not) of the tree's build scripts and generator
    scripts: shell, Python, Perl, awk, Ruby, Lua, Tcl, m4, and any file without an extension that starts with `#!`.
  Those functions are kept, and verify checks them (`roots.generatedCode`).

[`examples/hellbuild`](../examples/hellbuild/README.md) is all of this in one small build.

## Verify

Every C/C++ carve runs `verify` on the **emitted** tree. It is an independent, tokenizer-only check that does
not use the carve's graph: it fails the run (**exit 3**) when emitted code uses a function or variable that only a
**dropped** file defines — the carved tree would not link. Such a use on a line that is dead under the
`#ifdef` world is reported as a note, not a failure (it is correct if the world is). Files over
`maxParseBytes` are not checked and are counted. Details go to `codecarver/verify.txt`.

What counts as a use and a definition follows the compiler and linker: a call `name(...)` where a function-like
macro `name` is visible (the file or a header it includes) is a macro expansion, not a use; `(name)(...)` is a use.
Aliases (`alias` attributes, alias macros, `#pragma weak a = b`, `_Pragma`, asm `.set`) define the alias and use the
target; assembly files define what they export (`.globl`, `.weak`, `PUBLIC`, `name PROC`). A name declared weak
(`__attribute__((weak))` on a declaration, `#pragma weak name`) links as null when undefined, so a failure on it
whose only definitions are in files the build never compiled is a note. Names the linker or loader binds that no
call spells out count the same way, both for what the carve keeps and for verify: an `ifunc` is defined by its
resolver; an asm label (`__asm__("sym")`) or `#pragma redefine_extname` renames the symbol; `__wrap_X` stands for
`X` when the build links with `--wrap=X`; a C99 `inline` body in a header is emitted by the file that declares it `extern` (a GNU `extern inline` body
emits nothing); `-D` macros that rename a definition (`-Dold=new`, `'-DNAME(n)=n##_impl'`; a name renamed differently by different
compiles is every one of them); symbols the link
names (`--defsym`'s right side, `--undefined`, `--require-defined`, `--entry`, the driver's `-u` and `-e`, each
also through `-Wl,` or `-Xlinker`, and a linker script's `ENTRY`/`EXTERN`/`PROVIDE`); and string literals in a file that looks symbols up by name (`dlsym`, `GetProcAddress`). The `-D`
macros and link flags come from the build logs and from the tree's own build scripts (makefiles, shell scripts,
CMake files, linker scripts), so a carve without a build log still sees them. At a `carveSourceFileContents` stage, a
function **pruned from a kept file** that emitted code still uses fails too, with cause `prunedFromKeptFile`.
Variables are checked where they are defined in a `.c`/`.cpp` file; a definition inside a header is not.

**Before** it reports, each stage closes over what verify finds: a definition the emitted code uses but the plan
left out (the graph missed that use) is kept, and the stage is emitted again from the larger plan, until nothing is
missing (at most 8 rounds). Each name it kept is counted by cause as `<stage>.verify.keptByCheck.<cause>` (an
unrecognised definition also by shape, `...definitionNotRecognized.<shape>`; a definition cut from a kept file also by
what the graph made of the use, `...prunedFromKeptFile.<cause>`, and where the use sits, `...prunedFromKeptFile.use.<shape>`) and listed as a `KEPT` line in
`verify.txt`, so the gap stays visible while the carved tree links; a later stage of the same kind starts from those
and reports them too. Every file defining the name is kept, except a header (it compiles only where it is included)
and a file the build never compiled; a name defined only there stays a failure. The infrastructure is copied once,
after the plan settles.
Whatever is still missing after that fails as below.

Each failure also gets a **cause**, counted in `summary.txt` as `verify.failed.<cause>` (numbers only, safe to
send back) and named per failure in `verify.txt`: `definitionNotRecognized` (the dropped file's definition never
became part of the graph: an unusual definition shape), `definitionFileLocal` (only a file-scope `static`),
`useNotModelled` / `useInHeaderNotModelled` (the definition is known but the use was not captured),
`useInUnparsedFile` (the use is in a file kept whole without parsing),
`useInUnreachedCode` (only code the carve did not reach uses it), `definedInFileNotBuilt` (only in files a build
trace never opened, with no build log to check the trace), `prunedFromKeptFile`, or `other`.

An **entry point** that isn't found fails the run (exit 1) and says why: it is defined only in a file the traced
build never compiled (not part of this build), it sits in an `#if` branch this build's configuration turns off,
the parser didn't recognise its definition (see `--why`), or it isn't defined anywhere in the tree.

A `definitionNotRecognized` failure is also described by **shape**, counted as
`verify.failed.definitionNotRecognized.<shape>` (one failure can have several) and listed per failure in `verify.txt`:

| Shape | Meaning |
|---|---|
| `noReturnType` | nothing before the name (old-style implicit `int`, or a macro-headed body) |
| `extraWordBeforeName` | an unknown word between the type and the name (`int CALLCONV f(...)`) |
| `macroCallBeforeName` / `macroCallInParameters` | a macro call in the head (`FUNC(void, X) f(...)`) |
| `functionPointerParameter` | a parameter like `int (*cb)(void)` |
| `kAndRDeclarations` / `bareKAndRHead` | K&R parameter declarations / bare undeclared parameters |
| `wordsAfterParameters` | words between `)` and `{` (`reentrant`, attribute macros) |
| `directiveInHead` | a `#if`, `#pragma` or other directive line inside the head |
| `noBodyAfterHead` / `unbalancedParameters` / `nameNotOnLine` | the head as written does not reach a `{` |
| `nameIsAFunctionLikeMacro` | the tree also `#define`s the name as a function-like macro |
| `fileNotParsed` / `inDeadIfdefBranch` | the file was kept whole unparsed / the line is dead under the `#ifdef` world |
| `nameReadAsType`, `insideParseError`, `insideABody`, `parsedAsDeclaration`, `parsedAsDefinitionButRejected` | what the parser made of the name |
| `parsedAs_<node>` | otherwise, the grammar node the name ended up in (a tree-sitter node name) |

These are fixed words, so the counts are as safe to send back as the rest of `summary.txt`.

When a file's parse has errors, the carve also runs `verify`'s own token scanner over it and defines every function
the scanner sees that the parse missed (error recovery can swallow an ordinary function near a construct the grammar
cannot read). Such a function is kept with its file rather than carved on its own; `parse.definitionsRecoveredByScan`
in `summary.txt` counts them. A name the tree also `#define`s as a function-like macro is not recovered this way.

`verify` is a link check, not a build: it cannot see a missing type, macro or header. Build what you carved:

```
codecarver carve src/ --config carve.toml   # e.g. with a [stages.prune] carveSourceFileContents = true
cc -c out/prune/carved/*.c -Iout/prune/carved/   # or your real build, pointed at the carved tree
```

## First run on a large repo

Recommended workflow the first time you point it at a big, unfamiliar tree:

```
# in carve.toml: analysisOnly = true for the dry run; drop it (and add [stages]) for the real emit.
codecarver carve <dir> --config carve.toml   # 1. analysisOnly: stats, dropped files, warnings, verify (no emit)
codecarver carve <dir> --config carve.toml --emit-from <outputDirectory>   # 2. emit that plan, no re-parse (file-level)
#   or drop analysisOnly (and add [stages]) for a full carve -> outputDirectory/[<stage>/]carved + codecarver/
cc -c out/carved/*.c -Iout/carved/           # 3. build the output (the only real guarantee)
```

On a large tree it prints a **live ETA** while parsing (the dominant phase) — e.g.
`parsing : 22% (4,376/20,075 files, 1.2 MB/s) -- ETA ~6m 41s`. It is measured on this run: the rate over the
last minute and over the last 15 s, from both bytes and files, showing the smaller estimate, so a slow opening
stretch of big files stops counting once it is past. Until 15 s and 2% of the work have passed it shows
`ETA estimating...`. Printed to stderr, so it never pollutes stdout. Reachability and emit are a short tail after
parsing; emit scales with how much is *kept*.

It's built to survive a messy real tree: a file it can't read or can't parse is **skipped with a
`warn:` line and kept whole** (never crashes the whole run), and files over `maxParseBytes`, over the
`parseTimeout` budget or over `maxSymbolsPerFile` are kept whole too (reported on `big:`, `budget:` and
`warn:` lines).
The source is read byte-transparently: non-UTF-8 text is preserved, a UTF-8 BOM is kept, and UTF-16 files are
copied whole.

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
codecarver carve <dir> --config carve.toml --why sym   # why a symbol was kept (its chain to a root) or that it was carved
```

Per-symbol keep/drop detail for the whole tree is in `codecarver/decisions.txt`; file-level detail in
`report.txt` / `manifest.json`. If the tool itself crashes (an unhandled exception), it writes a source-free
diagnostic `.zip` and prints its path — see [`SUPPORT.md`](SUPPORT.md).

## What CodeCarver does not do

- It doesn't *run* your program to carve it (no runtime, no hardware in the loop).
- It errs toward keeping code when a reference is ambiguous (soundness over minimality), so output can carry
  some unused code — but it should always build. Known looser spots: a C++ constructor is kept whenever its
  class name appears in any header or kept file (outside the class's own body), even if no object is ever
  built; a virtual call keeps every override of that name.
- Linker scripts are read for `KEEP()` only in GNU ld form (`.ld`, `.lds`, `.ldscript`). IAR `.icf`, ARM
  `.sct`, TI `.cmd` and preprocessed `.ld.S` scripts are copied but not read — name such sections' symbols in
  `entryPoints`.
- Correctness is bounded by input fidelity: wrong `defines` or a wrong `buildLogs` give a wrong carve.
  Feed it the real build's config.
