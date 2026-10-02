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
  resolved-config.toml the exact config the run resolved to
```

`decisions.txt` is usually the fastest way to answer "why is this still here / why did that
disappear?" across the whole tree at once — one line per symbol, and for a kept symbol a short
chain back to the root that pulled it in. For a single symbol, `carve <dir> --config carve.toml
--why <symbol>` prints just that chain.

> **These artifacts include your file and symbol NAMES** (never file *contents*). That's fine for
> your own debugging; **review `decisions.txt` / `manifest.txt` before sending them** if identifiers
> are sensitive. A fully anonymized, name-free share path is tracked below.

## The `verify` check

A compiler-free soundness check runs on **every** carve (C/C++) and is folded into `report.txt` and
the run output: it flags any kept function that calls an in-scope function that was carved out (which
wouldn't link). If a carved tree won't build, read the `verify` line first — a non-empty result points
straight at the broken edge, and the run exits non-zero so it can gate CI.

## Reporting a bug

Include: what you ran (your `carve.toml` — it has no source in it), what you expected, what happened,
and either the crash `.zip` or the relevant `codecarver/` artifacts. For a wrong keep/drop, the
`decisions.txt` line for the symbol in question (plus its `--why` chain) is usually enough to reproduce.

## Known gap — the anonymized share bundle

An earlier build could emit `repro.graph.json`: the full dependency graph with **every symbol and path
replaced by an opaque token** (no reverse mapping) — the ideal "safe to share, zero IP" artifact for
reproducing a wrong keep/drop off-box. The engine that produces it still exists, but the lean CLI
dropped the flag that requested it, so today the shareable artifacts are the name-bearing ones above.
Re-exposing the anonymized bundle (as a `--diag` option or a config switch) is the open design item;
until then, prefer the crash `.zip` (source-free) and redact names from `decisions.txt` before sharing.
