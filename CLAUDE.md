# CodeCarver — project instructions

## ComputeWarden

Follow the global ComputeWarden policy (shared claims for everyday and heavy jobs alike; `exclusive` only with the user's express approval for that job; `solo` for timing measurements). Project specifics:
Owner e.g. `codecarver`. Other sessions on this box (CodeCompass, CodeSpawner) coordinate through the same
daemon, so this keeps us from all pegging the CPU/disk at once.

**Heavy jobs (`kind: "shared"`; `exclusive` only with the user's express approval for that job)** — they peg most/all cores or sustain heavy disk I/O, any duration:
- Ground-truth oracle at scale — `carver-groundtruth-oracle.ps1` against the ~100 GB `_death` corpus
  (local `C:\Playground\CodeCompass\.corpus\_death` or its copy on the network share):
  `cpu, disk` locally, `cpu, network` against the share.
- Synthetic corpus generation — `tools\codespawner\codespawner.exe gen --preset death` (~90 GB write),
  and any large `make`/corpus build: `cpu, disk`.
- Build sweeps — `carve-build-report.ps1`, `corpus-compile-sweep.ps1`, `cpp-oracle-sweep.ps1`, and the WSL
  oracles `wsl-*-oracle.sh` (dozens of gcc/g++/arm-none-eabi compiles): `cpu`.

**Timing measurements (`kind: "solo"`):** timed A/B carves and benchmarks — the only time we need the machine
to ourselves. Acquire right before the measured run, release as soon as it ends.

**Everyday (shared claims):** a single `dotnet build`, a single carve of a small tree, and the routine full
`dotnet test` (~2 min). Edits and reads/searches need no claim.
