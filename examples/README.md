# Examples

Worked examples of CodeCarver — inputs and carved outputs checked in, so you can see exactly what the
tool does before running anything. Each example with a `carve.toml` runs with `codecarver carve src --config
carve.toml` from its own directory (or its `run.ps1` / `run.sh`), and CI carves every one of them.

| Example | What it shows |
|---|---|
| [`multistage-firmware/`](multistage-firmware) | **The whole process, end to end.** A Cortex-M-style image carved at three stages (safe / aggressive / max) driven entirely by a `carve.toml` — build log (pins the `#ifdef` world), observed build/run traces, a `.cmm` loader, dead-code drop, an excluded board variant, auto-excluded junk, an implicit ISR root, and the per-stage `carved/` + `codecarver/` layout. |
| [`mixed-cpp-firmware/`](mixed-cpp-firmware) | C + C++ in **one graph** (C → C++ → C calls across an `extern "C"` bridge, a virtual call, a template), with the **union** of two builds and two runs, and a `.cmm` loader. |
| [`csharp-app/`](csharp-app) | A tiny C# console app carved **file-level**: an unreferenced `.cs` file is dropped, and the carved project still builds and runs. |
| [`cmm-trace/`](cmm-trace) | **Trace-seeded TRACE32 `.cmm` tightening.** A run trace that opened one flash script seeds a `DO`/`GOSUB` closure; with `dropUnobservedCmm = true` the scripts outside it are dropped, including one reachable only through a dropped script's dynamic `DO &var`. Shows C dead-code drop and `.cmm` tightening in one carve. |
| [`stringlib/`](stringlib) | A small multi-feature C library carved two ways (encode-only; input-sanitizer). Demonstrates file-level dropping, intra-file function pruning, data-table pruning, and the size report — with the carved output checked in for each scenario. No `carve.toml`: the README shows the two configs. |

Two directories are **test fixtures**, not walkthroughs (no README or config):

- [`cortexm-firmware/`](cortexm-firmware) — a bare-metal Cortex-M source tree (vector table, weak-alias
  handlers, a `KEEP()`'d registration section, a linker script) that the build-verify tests and
  `carve-build-report.ps1` carve and link with `arm-none-eabi-gcc`.
- [`tiny-firmware/`](tiny-firmware) — three loose files (`main`/`sensor`/`debug`), the smallest "drop an
  unused module" shape.

`forceKeepFiles` (keep a file the carve can't see it needs — a prebuilt, a generated file, a file loaded by
reflection) is shown as a variant in the multistage README.

## Carving a real third-party repo

The examples are self-contained. To see CodeCarver on real code, fetch the corpus and use the exact
invocations in [`../docs/REPRODUCE.md`](../docs/REPRODUCE.md) — e.g. cJSON, fmt, simdjson, pugixml. Those
repos aren't checked in here (they're pulled by `../fetch-corpus.ps1`).
