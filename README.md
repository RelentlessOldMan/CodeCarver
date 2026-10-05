# CodeCarver

[![CI](https://github.com/RelentlessOldMan/CodeCarver/actions/workflows/ci.yml/badge.svg)](https://github.com/RelentlessOldMan/CodeCarver/actions/workflows/ci.yml)

**Carve a huge, messy repo down to only the code needed to build and run one thing — and
strip the unnecessary code inside that thing too.**

Not just dead-code removal: *unnecessary*-code removal relative to a chosen entry set and a
specific build configuration. You say what you need (entry points / files, a build target +
config, optionally a runtime trace); CodeCarver traces every dependency and emits the
minimal slice that still **builds and links**.

> **Adapting it to your own codebase?** You don't need to share any source. See
> **[`docs/ADAPTING.md`](docs/ADAPTING.md)** — fill in a short anonymized *Codebase Profile* (patterns,
> entry points, config) and hand it to a CodeCarver session; it configures and hardens the tool for
> you, no private code required.

> ⚠️ **WIP — not yet validated on a real-world production build.** The engine works and the carved
> output compiles across 20+ open-source repos (below), but it hasn't been proven on a large
> proprietary target yet. Treat intra-file carving (`carveSourceFileContents`) as experimental; file-level
> carving is the sound default.
>
> Status: **working for C, C++, C#, and TRACE32 `.cmm`, on Windows and Linux.** Tree-sitter front-end, deterministic reachability engine,
> `#ifdef` resolution, build-log scraping, and an emitter that does both **file-level** and
> **intra-file** carving (unused functions *and* data tables). Scales to **multi-GB auto-generated
> headers** — files past `maxParseBytes` skip the parser and are kept whole via `#include`-closure,
> so a 1.4 GB register header ingests in a second instead of exhausting memory; `carveHeaderFileContents` then
> streams it down to just the `#define`s you transitively use (a 45 MB / 1M-define header → a few hundred
> bytes, in one test). Carve → prune → **compile-clean** is
> verified on 20+ real repos (cJSON, SQLite, Lua, zlib, mongoose, mimalloc, monocypher, tiny-regex-c,
> qrcodegen, rax, …), and **link-clean on real Cortex-M firmware** with `arm-none-eabi-gcc` — vector-table
> ISRs and weak-alias handlers survive, dead code leaves the image. C# and `.cmm` carve file-level. Every carve
> checks its own emitted tree for link errors (`verify`, exit 3 on failure). See
> [`docs/USAGE.md`](docs/USAGE.md) to use it, and [`DESIGN.txt`](DESIGN.txt) for the original design rationale
> (written before implementation; USAGE.md describes what was built).

## The idea in one paragraph

It's reachability slicing — like a linker's `--gc-sections` or a JS bundler's tree-shaking,
but generalized to any language and to *your* build config. Build a dependency graph, seed
it with roots (what you need), compute the transitive closure; everything outside is
unnecessary. The carve is a **single deterministic pass** — no build-and-retry loop, no
hardware in the loop. It's exact given (1) the exact `#define`/config, which *resolves* the
preprocessor rather than guessing it, and (2) a **complete** set of roots and edges. The
only real difficulty is graph completeness — the edges and roots that don't appear
syntactically (function pointers, vtables, and for embedded especially the **interrupt
vector table** and static initializers, which a naïve from-`main` closure silently drops).
CodeCarver handles those by discovering the implicit roots and modelling the non-syntactic
edges conservatively: when a target can't be resolved, it keeps the candidate set, trading
a little precision to never break the build.

## Real results — compiled *image*, not source bytes

The headline number is what lands on the target, so the harness compiles the full build vs. the
carved build with the pinned gcc at `-Os` and compares the code+data footprint (`text+data` from
`size` — the bytes that occupy flash), independent of any linker `--gc-sections`:

| Repo | Carve to | Full image | Carved image | Smaller |
|---|---|---:|---:|---:|
| cJSON | `cJSON_Parse/Print/Delete` | 21,168 B | 5,388 B | **75%** |
| tinyexpr | `te_interp` | 34,196 B | 8,356 B | **76%** |
| qrcodegen | `encodeText`, `getModule` | 10,228 B | 9,388 B | 8% |

Even *with* `-Os` already dead-stripping within each file, the carve removes what the optimizer
can't: functions that are externally visible (non-`static` API) but unreachable from *your* chosen
entry points. (qrcodegen is mostly lookup tables that the chosen roots genuinely need — an honest,
low-win case.) These are asserted per-build in the test suite (`CompiledImageSize_Shrinks_*`).

## Build & test

```powershell
dotnet build CodeCarver.sln -c Release
./check.ps1                 # fast tests
./check.ps1 -Big -Fetch     # also pull the pinned gcc + corpus and compile carved real repos
```

The corpus + toolchains are fetched by script, and `fuzz.ps1` replays the exact carve-and-recompile
loop that found the fmt / simdjson / pugixml / wren bugs — so anyone can reproduce both the validation
and the bug-hunt. See [`docs/REPRODUCE.md`](docs/REPRODUCE.md). To stress-test it against **your own**
repo and shake out issues, follow [`docs/SHAKEDOWN.md`](docs/SHAKEDOWN.md) (written to be handed to a
local coding-agent session). For an **embedded firmware image** specifically — where image size is the
metric and roots are ISRs/vector-table/exported API — use the tailored
[`docs/WORKREPO.md`](docs/WORKREPO.md) runbook and the fill-in-the-blanks
[`presets/embedded-arm.example.ps1`](presets/embedded-arm.example.ps1) driver.

## Carve something

```powershell
dotnet run --project src/CodeCarver.Cli -- init                     # write an annotated carve.toml, then edit it
dotnet run --project src/CodeCarver.Cli -- carve <dir> --config carve.toml
dotnet run --project src/CodeCarver.Cli -- demo                     # a narrated toy embedded carve
```

Full command/option reference — the tightness ladder, `#ifdef` resolution, build-log scraping, the
size report — is in [`docs/USAGE.md`](docs/USAGE.md). For worked examples with inputs **and** carved
outputs checked in, start at [`examples/`](examples) (the `stringlib` walkthrough shows file-level +
intra-file + table pruning on one small library).

## Layout

```
src/CodeCarver.Core       graph model · roots · reachability · #ifdef scanner · build-log scraper · emitter
src/CodeCarver.Frontend   tree-sitter C/C++ extraction (calls, macros, globals, conservative edges)
src/CodeCarver.Cli        the `codecarver` CLI: `carve` / `init` / `scan-log` / `version` / `demo` / `help`
tests/CodeCarver.Tests    xUnit suite (fast) + build-verify (compiles carved output with gcc)
examples/                 worked examples with configs and inputs; CI carves every one
tools/capture/            capture build/run file-access traces (ProcMon / strace) for buildTraceFiles / runTraceFiles
docs/USAGE.md             how to use it        DESIGN.txt   architecture + rationale
docs/TOOLING.md           toolchain adapters   docs/TESTING.md   test strategy
docs/SUPPORT.md           what to send when a carve goes wrong (source-free)
check.ps1                 test gate: fast suite; -Big adds build-verify; -Fetch pulls toolchains + corpus
fetch-*.ps1 · fuzz.ps1    fetch corpus/toolchains · carve-fuzz a repo (docs/REPRODUCE.md)
docs/SHAKEDOWN.md         runbook to hammer it against your own repo and find issues
docs/WORKREPO.md          tailored runbook for a real firmware image (size metric, ISR/vector roots)
presets/                  fill-in-the-blanks carve+build+size drivers (embedded-arm.example.ps1)
differential-carve.ps1    carve same repo from two paths (local vs network share); assert identical
release.ps1               package a versioned zip ONLY for a commit already pushed to origin
wsl-*-oracle.sh           Linux soundness oracles: linker-map (C) · link carved tree (C++) · object-symbol
wsl-syntax-check.sh       gcc/g++ -fsyntax-only every file in a tree (used by corpus-compile-sweep.ps1)
tools/codespawner/        vendored CodeSpawner generator (exe + manifest-schema.md + GENERATOR_VERSION)
carver-groundtruth-oracle.ps1  soundness+precision oracle: reads a CodeSpawner v1 manifest, carves, asserts kept ⊇ reachable + measures over-keep (scales to 100 GB)
carve-build-oracle.ps1    carve a CodeSpawner corpus, then compile + link it (positive) and prove indirect edges matter (negative)
carve-perf-bench.ps1      parse-throughput + peak-memory benchmark on a deterministic synthetic tree
carve-build-report.ps1    carve every corpus repo, BUILD the carved output (host gcc + ARM ELF), size table
corpus-compile-sweep.ps1  carve every corpus repo, compile each file baseline-vs-carved, flag regressions
cpp-oracle-sweep.ps1      link every carved C++ corpus repo against a root-calling driver (needs g++)
```

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🔪

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.

Bundled third-party components (Tomlyn, the tree-sitter .NET bindings, the tree-sitter library and its C, C++
and C# grammars) keep their own licences — MIT, BSD-2-Clause and the Unicode/ICU licence — reproduced in full
in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which ships in every release.
