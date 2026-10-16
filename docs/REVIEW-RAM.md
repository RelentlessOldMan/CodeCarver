# RAM review (wasted memory)

Review of CodeCarver against the wasted-RAM review specification, on 1.0.283. Measured on local synthetic and public
trees only, each carve at low priority with memoryStopMb = 4000. Labels: CONFIRMED = measured or traced here;
HYPOTHESIS = consistent with the evidence, not reproduced.

## A. Executive summary

- Most of a run's peak is the managed heap's headroom and the parse's native heap, not live data. On mactree (1.6M macro
  nodes) the live heap after the graph build was 0.58 GB, the managed heap 1.44 GB, and private memory 1.69 GB. The GC
  is already tuned (Server GC + DATAS, ConserveMemory 7, a compaction after the graph build).
- The link check was the largest avoidable peak: on regtree (65 register headers, 228 MB) its dropped-files loop peaked
  at 2.7 GB. Three fixes (M1-M3) bring the run's peak to 1.47 GB (-46% against the 1.0.283 baseline) and the check runs
  faster too.
- M3 matters most for work: every memory budget (parse, macro pre-pass, check scans, held header text) was a fraction
  of the MACHINE's memory, whatever memoryStopMb said. On a large work machine the check's scans were sized for many
  times the stop. That is the likeliest cause of C19 (stopped 119 files into the dropped-files loop at 21 GB private, 19
  GB managed).
- The C20 spike (11.7 -> 21.3 GB in 90 s, emitted-files loop) is most likely the quadratic #define join that 1.0.241
  removed (14 GB allocated in that loop, now 0.9 GB): short-lived strings that size go to the large object heap, which
  only a full collection frees (M4, HYPOTHESIS; C21's stop.* keys tell if it is not).

## B. Memory architecture overview

| Phase | What it holds | Bounded by |
|---|---|---|
| walk / inputs / defines | file list, build-log commands, per-TU define tables | tree size |
| pre-parse + build-graph | tree-sitter trees (native), macro pre-pass, the graph | ParseMemory (budget/4), pre-pass gate (budget/8) |
| roots / reachability / precision | the graph + keep sets | graph size |
| emit | streamed copy | ReadAhead window |
| check (headers, dropped, pruned, emitted) | per-file scans in flight, header text held, results ahead | ScanBudget (budget/512 bytes of file), scan gate (budget/8), HeaderTextBudget (budget/32), ReadAhead ahead = 512 |

"budget" was the machine's memory; since M3 it is the machine's or memoryStopMb, whichever is less.

### Baseline (1.0.283, MB: phase peak / allocated)

| phase | T | w | L | regtree | mactree | tmpltree | longmac | errtree | bigtree |
|---|---|---|---|---|---|---|---|---|---|
| build-graph | 37/12 | 168/1196 | 919/1618 | 68/0 | 1548/6789 | 47/19 | 77/1754 | 1366/4802 | 643/1395 |
| roots | 37/0 | 181/32 | 1426/854 | 68/0 | 1549/0 | 48/0 | 47/0 | 1296/0 | 688/42 |
| check (stage 0) | 40/4 | 213/332 | 1569/617 | 2698/8705 | 1481/681 | 403/1577 | 114/909 | 1365/0 | 991/1166 |
| run peak private | 43 | 238 | 1630 | 2699 | 1602 | 403 | 114 | 1366 | 993 |
| graph nodes | 1,482 | 71,428 | 153,278 | 66 | 1,612,001 | 833 | 602 | 10 | 7,019 |

## C. Ranked findings

| ID | Finding | Label | Peak effect | Status |
|---|---|---|---|---|
| M3 | Memory budgets sized from the machine, not memoryStopMb | CONFIRMED | regtree 2217 -> 1465 MB; scales with machine size | fixed |
| M1 | Check tokenizer list presized at a token per 4 chars | CONFIRMED | 227 MB of headers -> ~900 MB of list for 10 tokens | fixed |
| M2 | Check reads via File.ReadAllText (text held twice, 4 KB reads) | CONFIRMED | -10..30% check allocation | fixed |
| M4 | C20 emitted-loop spike = large-object garbage of the quadratic join | HYPOTHESIS | 9.6 GB at work | fixed in 1.0.241; C21 confirms |
| M5 | GC headroom: heap ~2.5x live data | CONFIRMED | mactree 1.44 GB heap for 0.58 GB live | knob documented, no default change |
| M6 | A define table per logged TU, identical ones not shared | HYPOTHESIS | ~+13 MB on L; work: TUs x specs | deferred |
| M7 | LineMaps clones the define table per file scanned | CONFIRMED (alloc) | small per file | deferred |
| M8 | DefinerMacros.FromText per dropped file | CONFIRMED (alloc) | regtree 0.9 GB allocated, transient | deferred |

Cut line: M1-M3 ship now; M5 as documentation. Below the line: M6-M8 (transient or small on every measured tree).

## D. Detailed findings

**M3 - budgets ignore memoryStopMb.** `MemoryGate.MachineBytes()` fed ParseMemory (1/4), the pre-pass and check scan
gates (1/8), ScanBudget (1/512 of file bytes in flight; a scan holds ~35x its file), ReadBudget and HeaderTextBudget
(1/32). A run told to stop at 20 GB on a 128 GB machine let the check's scans hold ~9 GB at once. Fix:
`MemoryGate.RunLimitBytes` (set from memoryStopMb at the start of a run) and `BudgetBytes()` = the lesser, used by every
budget. Budgets only decide when work runs, never what it finds (the check's results are unchanged; a header not held is
read again). Cost: with a low stop the parse runs fewer large files at once (mactree build-graph 29.4 -> 33.6 s in one
noisy run, L unchanged).

**M1 - token list presize.** `EmittedLinkCheck.Tokenize` sized its list at `s.Length / 4` Toks (16 B each): 8 bytes per
byte of file, twice the text string. Measured chars per token: regtree 23.9M (directive-only), mactree 98, L 14, bigtree
7, so the list was 1.8x-6M x larger than needed. Now `min(len/8, 65536)` and grown by doubling.

**M2 - check reads.** `TryRead` used `File.ReadAllText(Latin1)`: a StreamReader's char buffer and StringBuilder chunks
next to the string (2x the text for a large file) in 4 KB reads. Now `IoMeter.ReadAllText`, which reads small files into a
pooled buffer and, new, decodes a large Latin-1 file straight into its string (no builder) unless a byte-order mark says
otherwise.

**M4 - C20.** See A. If C21 stops again in the emitted loop, `stop.inHandKb` / `stop.largestFileInStepKb` separate data
in flight from heap headroom.

**M5 - GC headroom.** ConserveMemory 9: peak -16% (mactree 1841 -> 1547) and -12% (L) but build-graph +23..50%; gen0size
16 MB: no gain. Left at 7; `DOTNET_GCConserveMemory=9` is the knob for a memory-tight machine (USAGE).

**M6-M8.** Small or transient on every measured tree; revisit if a work stop names the defines phase or the dropped loop.

## E. Quick wins

M1, M2 (shipped).

## F. Major architectural opportunities

The graph's string-keyed node index (66 MB entry array for 1.6M nodes) and per-node strings are the retained floor; an
interned/int-keyed index would cut it, at the cost of touching every graph consumer. Not attempted.

## G. Memory savings estimate

Check-heavy trees: -20..46% run peak (regtree, bigtree); graph-heavy trees: ~0 (the floor is the graph + GC room). At
work, M3 bounds the check's in-flight scans by the stop instead of the machine.

## H. Validation and benchmark plan

prof.ps1 (scratch): one low-priority carve per tree, collecting mem.*, check.*, time.* from the summaries; allocation
traces with dotnet-trace (GC AllocationTick) grouped by first CodeCarver frame; dotnet-gcdump after the graph build for
live data. Peaks vary +-10% run to run with GC timing: a fix is judged by allocation and live data, peak as a check.

## I. Coverage report

Profiled: walk, inputs, defines, pre-parse, build-graph, roots/reachability/precision, emit, all four check loops, on
nine trees. Audited by reading: EmittedLinkCheck (read, tokenize, gates, ReadAhead), IoMeter, MemoryGate/ParseMemory,
MacroTable/LineMaps, per-TU define tables. Not audited line by line: the CLI's emit/stage plumbing, runtime traces.

## J. Remaining unknowns

C20's spike (M4) not reproduced locally. Work-scale graph floor (3M macro nodes) estimated, not measured.

## K. Fixes applied

| ID | Version | Test |
|---|---|---|
| M1, M2, M3 | (this commit) | IoMeterTests.LargeLatin1Reads_DecodeExactlyAsFileDoes; StreamingCheckTests.Budgets_AreFractionsOfTheRunLimit_WhenItIsLessThanTheMachine; full suite |

Measured (run peak private MB, 1.0.283 -> with M1-M3): regtree 2699 -> 1465, bigtree 993 -> 971, L 1630 -> 1560,
mactree 1602 -> 1841 (GC noise: same live data, check allocation -31%).

## L. Changes to documented behavior

memoryStopMb now also caps the memory budgets (parse, pre-pass, check), not only the stop.
