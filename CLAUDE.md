# CodeCarver — project instructions

## ComputeWarden: gate intensive work

This project opts **IN** to ComputeWarden. Before starting a machine-saturating operation, call
`computewarden_acquire` (owner e.g. `codecarver`, a short description, a `lease_seconds` covering the
expected duration). If it is not acquired — or status is `UNKNOWN` — do **not** start: report the blocker
and ask whether to wait/retry or proceed. Call `computewarden_release` when done (crash-safe via lease
expiry). Other sessions on this box (CodeCompass, CodeSpawner) coordinate through the same gate, so this
keeps us from all pegging the CPU/disk at once.

**Gate these** (peg most/all cores or sustain heavy disk I/O — any duration):
- Ground-truth oracle at scale — `carver-groundtruth-oracle.ps1` against the ~100 GB `_death` corpus
  (local `C:\Playground\CodeCompass\.corpus\_death` or the `\\IRISH\TestHole\death` share).
- Synthetic corpus generation — `tools\codespawner\codespawner.exe gen --preset death` (~90 GB write),
  and any large `make`/corpus build.
- Build sweeps — `carve-build-report.ps1`, `corpus-compile-sweep.ps1`, `cpp-oracle-sweep.ps1`, and the WSL
  oracles `wsl-*-oracle.sh` (dozens of gcc/g++/arm-none-eabi compiles).

**Do NOT gate**: normal edits, a single `dotnet build`, a single carve of a small tree, reads/searches.
The routine full `dotnet test` (~2 min) is **borderline** — gate it only when another heavy job is likely
running concurrently on the box.
