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
- M3: every memory budget (parse, macro pre-pass, check scans, held header text) was a fraction of the MACHINE's
  memory, whatever memoryStopMb said. The work machine has 32 GB and stops at 20 GB, so the budgets were 1.6x the stop's
  share: real, but not by itself the cause of C19 (an earlier draft of this report assumed a 128 GB machine; wrong).
- The C20/C21 spike (12 -> 21 GB in 90 s, 347 files into the emitted-files loop, 0.5 MB of files in hand, 18 GB
  managed) is the template walk (M9, CONFIRMED locally by allocation; work run pending). A walk reads a file's whole
  include closure; a header the check could not hold (work held its 1 GB budget) is read again, a new string, so the
  parse cached per string missed and every walk re-parsed every header it reached, at 35x the header text, up to 64
  walks at once, none of it counted by the scan gate. Two fixes: the parse reads by hand (35x -> 6.5x), and walks that
  re-read take their measured cost from a walk budget (an 8th of the run limit). M4 (the 1.0.241 quadratic join) was
  WRONG: C21 ran on 1.0.283 and spiked the same way.

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
| M9 | Template walk re-parses every header not held, 35x its text, 64 walks at once, ungated | CONFIRMED (local alloc) | C20/C21: 12 -> 21 GB in 90 s | fixed: hand parse + walk budget |
| M4 | C20 spike = large-object garbage of the quadratic join | WRONG | - | C21 spiked the same on 1.0.283 |
| M5 | GC headroom: heap ~2.5x live data | CONFIRMED | mactree 1.44 GB heap for 0.58 GB live | knob documented, no default change |
| M6 | A define table per logged TU, identical ones not shared | HYPOTHESIS | ~+13 MB on L; work: TUs x specs | deferred |
| M7 | LineMaps clones the define table per file scanned | CONFIRMED (alloc) | small per file | deferred |
| M8 | DefinerMacros.FromText per dropped file | CONFIRMED (alloc) | regtree 0.9 GB allocated, transient | deferred |

Cut line: M1-M3 ship now; M5 as documentation. Below the line: M6-M8 (transient or small on every measured tree).

## D. Detailed findings

**M3 - budgets ignore memoryStopMb.** `MemoryGate.MachineBytes()` fed ParseMemory (1/4), the pre-pass and check scan
gates (1/8), ScanBudget (1/512 of file bytes in flight; a scan holds ~35x its file), ReadBudget and HeaderTextBudget
(1/32). A run told to stop at 20 GB on a 32 GB machine had budgets sized for 32. Fix:
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

**M9 - template walk (C20/C21).** Found by `Activity.Part` (allocation per part of a check loop's per-file work) and
`CODECARVER_DEBUG_ONLY=check-emitted` (the link check alone, in seconds). On tmpltree one walk that pulled in 27 headers
(28 MB of directive text) allocated 1,011 MB; the profile put 950 MB of it in `TemplateInstances.Parse`, 670 MB of that
the Match/Group objects of the Directive/Conditional/DefineHead regexes run on every #define. The parse is cached per
header string; a header past HeaderTextBudget is read again for each walk (a new string) and so parsed again for each
walk. Fixes: the parse reads each line in place by hand (same answers: TemplateParseTests runs the old regex parse
beside it), 1,011 -> 184 MB; once a header was not held, a walk takes from a walk budget (an 8th of the run limit,
`CODECARVER_CHECK_WALK_MB`) what the largest re-reading walk allocated, the first one running alone to size the rest.
Keys: `check.walkWaits`, `check.walkCostMaxMb`, and `check.<loop>.part.<part>.allocatedMb/itemMaxMb/items/pulledIn/pulledInMb`.

C22 (1.0.285, check-emitted alone) still stopped at 21.6 GB in 3 minutes: one walk pulled in 436 headers (402 MB) and
allocated 3.6 GB, up to 16 walks at once: the walk budget, which charges only walks that re-read headers, did not
hold them back. The parse was kept with each held header at about 5x its text (an Op per line with a copy of the line, the
name and body of every #define, a Macro, and a string-keyed index). Second fix: an Op is now only where the line is
(kind, lines, start, end); the name, include and macro are read again from the text when a walk asks for them, the
define index goes by the name's hash (no string per name). A first parse takes 2 bytes a char from the same budget
(`check.parseWaits`, part `templateParse`). Local repro (64 units over 120 register headers, 70 MB of #defines): the
walk that parses them 712 -> 197 MB (parse 58 MB, the rest the re-read text), 63 re-reading walks 8.6 -> 4.5 GB, check
56 -> 21 s; 64 units over different 64-header chains: walks 670 -> 157 MB, peak 507 -> 292 MB.

The ratio tests (TemplateWalkMemoryTests: allocation per char of header pulled in, held and read again) then found the
re-read path at 7.7 bytes a char: `DirectivesOnly` built each header again in a StringBuilder (chunks, then its
ToString). It now measures, writes the string once at its size, and returns a header that is all directive lines as it
is: 683 -> 322 MB for 89 M chars read again (the file's bytes, its string and its parse). Limits asserted: a first walk
2 bytes a char, a cached walk 0.1, the link check's walks 1.5 held and 4 read again (about 10 before these fixes).

**M4 - C20 = the quadratic join. WRONG.** C21 ran on 1.0.283 (join fixed) and spiked the same way.

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

M9 is confirmed by allocation on a local tree, not yet by a work run (CODECARVER_DEBUG_ONLY=check-emitted on the work tree says). Work-scale graph floor (3M macro nodes) estimated, not measured.

## K. Fixes applied

| ID | Version | Test |
|---|---|---|
| M1, M2, M3 | 1.0.284 | IoMeterTests.LargeLatin1Reads_DecodeExactlyAsFileDoes; StreamingCheckTests.Budgets_AreFractionsOfTheRunLimit_WhenItIsLessThanTheMachine; full suite |
| M9 | (this commit) | TemplateParseTests.HandParse_ReadsDirectivesAsTheRegexesDid; StreamingCheckTests.LinkCheck_WalksThatReadHeadersAgain_WaitForTheWalkBudget_SameAnswer; full suite |

Measured (run peak private MB, 1.0.283 -> with M1-M3): regtree 2699 -> 1465, bigtree 993 -> 971, L 1630 -> 1560,
mactree 1602 -> 1841 (GC noise: same live data, check allocation -31%).

## L. Changes to documented behavior

memoryStopMb now also caps the memory budgets (parse, pre-pass, check), not only the stop. New: CODECARVER_CHECK_WALK_MB
(the template walk budget), and two temporary debug switches, CODECARVER_DEBUG_ONLY and CODECARVER_DEBUG_CHECKMEM (USAGE).
