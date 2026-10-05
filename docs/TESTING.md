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

## Running

- `./check.ps1` — Tier 1 (builds, then the fast suite).
- `./check.ps1 -Big` — also Tier 2 (the build-verify tests).
- `./check.ps1 -Big -Fetch` — fetch toolchains + corpus first, then everything. The before-you-push gate.
