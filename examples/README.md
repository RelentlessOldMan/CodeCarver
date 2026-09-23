# Examples

Worked examples of CodeCarver — inputs and carved outputs checked in, so you can see exactly what the
tool does before running anything.

| Example | What it shows |
|---|---|
| [`stringlib/`](stringlib) | **Start here.** A small multi-feature C library carved two ways (encode-only; input-sanitizer). Demonstrates file-level dropping, intra-file function pruning, data-table pruning, and the size report — with the carved output checked in for each scenario. |
| [`cortexm-firmware/`](cortexm-firmware) | A bare-metal Cortex-M image (vector table, weak-alias handlers, a `KEEP()`'d registration section, a linker script). Shows the embedded story: implicit roots, and emitting the `.ld` so the carved tree links with `arm-none-eabi-gcc`. Exercised by the build-verify tests. |
| [`tiny-firmware/`](tiny-firmware) | The minimal three-file demo (`main`/`sensor`/`debug`) — the smallest "drop an unused module" carve. |

## Carving a real third-party repo

The `stringlib` example is self-contained. To see CodeCarver on real code, fetch the corpus and use the
exact invocations in [`../docs/REPRODUCE.md`](../docs/REPRODUCE.md) — e.g. cJSON (99 files → 2), fmt,
simdjson, pugixml. Those repos aren't checked in here (they're pulled by `../fetch-corpus.ps1`), but the
commands and expected results are documented.
