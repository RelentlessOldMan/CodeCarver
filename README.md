# CodeCarver

[![CI](https://github.com/RelentlessOldMan/CodeCarver/actions/workflows/ci.yml/badge.svg)](https://github.com/RelentlessOldMan/CodeCarver/actions/workflows/ci.yml)

**Carve a huge, messy repo down to only the code needed to build and run one thing — and
strip the unnecessary code inside that thing too.**

Not just dead-code removal: *unnecessary*-code removal relative to a chosen entry set and a
specific build configuration. You say what you need (entry points / files, a build target +
config, optionally a runtime trace); CodeCarver traces every dependency and emits the
minimal slice that still **builds, links, and runs**.

> **Adapting it to your own codebase?** You don't need to share any source. See
> **[`docs/ADAPTING.md`](docs/ADAPTING.md)** — fill in a short anonymized *Codebase Profile* (patterns,
> entry points, config) and hand it to a CodeCarver session; it configures and hardens the tool for
> you, no private code required.

> ⚠️ **WIP — not yet validated on a real-world production build.** The engine works and the carved
> output compiles across 20+ open-source repos (below), but it hasn't been proven on a large
> proprietary target yet. Treat `--prune` (intra-file) as experimental; file-level carving is the
> sound default.
>
> Status: **working for C, C++, C#, and TRACE32 `.cmm`.** Tree-sitter front-end, deterministic reachability engine,
> `#ifdef` resolution, build-log scraping, and an emitter that does both **file-level** and
> **intra-file** carving (unused functions *and* data tables). Scales to **multi-GB auto-generated
> headers** — files past `--max-parse-bytes` skip the parser and are kept whole via `#include`-closure,
> so a 1.4 GB register header ingests in a second instead of exhausting memory; `--prune-headers` then
> streams it down to just the `#define`s you transitively use (a 45 MB / 1M-define header → a few hundred
> bytes, in one test). Carve → prune → **compile-clean** is
> verified on 20+ real repos (cJSON, SQLite, Lua, zlib, mongoose, mimalloc, monocypher, tiny-regex-c,
> qrcodegen, rax, …), and **link-clean on real Cortex-M firmware** with `arm-none-eabi-gcc` — vector-table
> ISRs and weak-alias handlers survive, dead code leaves the image. C# and `.cmm` carve file-level. See
> [`docs/USAGE.md`](docs/USAGE.md) to use it, and [`DESIGN.txt`](DESIGN.txt) for the *why*.

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
local coding-agent session).

## Carve something

```powershell
dotnet run --project src/CodeCarver.Cli -- carve <dir> --roots foo,bar --prune --out out/
dotnet run --project src/CodeCarver.Cli -- demo          # a narrated toy embedded carve
```

Full command/option reference — the tightness ladder, `#ifdef` resolution, build-log scraping, the
size report — is in [`docs/USAGE.md`](docs/USAGE.md). For worked examples with inputs **and** carved
outputs checked in, start at [`examples/`](examples) (the `stringlib` walkthrough shows file-level +
intra-file + table pruning on one small library).

## Layout

```
src/CodeCarver.Core       graph model · roots · reachability · #ifdef scanner · build-log scraper · emitter
src/CodeCarver.Frontend   tree-sitter C/C++ extraction (calls, macros, globals, conservative edges)
src/CodeCarver.Cli        the `carve` / `scan-log` / `demo` commands
tests/CodeCarver.Tests    xUnit suite (fast) + build-verify (compiles carved output with gcc)
docs/USAGE.md             how to use it        DESIGN.txt   architecture + rationale
docs/TOOLING.md           toolchain adapters   docs/TESTING.md   test strategy
fetch-*.ps1 · fuzz.ps1    fetch corpus/toolchains · carve-fuzz a repo (docs/REPRODUCE.md)
docs/SHAKEDOWN.md         runbook to hammer it against your own repo and find issues
```

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🔪

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
