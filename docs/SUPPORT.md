# CodeCarver — Getting Help

If a carve misbehaves — wrong output, a crash, a tree that won't build — you can hand a developer
everything needed to diagnose it **without sharing a single line of your source**. CodeCarver collects a
source-free diagnostic package for you; you don't need to write your own scripts.

## The one thing to run

```
carve <dir> --roots <a,b,...> [your usual flags] --diag report.zip
```

This runs the carve normally **and** writes `report.zip`. Send that file. That's it.

If the tool **crashes**, you don't even need `--diag`: a package is written automatically and its path is
printed, e.g.

```
diag    : diagnostic package written -> C:\Users\...\Temp\CodeCarver_diag_<id>.zip
          it contains no source (only what the tool did) — send this file to report the bug.
```

## What's in the package (and what is never in it)

Open `manifest.txt` inside the zip — it describes itself. In short:

**Included** (all of it describes what the *tool* did, not your code):
- `summary.txt` — human-readable overview: version, environment, parameters, timings, warnings, any failure.
- `diagnostics.json` — the same information, structured.
- `manifest.txt` — a self-describing list of contents.

**Never included, by design:**
- Source file **contents** — proprietary, never collected.
- Environment variables, secrets, credentials.
- Your home-directory path / username — redacted to `%USERPROFILE%` / `$HOME`.

## Optional deeper artifacts

Two opt-in flags add more, for harder bugs. Both still exclude source **contents**.

### `--diag-repro` — anonymized, replayable graph (safe to share)

```
carve <dir> --roots ... --diag-repro
```

Attaches `repro.graph.json`: the dependency graph the carve ran over, with **every symbol name and file
path replaced by an opaque token** (`s0`, `f3.c`, …) that has no way back to the original. It carries no
source, no real names, no paths — just the structure (nodes, edges, roots, and the set that was kept).

This is the most useful artifact for "it kept/dropped the wrong thing" bugs: a developer can *replay* the
reachability on the anonymized graph and reproduce your exact result on their machine, with none of your IP.

### `--diag-verbose` — per-file keep/drop table (includes NAMES)

```
carve <dir> --roots ... --diag-verbose
```

Attaches `keepdrop.txt`: which files/nodes were kept vs dropped and **why** (the keep-reason histogram),
plus unresolved roots and files kept whole. Unlike everything above, this **does include file and symbol
NAMES** (still never file *contents*). The manifest flags this prominently. Use it when names are not
sensitive, or review the file before sharing. Omit the flag to exclude names.

If you pass `--diag-repro` or `--diag-verbose` without `--diag`, the package is written to a temp path and
the location is printed.

## Also handy

- `--manifest out.json` — writes the full keep/drop decision as JSON next to your carve (names included;
  for your own inspection, not anonymized).
- `--why <symbol>` — prints the chain explaining why one symbol survived the carve.
- `--verify` — a compiler-free soundness check (no kept function calls an in-scope function that was carved
  out). A good first thing to run if a carved tree won't link.

## Reporting a bug

Include: what you ran (the command line), what you expected, what happened, and the `report.zip`. With
`--diag-repro` attached, most carve-correctness issues can be reproduced without any further back-and-forth.
