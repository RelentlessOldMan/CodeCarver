# Testing strategy

CodeCarver must produce **working code**, so testing it means proving — automatically, on real
code — that a carve still builds and never drops something the image needs. The product itself needs
no compiler; **only this regression suite does**, and it uses *pinned* toolchains (never whatever is
on the dev box) so results are reproducible on any machine and in CI.

## Two tiers

### Tier 1 — pure engine (no toolchain, always runs)
The reachability engine, graph model, root/edge model, and the scenario library run under plain
`dotnet test` in ~tens of milliseconds. Hand-authored ground-truth (the `Scenarios/` library) needs
no compiler. This is the fast inner loop; keep it green constantly.

### Tier 2 — real repos + real toolchains (fetched, runs on demand / in CI)
The honest test: carve real code and let the actual toolchain judge it. Gated so a missing toolchain
skips (with a notice) rather than failing.

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

Fetched by a `fetch-toolchains.ps1` from pinned URLs/versions. The trio covers the cases that matter:

| Toolchain | Covers | Why this one |
|---|---|---|
| **arm-none-eabi-gcc** | embedded ELF: vector tables, `.init_array`, `--gc-sections`, maps | runs on Windows, cross-compiles to the target's actual world — no Linux box needed |
| **LLVM/Clang** | host C/C++, the `-E` preprocessing engine, later the semantic tightener | one cross-platform download, gold-standard preprocessing |
| **MinGW-w64 GCC** (optional) | a second host C compiler for cross-checking | catches compiler-specific assumptions |
| **.NET SDK** (already present) | C# via Roslyn | already required to build CodeCarver |

Pinned by version + URL (and ideally hash) so every run/box gets identical tools. Toolchains are
generated/fetched, never committed.

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

## Preprocessed inputs for tightness tests

To test the tightness ladder reproducibly, the pinned toolchain generates the resolved `.i` files
(`-save-temps` / `-E`) that the `--preprocessed` mode consumes — so `#ifdef`-resolution tightening is
tested against real, deterministic preprocessed output, not a hand-wave.

## Running

- `dotnet test` — Tier 1 (fast, always).
- `./check.ps1 -Big` (planned) — fetch toolchains + corpus, run Tier 2, assert carved output builds
  and soundness holds. The before-you-push gate.
