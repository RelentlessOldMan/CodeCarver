# Running CodeCarver on the real firmware repo

A focused runbook for the actual target: a large C/C++ firmware repo whose build produces an **image**
flashed to many identical chips over a TRACE32 flow, where **image size is the headline metric**. This
is the "point a local session at it" doc — narrower and more opinionated than `docs/SHAKEDOWN.md`
(which is the general bug-hunting runbook). Fill in the blanks, work top-to-bottom.

The one invariant: **a carve must still build and still boot.** File-level carving (no `--prune`) is
the sound default; `--prune` (intra-file) is the aggressive tier where the size win — and the bugs —
live. Never trade soundness for size: when unsure, CodeCarver keeps more, and so should you.

Fill these in once:

| blank | what it is | how to find it |
|---|---|---|
| `<REPO>` | firmware source root | — |
| `<ROOTS>` | entry symbols the image truly needs | see **Roots** below — this is the whole game |
| `<LANG>` | `c` or `cpp` | `cpp` if any `.cpp/.cc/.hpp` in the build |
| `<EXCLUDE>` | dirs NOT in this image | tests, host tools, **other board/chip variants**, bootloader-vs-app |
| `<AUX>` | linker/startup files to copy verbatim | `*.ld,*.lds,*.s,*.S,*.icf` globs |
| `<BUILD>` | your real build command | the make/cmake/TRACE32 invocation that produces the image |
| `<SIZE>` | image-size probe | `arm-none-eabi-size`, or `.bin` byte count |

---

## 0. Roots — get these right or nothing else matters

An embedded image has **no single `main`** the way a desktop program does. Its live set is anchored by
symbols the linker/runtime keep implicitly, and if you don't name them the carve will look tiny and be
wrong. Enumerate:

- **`main`** / RTOS **task entry points** / the scheduler's registered tasks.
- **Every ISR in the vector table.** These are called by hardware, never by C code. CodeCarver
  auto-discovers many implicit roots (vector table, `.init_array`/`constructor`, `used`/`retain`,
  linker `KEEP()`, weak aliases, inline-asm refs, standalone `.s` startup) — the summary line prints
  `implicit:`/`asm:`/`section:` counts. **Verify that count matches your mental model** of how many
  ISRs/ctors you have. If an ISR isn't showing up, name it explicitly in `<ROOTS>`.
- **Exported API** the image exposes (bootloader→app entry, TRACE32-poked functions, DFU/comms
  handlers, calibration hooks). Anything invoked from *outside* the C call graph is a root.
- **Symbols referenced only from a linker `KEEP()`** or a custom `__attribute__((section))` — pass the
  linker script via `--aux` and CodeCarver roots `KEEP()`'d sections automatically; still eyeball the
  `section:` count.

> Rule of thumb: if dropping it would leave the chip unable to boot, respond to an interrupt, or answer
> a command you poke over TRACE32 — it's a root.

**Guard the list with `--strict-roots`.** Because the root set is long and hand-maintained, a single
typo'd ISR/API name would otherwise carve that symbol away silently and look like a *cleaner* result
(bigger size win). CodeCarver warns per unresolved root and flags them on the `roots` summary line;
`--strict-roots` turns any unresolved root into a non-zero exit. Use it in CI / the preset so a typo
fails loudly instead of shipping a broken image. If a name is legitimately absent for this variant
(defined only under a different `#ifdef`, or in an excluded board dir), fix the name/exclude — don't
drop the flag.

## 1. Smoke test (analysis only, no write)

```
carve <REPO> --roots <ROOTS> --lang <LANG> --exclude <EXCLUDE> --aux <AUX>
```

Read the summary hard:
- `roots` / `implicit:` / `asm:` / `section:` — do the implicit counts match reality? (see Roots).
- `none of the requested roots were found` → a macro-defined signature or namespace-macro file the
  front-end didn't capture. `--dump-spans | findstr <sym>` to confirm; if truly missing it's a bug —
  capture the definition's exact text and file it (see `docs/SHAKEDOWN.md` §6).
- `0 files kept` / `100% smaller` with lots of `warn:` → the "silently resolved nothing" trap; your
  roots didn't anchor anything. Fix roots before going further.
- A crash/stack trace → always a bug (the tool warns-and-skips, never throws). Capture it.

## 2. Compiler-free soundness gate

```
carve <REPO> --roots <ROOTS> --lang <LANG> --exclude <EXCLUDE> --prune --verify
```

`--verify` flags any **kept** function that calls an **in-scope dropped** function (that wouldn't link)
and exits non-zero. Run with and without `--prune`. Every violation is a concrete bug — note the
`caller -> callee` pair and run `--why <callee>`. Caveat learned the hard way: `--verify` only sees
call shapes the front-end recognizes, so it cannot catch a reference it never modeled — **§4 (build the
image) is the authoritative test.**

## 3. Feed the real config so the carve matches the real build

An `#ifdef`-heavy firmware tree carves *looser* than needed in open-world mode (unknown branches kept)
and, more dangerously, can mismatch your build if it guesses. Pin the world to your actual build:

- `--build-log <make -n output>` — the exact `-D`/`-I` flags your build uses (best).
- or `--define CHIP=X,FEATURE_Y,...` — the defines for **this** image variant.
- or `--probe <arm-none-eabi-gcc>` — let CodeCarver ask the compiler for its predefined macros.

Match this to the variant you're carving (chip rev, app-vs-bootloader). Getting it right is both a
tightness win (smaller image) and a soundness guard (no branch mismatch). See `docs/USAGE.md` §tightness.

## 4. The authoritative test — build the carved image and compare size

```
carve <REPO> --roots <ROOTS> --lang <LANG> --exclude <EXCLUDE> --aux <AUX> \
      --build-log build.log --prune --out out/ --manifest m.json
# then build out/ with YOUR toolchain / TRACE32 flow:
<BUILD>            # pointed at out/
<SIZE>             # image size of the carved build
```

Compare against the same `<BUILD>`/`<SIZE>` on the original tree:
- **Compile errors** (`undeclared`, `implicit declaration`, `has no member`, `expected '}'`) → a dropped
  symbol / broken structure. Highest-value findings — capture file + first error lines.
- **Link errors** (`undefined reference`, `aliased to undefined symbol`) → a missed implicit root
  (vector entry, weak alias, `KEEP()` section, C++ constructor). Add it to `<ROOTS>` or file it.
- **Boots but misbehaves** → an indirectly-referenced table/handler dropped. Rare (CodeCarver
  over-approximates function-pointer tables), but if it happens, capture the construct.
- **Image size delta** → the payoff. Record before/after `<SIZE>`. Do a **file-level** run (omit
  `--prune`) too: it should almost always build, and its size delta is your safe floor.

Order of trust: **builds + boots + smaller** > builds > `--verify` clean. Only the first is the goal.

## 5. If you have a Linux host (mini-PC / WSL) — the linker-map oracle

The strongest automated check. Build the **full** source with `--gc-sections` and force-keep the roots
so the *linker* discards everything unreachable, then assert the linker-kept set ⊆ carve-kept set (a
linker-kept function the carve dropped is a real soundness bug). This is how the `adler32` and the C++
template-call bugs were found. Tooling is committed and ready:

- `wsl-map-oracle.sh` — linker-kept ⊆ carve-kept (C).
- `wsl-cpp-oracle.sh` + `cpp-oracle-sweep.ps1` — link the carved C++ `--out` against a root-calling
  driver (`oracle/*_driver.cpp`).
- `wsl-obj-oracle.sh` — single hot file, no full link: compile one source baseline-vs-carved and diff
  object symbols; flags any symbol defined in baseline but referenced-undefined in the carve (catches
  prototype-covered static drops). Ideal for a giant amalgamation-style `.c`.

**Match the `-D`/`-I` config between the oracle build and the carve** (§3) or you get false mismatches.
See `docs/SHAKEDOWN.md` §5b for the caveats.

## 6. Systematic hammering (vary one axis, re-run §2 + §4)

| axis | values |
|---|---|
| roots | full entry set · ISR-only · one exported API · bootloader vs app |
| prune | *(off — file level, the safe floor)* · `--prune` · `--prune --prune-headers` |
| config | `--build-log` (best) · `--define <variant>` · `--probe <cc>` |
| exclude | none · tests/tools · **other chip/board variant** (stops name-collision over-keep) |
| variant | each of the 24 targets' build config, if they differ |

High-yield shapes in firmware (past bugs came from these): heavy macros, macro-opened namespaces,
computed-goto interpreters, `try`/`catch` in macros, generated `.inc`/`.def` tables `#include`d into a
`.c`, `#if`-split function-pointer tables, weak-alias handler fan-out, and C++
constructors/operators/templates. If your image is C++-heavy, template methods called only with
explicit template args (`obj.method<N>()`) were a real drop — now fixed and regression-tested, but the
class is worth a look on your own templates.

## 7. Triage (for anything that breaks)

Capture: the **file** + first compiler-error lines; `--why <missing_symbol>` (was it CARVED, or kept
with a chain to a root?); `--dump-spans | findstr <symbol>` (captured at all? right span?); the
**construct** that tripped it (macro/table/asm/`#if`/template); and a **minimal repro** if you can. A
good report reads: *"carving `<REPO>` for roots `<X>` with `--prune`: `foo.c` fails — `get_bar`
undeclared; `--why get_bar` says CARVED; it's called from a `FOO_TABLE(...)` macro at foo.c:120."*
That's directly fixable as a front-end/emitter change with a regression test.

## 8. Local vs network file-share differential (no toolchain needed)

Before you can build, one more source-only signal worth collecting: carve the **same repo from two
locations** — your local copy and a copy on a network share (UNC `\\server\share\...` or a mapped
drive) — and assert the carve **decisions are byte-identical**. The source location must never change
what's kept or dropped; if local and network disagree, that's a real bug — path normalization (UNC vs
drive-letter, backslash/forward-slash, `>260`-char long paths, case-folding) or nondeterministic
ordering. It also times both runs so you see the network I/O cost on the big tree.

```powershell
./differential-carve.ps1 -RepoA C:\work\firmware -RepoB \\server\share\firmware `
    -Roots <ROOTS> -Lang <LANG> -Exclude <EXCLUDE> -Prune
# determinism only (same path twice): omit -RepoB
```

It compares the manifest (everything except the absolute `root` line — roots, defines, stats,
kept/dropped file sets) and every emitted file byte-for-byte, and prints `DIFFERENTIAL PASS/FAIL`. A
`FAIL` is directly file-able (capture the differing entries). This is the recommended way to exercise
the network path safely before spending toolchain time on a build.

The harness disables the parse budget by default (`-ParseTimeout 0`) on purpose: the budget is a
wall-clock decision (a huge file is kept-whole if its parse blows the timeout), so a slower share could
keep-whole a file it carved locally — a *timing* difference, not a path bug. Disabling it isolates
path-handling from I/O speed. If a `FAIL` appears, first compare the `warn:` lines: different warnings
(a skipped unreadable file, a parse-budget keep-whole) mean the two runs saw different *inputs* — an
environment effect, not a defect. Same inputs + same warnings + different output is the real bug.

---

See `presets/embedded-arm.example.ps1` for a fill-in-the-blanks driver that runs §1–§4 and prints the
image-size delta, and `differential-carve.ps1` (§8) for the local-vs-network check.
