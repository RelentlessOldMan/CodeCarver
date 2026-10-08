# CodeCarver — Getting Help

If a carve misbehaves — wrong output, a crash, a tree that won't build — you can hand a developer
enough to diagnose it **without sharing a single line of your source**. Everything below is either
source-free, made of your own names (never file *contents*), or anonymized code structure with every name
replaced (`debug/anon.zip`, below), and you decide what to send.

## The first thing to send: `summary.txt`

Every carve writes `<outputDirectory>/[<stage>/]codecarver/summary.txt` and the same data as `summary.json`.
They hold **numbers, booleans and fixed category names only** — the `#ifdef` world, graph size, roots by kind,
trace counts, `.cmm` counts, per-stage sizes and `verify` counts, warnings by category, the exit code. No path,
file name or symbol appears in them (a test enforces this), so they can be sent back as-is when the source must
stay on its machine. Send it together with the exit code and the CodeCarver version.

On a successful carve it also says where the carve could get tighter and where the time went:
`keep.files.<reason>` / `keep.bytes.<reason>` count each kept file once under why it is kept (`root`;
`indirectOnly`: kept only because its address is taken, a vtable or inline asm names it, the price of soundness;
`header`: included by kept code; `reached`: called or referenced from a root), `keep.bytes.largest10Percent` is the
share of kept bytes in the ten largest kept files, and `time.<phase>.ms` is each phase's time, in run order from
the start: `buildLogs`, `defines` (per-file define sets, compiler probe), `walk` (the tree walk and file sizes),
`buildTraces`, `input+refscan`, `pre-parse`, `build-graph`, `roots`, `reachability`, `precision`, `emit`. Those
add up to the run. A key with a further dot inside (`time.pre-parse.<part>.ms`, `time.build-graph.<part>.ms`,
`time.outsideIncludes.ms`, which is part of `roots`) is part of a phase, not added to it.
`time.pre-parse.<part>.ms` splits the macro scan before parsing: `treeMacros` (the tree's own files),
`outsideHeaders` (headers the build reads outside the root), `quotedIncludes` (following `#include "..."` out of the
tree) and `unparsedHeaders` (big and dense headers, streamed).
`stageN` is a stage's position in that run, least aggressive first: with no `--stage` and stages `safe` and `max`,
`max` is `stage1`; run alone with `--stage max`, it is `stage0`. `stageN.carveSourceFileContents` and
`stageN.carveHeaderFileContents` say which stage it is.
What content carving did, per stage: `stageN.sourceCarve.filesPruned` / `.bytesRemoved` (unreached definitions cut
from kept source files; headers are never pruned) and `stageN.headerCarve.*` (unused `#define`s cut from big
generated headers: `bigHeadersKept`, `wholeIncludedFromOutside` (left whole, see below), `bytesBefore`, `bytesAfter`,
`definesDropped`). Two stages that differ little in size usually show why here.
`roots.includedFromOutside` counts headers in the carve root that code outside it `#include`s (found through the
build log's outside compiles and the build trace's outside files); they are kept whole in every stage, since the
rest of the build compiles against them, and so are the root headers they include in turn.
`roots.namedFromOutsideHeaders` counts the module functions and variables those headers name (prototypes, `extern`
data, macros that expand to either): they are rooted, so outside code still links in every stage. Both are 0 when
the build has no code outside the root. `outsideIncludes.filesRead` counts the files followed to find them;
`outsideIncludes.capped = True` means the scan stopped at its limit and may have missed some (a warning says so).

## When `verify` fails: `codecarver/debug/`

A failed `verify` (exit 3) writes every failure up, with no flag, in `<outputDirectory>/[<stage>/]codecarver/debug/`:

```
debug/
  raw/cases.txt   per failure, in your names: the use and the function around it, the code around the use and the
                  definition, the #if lines open at each and how the #if model read them, every file that defines the
                  name and whether the build log compiled it, the build trace opened it, it was parsed and it was kept,
                  and every graph node with the name. KEEP THIS LOCAL.
  raw/key.txt     which anonymized name is which of yours, to read an answer that talks about k12 or p3.c. KEEP LOCAL.
  anon/           the same cases ANONYMIZED, each caseN/ with a replayable copy of the files involved
  anon.zip        anon/ zipped: the thing to send
```

Cases that include the same headers share them: each distinct file is stored once in `anon/store/`, and
`caseN/files.tsv` lists where each goes. `anon/unpack.ps1` rebuilds the case trees.

**What the anonymized copy holds.** The files a failure involves (the use, every definition, the headers they
include, and SDK or configure headers outside the tree the build opened), their compile commands and build trace
entries, and a `carve.toml`, all rewritten: every word of every name, file and folder becomes an opaque word of the
same case (`uart_init` becomes `k1_k2`, `uart_send` becomes `k1_k3`, `drivers/uart.c` becomes `p4/p5.c`), string
contents are rewritten the same way, comments become blank, and numbers above 16 become ordered stand-ins (17, 18,
...). What stays is the *shape*: keywords, punctuation, `#if` structure, line numbers, file extensions, and
compiler, attribute and standard-library names (`static`, `__attribute__`, `weak`, `printf`, `uint32_t`). So it is
not your source, but it is your code's structure. **Look at `anon.zip` before sending it** if structure is sensitive.

Every file is checked before it is written: one that still holds a word from the original is left out (the case
says how many), never written.

**Replay.** CodeCarver carves each anonymized copy right away and records in `cases.txt` whether the same failure
happens there (`replay: yes`). A `yes` means the copy alone reproduces the bug: a developer can fix it from the zip
without ever seeing your code. Counts are in `summary.txt` (`<stage>.debug.cases`, `<stage>.debug.reproduced`).

## When the carved tree doesn't build: `build-output.txt`

`verify` checks functions and variables only; a missing header, type or macro shows up when you build the carved tree. Save
everything that build printed as `<outputDirectory>/<stage>/build-output.txt` (beside that stage's `carved/`;
`<outputDirectory>/build-output.txt` with no stages) and run the same carve again. CodeCarver reads the errors (gcc,
clang, MSVC, Keil armcc/armclang, IAR, and the GNU, LLVM, Microsoft, Arm and IAR linkers) and writes them up exactly
like verify failures, in `codecarver/debug-build/`: for each name the build missed, where the original tree defines it
and why the carve dropped it, raw (`raw/`, keep local) and anonymized (`anon.zip`, to send). The compiler's own words
stay in the anonymized error lines; every name and path in them is rewritten. A replay counts as reproduced when the
anonymized copy's carve drops every file that defines the name (or the missing header) too.

## Checking a fix: `tools\selfcheck\selfcheck.ps1`

The release ships a self-check that carves tiny made-up trees, one for each definition shape a past evaluation
found missed (out-of-line C++ methods in nested scopes, K&R definitions with and without parameter declarations,
implicit-int definitions, a function head split across `#ifdef`, `#if/#else` inside a parameter list, a
macro-defined function, a registration macro verify misread as a function), and checks each carve kept what `main`
needs. It uses none of your
source and takes seconds:

    powershell -ExecutionPolicy Bypass -File tools\selfcheck\selfcheck.ps1

It prints `PASS`/`FAIL` per case and exits 0 only when all pass. The PASS/FAIL lines are safe to send back.

## If it crashes

You don't have to do anything special. On an unhandled exception CodeCarver writes a **source-free**
diagnostic package to a temp path and prints it:

```
diag    : diagnostic package written -> C:\Users\...\Temp\CodeCarver_diag_<id>.zip
          it contains no source (only what the tool did) — send this file to report the bug.
```

Send that `.zip`. It carries version, environment (OS, CPU cores, RAM, source-drive free/total —
relevant to out-of-memory failures on huge headers), the command line with option values elided, phase
timings, counts, warnings **as counts per category** (not their text), and the failure itself. Free text such as
the exception message is scrubbed: quoted text, relative paths, file names, and this run's source, output,
config and input paths and entry-point names are removed, as is your home-directory path. It never contains
source contents, secrets or environment variables. The package is written only for an unhandled exception; a
configuration error (exit 2) or a failed `verify` (exit 3) prints its reason instead.

## If the output is wrong (no crash)

Every carve writes a `codecarver/` folder next to the carved tree — no flags needed:

```
<outputDirectory>/[<stage>/]codecarver/
  summary.txt / .json  numbers only — safe to send (above)
  report.txt           human-readable: roots, kept/dropped files by bucket, sizes, observed-file tags
  manifest.json        the same decision, structured (kept/dropped files, droppedCmm, stats, observed files)
  decisions.txt        per-SYMBOL keep/drop — every function/type/global/macro, KEPT or CARVED, and why
  verify.txt           the emitted-tree link check: each FAIL / #ifdef-dead use and where
  repro.graph.json     the dependency graph, fully ANONYMIZED (see below)
  resolved-config.toml the configuration actually used (selected builds/runs merged, paths resolved)
```

The `verify` and `world` results are printed in the run output (and counted in `summary.txt`); the `#ifdef`
world is also the header comment of `resolved-config.toml`.

`decisions.txt` is usually the fastest way to answer "why is this still here / why did that
disappear?" across the whole tree at once — one line per symbol, and for a kept symbol a short
chain back to the root that pulled it in. For a single symbol, `codecarver carve <dir> --config carve.toml
--why <symbol>` prints just that chain. Above 500,000 graph nodes `decisions.txt` holds only a short note;
use `--why` then.

> `report.txt`, `manifest.json`, `decisions.txt`, `verify.txt` and `resolved-config.toml` include your file and
> symbol **NAMES** and paths (never file *contents*). That's fine for your own debugging; **review them before
> sending** if identifiers are sensitive — or send only `summary.txt` and `repro.graph.json`.

## The `verify` check

Every C/C++ carve checks the tree it **emitted**, with its own tokenizer and without using the carve's graph:
if emitted code uses a function or file-scope variable that only a **dropped** file defines, the tree would not link, the `verify`
line says `FAILED` with the name and where it is used, and the run exits **3** so it can gate CI. A use on
an `#ifdef`-dead line is a note, not a failure. The full list is in `codecarver/verify.txt`. `verify` cannot see
a missing type, macro or header — if a carved tree won't build but `verify` is OK, the compiler's first error is
the lead.

## `repro.graph.json` — the anonymized, safe-to-share bundle

Written on every carve (no flag). It's the dependency graph the carve ran over, with **every symbol name
and file path replaced by an opaque token** (`s0`, `f3.c`, …) that has no way back to the original — only
the file *extension* is kept, because carve behavior depends on it. It carries no source, no real names,
no paths: just the structure (nodes, edges, roots, and the set that was kept). The extension is copied as-is,
so glance at it if your tree uses unusual, project-specific extensions.

This is the most useful artifact for "it kept/dropped the wrong thing" bugs — a developer can replay reachability on the anonymized graph and reproduce your
exact result on their machine, with none of your IP. It's streamed straight to disk, so it's produced at
any graph size (the run output reports its byte size).

## Reporting a bug

Include: the CodeCarver version, the exit code, what you expected, what happened, `summary.txt`, and
`repro.graph.json` (anonymized). Your `carve.toml` has no source in it, but it does name paths and symbols —
send it only if that's acceptable. If you're fine sharing names, the relevant `codecarver/` artifacts help too. For a wrong keep/drop, the anonymized graph alone usually reproduces it; the `decisions.txt`
line for the symbol in question (plus its `--why` chain) pins it down in your own names.
