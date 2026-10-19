# CodeCarver — project instructions

## ComputeWarden

Follow the global policy; owner `codecarver`.
- Memory: carves cap themselves with `[advanced] memoryStopMb` (local runs use 4000), so they are shared,
  never bigMemory. That covers the ground-truth oracle and the build sweeps (they run carves).
- Solo: a timed A/B carve (one carve per claim, ~5-6 min on the synthetic 546 MB tree).
