# Example: tightening TRACE32 `.cmm` scripts with a run trace

A real firmware tree carries hundreds or thousands of TRACE32 PRACTICE (`.cmm`) scripts — flash sequences,
board bring-up, test harnesses. They're **orchestration, not linked code**, so CodeCarver keeps them as
infrastructure. But a given image's run only ever touches a few of them. This example shows how a **run
trace** lets CodeCarver keep just the scripts that run (plus everything they `DO`), and — when you opt in
with `dropUnobservedCmm = true` — drop the rest.

Both the input (`src/`) and the carved result (`carved/`) are checked in, and a test regenerates and
byte-diffs them so they can never drift from the tool.

```
src/
  main.c          -> driver.c        (dead.c is unreachable C)
  Makefile
  scripts/
    flash.cmm       the flash sequence the run executes  (DO init; GOSUB Program -> DO write_image)
    init.cmm        DO common
    write_image.cmm DO common
    common.cmm      shared leaf
    debug_dump.cmm  a maintenance script no run touches; it does a dynamic `DO &board`
    board_rev_a.cmm a board variant, reachable only through debug_dump.cmm's dynamic `DO &board`
```

## The input

`inputs/run.trace` records what a real flash/run session opened — here, just `scripts/flash.cmm`. (You
capture this with ProcMon / strace; see [`tools/capture`](../../tools/capture).) The carve roots the C from
`main`, and seeds the `.cmm` closure from the observed `flash.cmm`.

## The result

```
carve src --config carve.toml
```

```
nodes   : 4/6 kept, 2 carved           (the C: main + driver kept, never_used dropped)
files   : 2/3 kept, 1 dropped           dead.c
cmm     : 4/6 script(s) kept (1 observed + 3 via DO/GOSUB closure), 2 dropped
verify  : OK
```

What happened, visible in [`carved/`](carved):

- **`flash.cmm` kept** — the run opened it (the seed).
- **`init.cmm`, `write_image.cmm`, `common.cmm` kept** — the static `DO`/`GOSUB` closure from `flash.cmm`:
  `flash → init` (`DO init`), `flash.Program → write_image` (a `DO` *inside* a subroutine still counts — the
  closure is file-level), and both `init` and `write_image` `DO common`.
- **`debug_dump.cmm` dropped** — nothing `DO`s it and no run opened it.
- **`board_rev_a.cmm` dropped** — only `debug_dump.cmm`'s dynamic `DO &board` could reach it, and that script
  is itself dropped.
- **The dynamic-`DO` rule:** had a *kept* script (say `init.cmm`) contained `DO &board`, CodeCarver could not
  know which scripts it runs, so it would drop **no** `.cmm` at all and say why.
- **`dead.c` dropped** — ordinary C dead-code removal, alongside the `.cmm` tightening, in one carve.

With **no** run trace, every `.cmm` is kept (CodeCarver can't prove which run — the sound default). With a
trace but **without** `dropUnobservedCmm = true`, every `.cmm` is still kept and the run reports how many the
trace and its closure account for: one run is one scenario, and a script another flash mode or menu uses would
otherwise be lost. The opt-in is what turns "keep all the scripts" into "keep the ones this image needs."

## Try it

```powershell
./run.ps1        # or ./run.sh — carves src/ into out/
```

Then compare `out/carved/scripts/` (what the trace proved you need) with `src/scripts/` (everything). The
dropped scripts are listed in `out/codecarver/manifest.json` under `droppedCmm`.
