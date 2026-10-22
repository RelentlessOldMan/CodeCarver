# Testing strategy

CodeCarver must produce **working code**, so testing it means proving — automatically, on real
code — that a carve still builds and never drops something the image needs. The product itself needs
no compiler; **only this regression suite does**, and it uses *pinned* toolchains (never whatever is
on the dev box) so results are reproducible on any machine and in CI.

## Two tiers

### Tier 1 — fast suite (no toolchain, always runs)
The reachability engine, graph model, root/edge model, front-ends, preprocessor, emitters, the CLI driven
in-process, and the hand-authored `Scenarios/` library — everything outside the `BuildVerify` namespace. No
compiler needed; a full run takes a couple of minutes. This is the inner loop and what CI runs, on Windows
and Ubuntu. CI also carves every example under `examples/` with the shipped CLI (each must exit 0, i.e.
`verify` passes) and runs `shellcheck` on the shell scripts.

### Tier 2 — real repos + real toolchains (fetched, runs locally)
The honest test: carve real code and let the actual toolchain judge it — the `BuildVerify` tests. They need
the fetched toolchains and corpus, which CI does not have, so they run locally with `./check.ps1 -Big`. A
missing toolchain or corpus repo skips (with a notice) rather than failing.

## The strongest test: build the carved output

The must-build guarantee, exercised end to end on real code:

1. **Carve** a real repo to a chosen root set.
2. **Compile + link the carved tree** with the pinned toolchain → assert it builds. *(the guarantee)*
3. **Soundness:** the carved image's symbols ⊆ the original image's symbols (we never added), and in
   dead-code mode the two linker maps match modulo symbols the linker itself also stripped.
4. **Behaviour (bonus):** if the repo has a runnable check, run it.

Complementary oracle (no carve): build the *original* with `-ffunction-sections -fdata-sections
-Wl,--gc-sections -Wl,--print-gc-sections -Wl,-Map=img.map`; the map is ground truth for what's in the
image. CodeCarver's predicted kept-set must be a **superset** (soundness = pass/fail); the excess is
our precision gap (measured, not gated — over-keeping is safe).

## Pinned toolchains (`.toolchains/`, gitignored)

Fetched by `fetch-toolchains.ps1` (Windows) from pinned URLs/versions:

| Toolchain | Covers | Why this one |
|---|---|---|
| **w64devkit gcc/g++** | host C/C++: compile and link carved output | one self-contained Windows download |
| **arm-none-eabi-gcc** (xPack) | embedded ELF: vector tables, `.init_array`, `--gc-sections`, maps | runs on Windows, cross-compiles to the target's actual world — no Linux box needed |
| **.NET SDK** (already present) | building and running carved C# (`examples/csharp-app`) | already required to build CodeCarver |

LLVM/Clang and MinGW-w64 are **not** fetched. The Linux oracles (`wsl-*-oracle.sh`) use the gcc/g++ installed
in WSL. Pinned by version + URL so every run/box gets identical tools (no hash check yet). Toolchains are
fetched, never committed.

## Corpus (`.corpus/`, gitignored)

Varied real repos pinned to exact commits, fetched by `fetch-corpus.ps1`, across the three tiers
(embedded C first, no skimping):

- **Embedded / systems C** — `#ifdef`-heavy drivers, startup/vector-table code, function-pointer
  tables (e.g. an RTOS, a bootloader, a small libc). Closest to the real target; exercises the
  hardest cases.
- **C++** — templates, classes, virtual dispatch (stresses vtable/override conservative edges).
- **Polyglot** — a spread including a C# and a `.cmm` sample (covers the generic claim).

Two uses per repo: **buildable** ones feed the build-the-carved-output test (Tier 2); ones that won't
build on the pinned toolchains still feed **parse-robustness** (the front-end must extract a graph
without choking) — the scale/mess test.

## `#ifdef` resolution tests

There is no preprocessed-input (`.i`) mode: CodeCarver resolves `#ifdef`s itself, from the build log's
per-file `-D` flags and an optional compiler probe. Its tests are compiler-free unit tests (`Preprocess/`,
the build-log scraper tests) plus the carve-and-compile checks above, which catch a wrongly-taken branch as a
build failure.

## Synthetic corpora and oracles

`tools/codespawner/codespawner.exe` generates compilable synthetic firmware trees with a manifest of the
true reachable set (see `tools/codespawner/manifest-schema.md`). `carver-groundtruth-oracle.ps1` carves one
and asserts kept ⊇ reachable plus measures over-keep, with no compiler; `carve-build-oracle.ps1` compiles and
links the carved tree. These are heavy at scale and run locally, not in CI.

## Coverage plan: every step, at scale (agreed with CodeSpawner)

Two header-carve bugs (1.0.324, 1.0.325) took hours on a real tree and seconds on ours: our trees didn't have
the shape that triggered them (millions of paste macros in big generated headers), and our tests checked a time
limit at one small size, not how time grows. This plan closes both gaps for every step, not only the header carve.

### Three tiers

| Tier | What | Where | Fails when |
|---|---|---|---|
| **T1 growth** | each step on an inline generator at N and 4N | unit tests, CI | time or peak memory grows > 6x (linear is 4x, quadratic 16x) |
| **T2 truth** | the CodeSpawner `firmware` corpus at `--scale 0.01` | CI | carve misses a `mustKeep` name or file, keeps a `mustDrop` define, a count differs from the manifest, the carved units don't compile (every unit in each build's compile db, with its `-D`/`-I`, `-std=c11 -Wall -Werror -c`, gcc and clang), or two carves differ (output bytes or summary counts) |
| **T3 scale** | the same corpus at `--scale 0.1` (1x on demand), one family scaled 4x with the rest fixed | locally, before every perf push, next to the memory A/B | wall time or peak memory per step past its recorded baseline, plus the T2 checks |

Every family in the corpus has its own count dial (files, paste macros, forwarders, chain length, include
depth, header size, assembly, `.cmm`, ...), so T3 can grow one dimension 4x and see which step scales badly.

### What the truth says (manifest, manifestVersion 1, added fields only)

- `mustKeepDefines`, `mustKeepFunctions`: what a sound carve keeps (soundness: carve ⊇ mustKeep).
- `mustDropDefines`: big-header defines no rule reaches, named from an alphabet no kept text uses (precision:
  carve ∩ mustDrop = ∅).
- `mustKeepFiles`: infrastructure and whole-file stages (`.cmm`, data loaded by name, scripts, makefiles).
- Exact counts: `bigHeadersKept`, `wholeIncludedFromOutside`, `denseHeaders`, `bytesBefore`, `pasteMacros`, `forwardingMacros`, `totalDefineLines`,
  `definesKept + definesDropped == totalDefineLines`; bounds for `definesDropped` and `bytesAfter` (the
  over-keep is the carve's choice, so the truth only bounds it).
- `firmware.treeSha`: a digest over every `fw/` file's relative path and bytes; T2 checks it before carving (a
  stale or damaged copy, a copy over a share).
- Trap symbols and the expected replan additions; hostile files (path and kind); dense headers; big headers
  included from outside the root.

The truth follows the **sound** rules below, never the current code: a case the carve gets wrong is a Carver
bug fixed against the corpus, not a corpus case removed.

### Header-carve rules the truth is built from

1. A name is a **paste macro** if any of its definitions anywhere pastes a parameter (`a##b`, not
   `, ##__VA_ARGS__`), or forwards to a paste macro: by a function-like body that calls one, or an object-like
   define whose body's last token is one (`#define F CAT`: `F(a, b)` calls it; `#define F (CAT)` does not). Order does not matter; a later definition only adds.
2. **Pieces**: every identifier beside a `##` (any length; numbers are not pieces); every identifier among the
   arguments of a paste-macro call in kept text, a call spanning lines included; a paste macro passed as an
   argument (`APPLY(CAT, A, 2)`) makes the enclosing call's argument identifiers pieces; in a computed call
   (`CAT(FWD_, 7)(x)`) the trailing arguments count too; and, for each argument identifier, the first and last
   token of every body of its object-like defines when that token is an identifier (an argument passed on is
   expanded, and `##` joins only the boundary tokens), each expanded the same way, transitively.
   Pieces come only from kept text outside the big headers, the big headers' non-define lines, and the bodies of
   **wanted** big-header defines (a define never kept is never expanded); rule 1 uses every definition. A
   define's own parameters are never pieces: its call sites supply them. An argument list runs to its matching
   `)` across lines and directives (`CAT(A,` / `#if X` / `B)` / `#else` / `C)` / `#endif` gives A, B and C).
   Calls inside comments or strings are left open: no truth name depends on them.
3. **Wanted** starts as every identifier of every line of kept text outside the big headers, every identifier of
   every non-define line of the big headers (comments, strings, `#undef`, `#pragma`, inactive `#if` text
   included), the identifiers of each build's `-D` defines, those of every header outside the root that a build
   includes, those of big headers kept whole because they are included from outside the root, and the reserved names (`__x`, `_` + uppercase, and NDEBUG, DEBUG,
   WIN32_LEAN_AND_MEAN, VC_EXTRALEAN, NOMINMAX, STRICT, UNICODE, NOGDI, NOUSER, NOCRYPT, NOSERVICE, NOMCX, NOIME,
   WINVER, NTDDI_VERSION); it grows to a fixpoint through the bodies of wanted defines; plus every big-header define whose name starts or ends with a piece (a piece equal to the name
   counts). A wanted name keeps all its definitions.
4. A file is **big** (header-carved) when longer than `maxParseBytes`; exactly `maxParseBytes` is parsed. A file
   of 1,000,000 bytes up to `maxParseBytes` is **dense** (carved by the same rules) when, over its first 262,144
   chars (a truncated last line dropped), define lines are at least 0.60 of the substantive lines and there are
   at least 50 (blank lines and lines starting with `//` or `/*` are not substantive). Edge cases (0.60, 0.5999)
   are flagged `edge: true`.
5. Truth sets per build (`firmware.builds.<name>.{mustKeepDefines, mustDropDefines, mustKeepFunctions,
   mustKeepFiles}`, each a sorted name list with sha256 and count) and their union; Carver keeps the union of
   its builds, so T2 asserts the union, and per build for a one-build config. `mustKeepFiles` per build is the files
   defining any must-keep name, their include closures in that build, and the infrastructure. Phase 1 writes
   `mustKeepFunctions` empty (count 0) until the call graph lands in phase 2.

### Step by step

| Step | Corpus cases | Phase |
|---|---|---|
| Graph build | include chains 30+ deep, guard and `#pragma once` cycles, one header by several paths, computed includes, `#include_next`, C files from tiny to just under `maxParseBytes`, 10k-line functions, big initializer tables | 1.3.2 + 3 |
| Roots: tree macros, outside/quoted/unparsed headers | headers outside the include dirs, quoted includes relative to the includer, headers too big to parse, `-D` names | 1.3.2 + 1 |
| Roots: assembly | `.S` that includes C headers, calls C, is called from C; names used only from assembly or linker scripts (GNU as, `clang --target=arm-none-eabi`) | 2 |
| Roots: link flags, sections, constructors | `KEEP(*(.sec))` linker scripts, `section`/`used`/`constructor` attributes, functions reachable only through a section table | 2 |
| Roots: generated code, traces | a build log naming generated files; file-access and runtime traces | 3 |
| Roots: binaries, unbuilt | ELF32 ARM objects and `ar` archives defining names with no source; source never built | 2 |
| Header carve | paste macros and forwarders in big headers (both orders, chains, `#if`/`#else`, aliases, redefinitions, cycles), every paste shape, call pieces across lines, higher-order and computed calls, more than 16 bodies for one object define, 64/65 pieces, dense headers and a 0.59 decoy, a big `.c`, boundary sizes, comment traps | 1 |
| Emit, promote, placeholders | files kept whole vs carved; a define needed only by a placeholder; a header promoted to whole | 2 |
| Check, replan | trap symbols reached only through indirect edges; the expected replan additions | 2 |
| Copy infrastructure | `.cmm` (dense small bulk and a size tail), data loaded by name, scripts, makefiles, tool configs | 3 |
| Builds, stages, `#ifdef` worlds | two builds with different `#ifdef` worlds (two compile dbs); one function in both branches of a config `#if`; every stage on the same source | 2 |
| Hostile input | CRLF/LF mix, BOMs, UTF-16, Latin-1, NUL bytes before and after 8 KB, unterminated comments, empty files, a long line (a length dial); junction loops and long paths opt-in | 1.3.2 + 3 |
| Network read | the T3 run from a local share path; compile db with relative paths; `treeSha` | 1 |

Phase 1 (CodeSpawner 1.4.0) is the header-carve family plus a scaled seed tree; phases 2 and 3 follow. Carver
pins the new CodeSpawner version in `tools/codespawner` and adds the T2 script (carve with header contents
carved, then check the truth, counts and compile). Sizes and generation times at 0.01, 0.1 and 1, the per-step
case details and the hostile-file list are in CodeSpawner's `manifest-schema.md` under `firmware`.

## Running

- `./check.ps1` — Tier 1 (builds, then the fast suite).
- `./check.ps1 -Big` — also Tier 2 (the build-verify tests).
- `./check.ps1 -Big -Fetch` — fetch toolchains + corpus first, then everything. The before-you-push gate.
