# CodeCarver — Getting Help

If a carve misbehaves — wrong output, a crash, a tree that won't build — you can hand a developer
enough to diagnose it **without sharing a single line of your source**. Everything below is either
source-free or made of your own names (never file *contents*), and you decide what to send.

## The first thing to send: `summary.txt`

Every carve writes `<outputDirectory>/[<stage>/]codecarver/summary.txt` and the same data as `summary.json`.
They hold **numbers, booleans and fixed category names only** — the `#ifdef` world, graph size, roots by kind,
trace counts, `.cmm` counts, per-stage sizes and `verify` counts, warnings by category, the exit code. No path,
file name or symbol appears in them (a test enforces this), so they can be sent back as-is when the source must
stay on its machine. Send it together with the exit code and the CodeCarver version.

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
if emitted code uses a function that only a **dropped** file defines, the tree would not link, the `verify`
line says `FAILED` with the function and where it is used, and the run exits **3** so it can gate CI. A use on
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
