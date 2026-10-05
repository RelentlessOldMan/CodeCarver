# Shakedown: hammering CodeCarver against a real repo

This is a runbook for stress-testing CodeCarver on a real, messy codebase to shake out bugs. It's
written to be followed by **you or a local coding-agent session** — point a session at this file and say
"follow docs/SHAKEDOWN.md to hammer CodeCarver against `<my repo>`, roots `<syms>`."

The one invariant to test everything against: **a carve must still build.** File-level carving
(`carveSourceFileContents = false`) is the sound default; intra-file carving (`carveSourceFileContents = true`,
via a `[stages]` tier) is the aggressive tier and is where bugs live. Every finding is ultimately "the carved
output didn't compile/link, but the original did."

---

## 0. Setup (once)

```powershell
dotnet build CodeCarver.sln -c Release            # the CLI
./fetch-toolchains.ps1                             # pinned gcc/g++/arm-none-eabi (for build-verify)
```

Use the built DLL directly to avoid `dotnet run` overhead on big repos:
`dotnet src/CodeCarver.Cli/bin/Release/net8.0/codecarver.dll carve ...` (`codecarver carve ...` is written
`carve` below; the other commands are `codecarver init`, `codecarver version`, `codecarver scan-log`).

**Record the version you tested.** `codecarver version` prints `CodeCarver <semver>+<git-sha>[-dirty]` — the
commit is stamped into the build, so cite this exact string in any report (it also heads every carve's
summary and is written to each carve's `codecarver/manifest.json` as `codecarverVersion`). `-dirty` means the built tree had
uncommitted changes; a clean pulled build won't show it. Rebuild after a `git pull` so the stamp updates.

Inputs live in a TOML `--config` file (`codecarver init` writes an annotated `carve.toml`; relative paths in it
resolve against the config file's directory). Pick your inputs:
- **entryPoints** — the entry symbols a build actually needs (ISRs, `main`, exported API, task entry points).
- **languages** — `["c"]` or `["c","cpp"]`.
- **excludeDirectories** — test/vendor/third-party dirs, and any *other* build variant (e.g. a second board's
  startup) so name collisions don't over-keep.

---

## 1. Smoke test

```
codecarver init                # then fill in entryPoints, languages, excludeDirectories, analysisOnly = true
carve <repo> --config carve.toml
```

Expect a summary: `roots`, `nodes`, `files`, `verify`, `world`, a `size` line, and `implicit:`/`asm:`/`section:`
lines for auto-kept embedded roots. **Red flags right here:**
- `requested root '<sym>' was NOT found as a symbol` → the run fails (exit 1). Check the "did you mean" names
  for a typo; `--why <sym>` still works. If it's a macro-defined signature or a namespace-macro file, that's a
  real bug — capture the definition's exact text.
- A configuration error (exit 2) — a build log with no recognised compile command, a trace with no path under
  the carve root, a compiler that can't be probed, an existing output directory CodeCarver didn't create. The
  message says what to change.
- `0 files kept` / `100% smaller` with lots of `warn:` lines → the "silently resolved nothing" trap.
- A crash / stack trace → always a bug (the tool is supposed to warn-and-skip, never throw). Capture it.

## 2. The soundness loop (no compiler needed)

```
carve <repo> --config carve.toml      # with a [stages.prune] (carveSourceFileContents = true)
```

The **`verify` check runs on every C/C++ carve**. It reads the **emitted** tree with its own tokenizer, independent
of the carve's graph, and fails the run (**exit 3**) when emitted code uses a function that only a **dropped**
file defines (the tree would not link). Uses on `#ifdef`-dead lines are notes, not failures. Details land in
`codecarver/verify.txt`. Check it with file-level AND an intra-file stage. Any failure is a concrete bug — note
the function and where it is used, and run `carve <repo> --config carve.toml --why <function>`. `verify` cannot
see missing types, macros or headers; step 3 can.

## 3. The real test — build the carved output

The ultimate check is your own build pointed at the carved tree:

```
carve <repo> --config carve.toml   # outputDirectory + a [stages.prune] in the config
# then build `<outputDirectory>/[<stage>/]carved` with YOUR toolchain (make, cmake, TRACE32, arm-none-eabi-gcc, …)
```

- **Compile errors** (`undeclared`, `implicit declaration`, `has no member`, `expected '}'`) → a dropped
  symbol / broken structure. These are the highest-value findings.
- **Link errors** (`undefined reference`, `aliased to undefined symbol`) → an implicit root missed
  (vector table, weak alias, `KEEP()` section, constructor).
- Compare `codecarver/manifest.json` (kept/dropped lists) against what you *know* the build needs.

Also do a **file-level** run (`carveSourceFileContents = false`) — if that breaks, it's a more serious bug
than an intra-file one (file-level should almost always build).

## 4. Systematic hammering

Vary one axis at a time and re-run steps 2–3. Each cell is a chance to break it:

| Axis | Values to try (config keys) |
|---|---|
| entryPoints | one symbol · your full entry set · an obscure/rarely-used API · an ISR-only set |
| granularity | file-level · `carveSourceFileContents` · + `carveHeaderFileContents` (via `[stages]`) |
| config | none · `defines = ["X=1","Y"]` · `buildLogs = ["build.log"]` (from `make -n`) · `compiler = "<cc>"` |
| exclude | none · `excludeDirectories = ["tests","vendor"]` · exclude other board/arch variants |
| limits (`[advanced]`) | default · `maxParseBytes = 5000000` · `parseTimeout = 5` |

High-yield shapes to aim at (these are where past bugs came from): heavy macros, macro-opened namespaces
(`FMT_BEGIN_NAMESPACE`-style), computed-goto interpreters, `try`/`catch` wrapped in macros, generated
`.inc`/`.def` tables `#include`d into a `.c`, constructors/`operator`/templates (C++), giant single-file
amalgamations, and `#if`-split function-pointer tables.

## 5. Automated baseline-vs-carved (for standalone-compilable files)

`fuzz.ps1` automates steps 3 for files that compile standalone: it baseline-compiles each source
uncarved with the pinned gcc/g++, carves intra-file (`carveSourceFileContents`), recompiles, and reports
any file that compiled before but fails after.

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

**The check that works today — link the carved tree.** The most robust form (and the one the CLI supports
now) is simply to **link the carved output itself** and let the linker find any dangling drop:

```powershell
# On Windows: carve to a buildable tree (config names the roots + the real -D/-I world)
carve <repo> --config carve.toml      # outputDirectory = D:/carved/<name>, entryPoints = <syms>
```
```bash
# In WSL: link the carved C sources with --gc-sections and the roots forced undefined.
gcc <carved>/*.c empty_main.c -Wl,--gc-sections -Wl,--undefined=<root>...
# an `undefined reference` is a genuine dangling drop (exactly how the adler32 bug surfaced). For C++: g++.
```

**Match the configs.** The carve and any oracle build must see the *same* `-D` world, or you get false
mismatches (e.g. a repo's name-mangling macros). Feed the carve the real config (`buildLogs` / `defines` /
`compiler`) so both see the same world.

> **nm-comparison variant (`wsl-map-oracle.sh`).** It compares the carve's per-symbol kept/all function sets
> against the linker's. Make the two lists from the carve's `codecarver/decisions.txt` (a full ledger is
> written for graphs up to 500,000 nodes):
> ```bash
> awk '$2=="Function"{print $3}' decisions.txt | sort -u > ccAll.txt
> awk '$1=="KEPT" && $2=="Function"{print $3}' decisions.txt | sort -u > ccKept.txt
> INC="-I. -Iinc" bash wsl-map-oracle.sh <repoDir> "$PWD/ccKept.txt" "$PWD/ccAll.txt" <root1,root2> <cfile...>
> ```
> It compiles with `-fvisibility=hidden` so the linker's kept set is *reachable-from-roots*, not *every exported
> symbol* — treat a flagged function that is itself an unused top-level API (nothing kept calls it) as a false
> positive; confirm with `--why`. The link-the-carved-tree check above needs no lists at all.

**C++ link oracle (one command).** `wsl-cpp-oracle.sh` automates the link-the-carved-output check for
C++: carve the repo on Windows, then link the carved tree against a tiny driver `main()` that calls the
roots. Committed drivers live in `oracle/` (e.g. `oracle/tinyxml2_driver.cpp`, `oracle/pugixml_driver.cpp`).
An `undefined reference` means the carve dropped a symbol a root reaches.

```powershell
# Windows: carve to a C++ tree (entryPoints in the config must match the driver's calls)
carve <repo> --config carve.toml      # languages = ["cpp"], outputDirectory = .oracle-cpp/<name>, a [stages.prune]
```
```bash
# WSL (after: sudo apt install -y g++): point at the carved/ subdir of the output directory.
INC="-I/mnt/c/.../.oracle-cpp/<name>/carved" \
  bash wsl-cpp-oracle.sh /mnt/c/.../.oracle-cpp/<name>/carved /mnt/c/.../oracle/<name>_driver.cpp
```

The script excludes `*test*`/`*demo*` units (and any `EXCLUDE=<regex>`, e.g. C++20 module units),
links with `--gc-sections`, and prints undefined references / compile errors. Note: header-only
template libraries (e.g. fmt) carry their bodies in headers, so file-level carving can't shrink them —
the meaningful C++ oracle targets are single-unit libraries with a `.cpp` (tinyxml2, pugixml),
amalgamations (simdjson), or a header compiled under `carveHeaderFileContents` (nlohmann/json).

**Whole-corpus sweep (one command):** `./cpp-oracle-sweep.ps1` carves every present C++ corpus repo and
runs the oracle against each with a committed driver (`oracle/<name>_driver.cpp`). This is how the
**template-argument-call** soundness bug was caught: simdjson calls `simd8::prev<N>`/`get<N>`/`shr<N>`
only with explicit template args (`obj.prev<2>(x)`), which tree-sitter parses as `template_method` /
`template_function` — the call query didn't match those, so the methods were pruned and the carve
failed to compile. Fixed by adding template-call patterns to `CppFrontEnd.CallsQuery`.

## 6. Triage a finding

For each break, capture enough to reproduce:

1. The **file** and the **compiler error** (first few lines).
2. `carve <repo> --config carve.toml --why <missing_symbol>` — shows whether it was CARVED and, if kept, its
   chain to a root.
3. The manifest's kept/dropped lists (`codecarver/manifest.json`) — was it captured/kept at all?
4. The **source** of the symbol + how it's referenced (the construct that tripped it — macro? template?
   table? asm? `#if`?).
5. Reduce to a **minimal repro** if you can: a few-line `.c`/`.cpp` that carves wrong.

**If the source can't leave the machine** (a proprietary repo), steps 1–5 stay local and what goes back is
source-free: `codecarver/summary.txt` (or `summary.json`) — counts, booleans and fixed category names only,
no path, file name or symbol — plus the exit code, the anonymized `codecarver/repro.graph.json`, and a
description of the construct's *shape* rather than its names. On a crash, add the diagnostic `.zip` the tool
prints. See [`SUPPORT.md`](SUPPORT.md).

A good report is: *"carving `<repo>@<sha>` for roots `<x>` with an intra-file stage (`carveSourceFileContents`):
`foo.c` fails — `get_bar` undeclared; `--why get_bar` says CARVED; it's called from a `FOO_TABLE(...)` macro
at foo.c:120. Minimal repro attached."* That's directly actionable.

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
