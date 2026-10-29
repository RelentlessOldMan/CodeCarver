# CodeCarver — project instructions

## Resource needs
- Carves cap their own memory with `[advanced] memoryStopMb` (local runs use 4000); the ground-truth oracle and build sweeps run carves.
- An A/B carve of the synthetic 546 MB tree takes ~5-6 min; a death-scale carve ~2.5 min from a local disk, ~12 min over a network share.
- Every carve writes a full carved copy of its tree, and a T2 run carves a corpus of millions of defines: scratch output from A/B rounds and T2 runs reached 400 GB.

## Scratch carve output
- A/B and T2 scripts delete each run's output dir as soon as they have read its summary keys into the results file; the results file is all a round needs.
- Before starting a new A/B round or T2 batch, delete the previous round's output dirs, logs and configs.
- In the session scratchpad keep only the A/B input trees, the snapshot of the latest release (the next A/B's baseline) and the current scripts. Delete older exe snapshots, traces (.nettrace, .etlx) and heap dumps (.gcdump) once their numbers are reported.
- At the end of a task, and before a long session ends, check the scratchpad's size and clean it to that keep-list.
