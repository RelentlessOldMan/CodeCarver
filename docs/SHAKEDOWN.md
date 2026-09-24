# Shakedown: hammering CodeCarver against a real repo

This is a runbook for stress-testing CodeCarver on a real, messy codebase to shake out bugs. It's
written to be followed by **you or a local coding-agent session** — point a session at this file and say
"follow docs/SHAKEDOWN.md to hammer CodeCarver against `<my repo>`, roots `<syms>`."

The one invariant to test everything against: **a carve must still build.** File-level carving (no
`--prune`) is the sound default; `--prune` (intra-file) is the aggressive tier and is where bugs live.
Every finding is ultimately "the carved output didn't compile/link, but the original did."

---

## 0. Setup (once)

```powershell
dotnet build CodeCarver.sln -c Release            # the CLI
./fetch-toolchains.ps1                             # pinned gcc/g++/arm-none-eabi (for build-verify)
```

Use the built DLL directly to avoid `dotnet run` overhead on big repos:
`dotnet src/CodeCarver.Cli/bin/Release/net8.0/CodeCarver.Cli.dll carve ...` (called `carve` below).

Pick your inputs:
- **roots** — the entry symbols a build actually needs (ISRs, `main`, exported API, task entry points).
- **--lang** — `c` or `cpp` (default `c`).
- **--exclude** — test/vendor/third-party dirs, and any *other* build variant (e.g. a second board's
  startup) so name collisions don't over-keep.

---

## 1. Smoke test

```
carve <repo> --roots <syms> --lang <c|cpp> --exclude tests,vendor
```

Expect a summary: `roots`, `nodes`, `files`, `size`, and `implicit:`/`asm:`/`section:` lines for
auto-kept embedded roots. **Red flags right here:**
- `none of the requested roots were found` → the front-end didn't capture your entry symbols. Try
  `--dump-spans | grep <sym>`. If it's a macro-defined signature or a namespace-macro file, that's a
  real bug — capture the definition's exact text.
- `0 files kept` / `100% smaller` with lots of `warn:` lines → the "silently resolved nothing" trap.
- A crash / stack trace → always a bug (the tool is supposed to warn-and-skip, never throw). Capture it.

## 2. The soundness loop (no compiler needed)

```
carve <repo> --roots <syms> --prune --verify
```

`--verify` is a compiler-free gate: it flags any **kept** function that calls an **in-scope** function
the carve dropped (that wouldn't link) and exits non-zero. Run it with and without `--prune`. Any
violation it prints is a concrete bug — note the `caller -> callee` pair and run `--why <callee>`.

## 3. The real test — build the carved output

The ultimate check is your own build pointed at the carved tree:

```
carve <repo> --roots <syms> --prune --out out/ --manifest m.json
# then build `out/` with YOUR toolchain/build system (make, cmake, TRACE32 flow, arm-none-eabi-gcc, …)
```

- **Compile errors** (`undeclared`, `implicit declaration`, `has no member`, `expected '}'`) → a dropped
  symbol / broken structure. These are the highest-value findings.
- **Link errors** (`undefined reference`, `aliased to undefined symbol`) → an implicit root missed
  (vector table, weak alias, `KEEP()` section, constructor).
- Compare `m.json` (kept/dropped lists) against what you *know* the build needs.

Also do a **file-level** run (`--out out/` without `--prune`) — if that breaks, it's a more serious bug
than an intra-file one (file-level should almost always build).

## 4. Systematic hammering

Vary one axis at a time and re-run steps 2–3. Each cell is a chance to break it:

| Axis | Values to try |
|---|---|
| roots | one symbol · your full entry set · an obscure/rarely-used API · an ISR-only set |
| prune | *(off — file level)* · `--prune` · `--prune` + `--prune-headers` |
| config | none · `--define X=1,Y` · `--build-log build.log` (from `make -n`) · `--probe <cc>` |
| exclude | none · exclude tests/vendor · exclude other board/arch variants |
| limits | default · `--max-parse-bytes 5000000` · `--parse-timeout 5` |

High-yield shapes to aim at (these are where past bugs came from): heavy macros, macro-opened namespaces
(`FMT_BEGIN_NAMESPACE`-style), computed-goto interpreters, `try`/`catch` wrapped in macros, generated
`.inc`/`.def` tables `#include`d into a `.c`, constructors/`operator`/templates (C++), giant single-file
amalgamations, and `#if`-split function-pointer tables.

## 5. Automated baseline-vs-carved (for standalone-compilable files)

`fuzz.ps1` automates steps 3 for files that compile standalone: it baseline-compiles each source
uncarved with the pinned gcc/g++, carves `--prune`, recompiles, and reports any file that compiled
before but fails after.

```powershell
./fuzz.ps1 -Repo <repo> -Roots "<syms>" -Lang cpp -Inc include,src
```

If most files show `0 baseline` it means they need your real build config to compile — fall back to
step 3 (build `out/` with your toolchain), which is the authoritative test anyway.

## 5b. The linker-map oracle (Linux/WSL — the strongest test)

If you have Linux (WSL is fine) this is the highest-signal check: build the repo's full source with
`--gc-sections` and force-keep the roots, so the **linker** discards everything unreachable — then assert
every function the linker kept is also kept by the carve. If the linker kept one the carve dropped,
that's a real soundness bug (the carve would fail to link). This catches cross-file drops that per-file
syntax checks miss — it's how the `adler32`/Z_PREFIX bug was found.

```powershell
# On Windows: carve + dump the kept/all function sets
carve <repo> --roots <syms> --dump-spans > spans.txt
# (extract KEEP Function names -> cc_kept.txt, all Function names -> cc_all.txt)
```
```bash
# In WSL, from the repo root:
INC="-I. -Isrc" LIBS="-lm" bash wsl-map-oracle.sh <repoDirUnderMntC> cc_kept.txt cc_all.txt "<syms>" <cfiles...>
```

**Match the configs.** The oracle build and the carve must use the *same* `-D` flags, or you get false
mismatches (e.g. a repo's name-mangling macros). Feed the carve the real config (`--build-log` /
`--define` / `--probe`) so both see the same world.

**Two caveats on the nm comparison.** (1) `wsl-map-oracle.sh` compiles with `-fvisibility=hidden` so the
linker's kept set is *reachable-from-roots*, not *every exported symbol* — otherwise a public API that
merely *calls* the roots (e.g. tiny-regex-c's `re_match` → `re_matchp`) shows as a false "violation"
even though the carve correctly dropped it. A few repos still export such symbols; treat a flagged
function that is itself an unused top-level API (nothing kept calls it) as a false positive — confirm
with `--why`. (2) The most robust check, free of that noise, is simply to **link the carved `--out`
itself**: `gcc <carved>/*.c empty_main.c -Wl,--gc-sections -Wl,--undefined=<root>...` — an
`undefined reference` there is a genuine dangling drop (this is exactly how the adler32 bug surfaced).
For C++, `CC=g++ ...` (after `sudo apt install g++`).

**C++ link oracle (one command).** `wsl-cpp-oracle.sh` automates the link-the-carved-output check for
C++: carve the repo to `--out` on Windows, then link that carved tree against a tiny driver `main()`
that calls the roots. Committed drivers live in `oracle/` (e.g. `oracle/tinyxml2_driver.cpp`,
`oracle/pugixml_driver.cpp`). An `undefined reference` means the carve dropped a symbol a root reaches.

```powershell
# Windows: carve to --out (roots must match the driver's calls)
carve <repo> --lang cpp --roots <syms> --prune --out .oracle-cpp/<name>
```
```bash
# WSL (after: sudo apt install -y g++):
INC="-I/mnt/c/.../.oracle-cpp/<name>" \
  bash wsl-cpp-oracle.sh /mnt/c/.../.oracle-cpp/<name> /mnt/c/.../oracle/<name>_driver.cpp
```

The script excludes `*test*`/`*demo*` units (and any `EXCLUDE=<regex>`, e.g. C++20 module units),
links with `--gc-sections`, and prints undefined references / compile errors. Note: header-only
template libraries (e.g. fmt) carry their bodies in headers, so file-level carving can't shrink them —
the meaningful C++ oracle targets are single-unit libraries with a `.cpp` (tinyxml2, pugixml),
amalgamations (simdjson), or a header compiled under `--prune-headers` (nlohmann/json).

**Whole-corpus sweep (one command):** `./cpp-oracle-sweep.ps1` carves every present C++ corpus repo and
runs the oracle against each with a committed driver (`oracle/<name>_driver.cpp`). This is how the
**template-argument-call** soundness bug was caught: simdjson calls `simd8::prev<N>`/`get<N>`/`shr<N>`
only with explicit template args (`obj.prev<2>(x)`), which tree-sitter parses as `template_method` /
`template_function` — the call query didn't match those, so the methods were pruned and the carve
failed to compile. Fixed by adding template-call patterns to `CppFrontEnd.CallsQuery`.

## 6. Triage a finding

For each break, capture enough to reproduce:

1. The **file** and the **compiler error** (first few lines).
2. `carve <repo> --roots <syms> --why <missing_symbol>` — shows whether it was CARVED and, if kept, its
   chain to a root.
3. `--dump-spans | grep <symbol>` — was it captured as a node at all? at the right line span?
4. The **source** of the symbol + how it's referenced (the construct that tripped it — macro? template?
   table? asm? `#if`?).
5. Reduce to a **minimal repro** if you can: a few-line `.c`/`.cpp` that carves wrong.

A good report is: *"carving `<repo>@<sha>` for roots `<x>` with `--prune`: `foo.c` fails —
`get_bar` undeclared; `--why get_bar` says CARVED; it's called from a `FOO_TABLE(...)` macro at foo.c:120.
Minimal repro attached."* That's directly actionable.

Categories and what they usually mean:
- **dropped callee / undeclared** → a reference the front-end didn't model (macro/table/asm/template).
- **broken braces / `expected '}'`** → an emitter pruning bug (mis-parsed span, class/scope structure).
- **link: undefined reference** → a missing implicit root (vector table, alias, `KEEP()` section, ctor).
- **over-keeps** (carve barely shrinks) → a precision issue, not a soundness bug — still worth noting.
- **crash / hang** → always a bug; grab the file it was on (`CODECARVER_TIMING=1` prints slow files).

## 7. Notes for a coding-agent session

If you're an agent following this: work the list top-to-bottom, keep going after the first bug (collect
several), and for each real finding either fix it in the front-end/emitter with a regression test (see
existing tests + `docs/REPRODUCE.md` for the pattern) or write it up per §6. Prefer minimal repros as
pure `CFrontEndTests`/`CppFrontEndTests` (fast, CI-safe) and gate any that need a compiler behind
`FindGcc()/FindGxx()` like the build-verify tests. Never weaken soundness to make a carve smaller: when
unsure, keep more. Re-run `./check.ps1 -Big` before committing.
