# CodeCarver — Getting Help

If a carve misbehaves — wrong output, a crash, a tree that won't build — you can hand a developer
enough to diagnose it **without sharing a single line of your source**. Everything below is either
source-free or made of your own names (never file *contents*), and you decide what to send.

## If it crashes

You don't have to do anything special. On an unhandled failure CodeCarver writes a **source-free**
diagnostic package to a temp path and prints it:

```
diag    : diagnostic package written -> C:\Users\...\Temp\CodeCarver_diag_<id>.zip
          it contains no source (only what the tool did) — send this file to report the bug.
```

Send that `.zip`. It carries version, environment (OS, CPU cores, RAM, source-drive free/total —
relevant to out-of-memory failures on huge headers), the sanitized command line, phase timings,
counts, and the failure itself. It never contains source contents, secrets, environment variables,
or your home-directory path (those are redacted).

## If the output is wrong (no crash)

Every carve writes a `codecarver/` folder next to the carved tree — no flags needed:

```
<outputDirectory>/codecarver/
  report.txt           human-readable: roots, kept/dropped files, sizes, the verify result, the #ifdef world used
  manifest.json        the same decision, structured (kept/dropped files, stats, observed files)
  decisions.txt        per-SYMBOL keep/drop — every function/type/global/macro, KEPT or CARVED, and why
  repro.graph.json     the dependency graph, fully ANONYMIZED — the safe-to-share artifact (see below)
  resolved-config.toml the exact config the run resolved to
```

`decisions.txt` is usually the fastest way to answer "why is this still here / why did that
disappear?" across the whole tree at once — one line per symbol, and for a kept symbol a short
chain back to the root that pulled it in. For a single symbol, `carve <dir> --config carve.toml
--why <symbol>` prints just that chain.

> `report.txt`, `manifest.json`, and `decisions.txt` include your file and symbol **NAMES** (never file
> *contents*). That's fine for your own debugging; **review them before sending** if identifiers are
> sensitive — or just send `repro.graph.json`, which is fully anonymized (see below).

## The `verify` check

A compiler-free soundness check runs on **every** carve (C/C++) and is folded into `report.txt` and
the run output: it flags any kept function that calls an in-scope function that was carved out (which
wouldn't link). If a carved tree won't build, read the `verify` line first — a non-empty result points
straight at the broken edge, and the run exits non-zero so it can gate CI.

## `repro.graph.json` — the anonymized, safe-to-share bundle

Written on every carve (no flag). It's the dependency graph the carve ran over, with **every symbol name
and file path replaced by an opaque token** (`s0`, `f3.c`, …) that has no way back to the original — only
the file *extension* is kept, because carve behavior depends on it. It carries no source, no real names,
no paths: just the structure (nodes, edges, roots, and the set that was kept).

This is the most useful artifact for "it kept/dropped the wrong thing" bugs **and the one you can send
without any review** — a developer can replay reachability on the anonymized graph and reproduce your
exact result on their machine, with none of your IP. (On a very large graph the bundle is skipped with a
note in the output, to avoid a memory spike; a streaming writer that lifts that cap is queued.)

## Reporting a bug

Include: what you ran (your `carve.toml` — it has no source in it), what you expected, what happened,
and `repro.graph.json` (anonymized) or, if you're fine sharing names, the relevant `codecarver/`
artifacts. For a wrong keep/drop, the anonymized graph alone usually reproduces it; the `decisions.txt`
line for the symbol in question (plus its `--why` chain) pins it down in your own names.
