# CPU efficiency review

Review of wasted CPU in CodeCarver (src/, at 1.0.271), following the CPU-efficiency review specification.
Phase 1 is a source audit; the code was not changed while it ran. Phase 2 (section K) records the fixes made,
each with its before/after measurement.

## A. Executive summary

CodeCarver's work is dominated by one thing: the front end's per-file parse (tree-sitter plus the passes over
each tree), which took about 75% of the CPU on the benchmark workload (section H). Earlier speed work removed
the big quadratics there (scope lookup, parent finding, name pooling, long `#define` tables), and the hot loops
of the link check's tokenizer are tuned. What remains is of four kinds:

1. **Exceptions as control flow** in the `#if` evaluator: every identifier in an `#if` throws and catches a
   `FormatException` (C1). This is the cheapest large fix.
2. **Linear lookups that grow with the input**:
   - the -D analysis is quadratic in the defines per command (C2);
   - the include-search list is built with `List.Contains` (C3);
   - same-name locals are scanned linearly per use (C5);
   - line numbers are found by scanning from offset 0 per match (C14);
   - drop/keep overlaps are compared pairwise (C13).
3. **The same answer worked out again**, across rounds, plans, stages and passes:
   - forced includes are read per command (C4);
   - C++ constructor gates re-tokenize every header per plan (C8);
   - the emit closure re-reads headers per wave (C9);
   - emitted files are re-scanned every check round (C10);
   - `CodeOnly` is built up to 7 times per file (C16).
4. **Allocation churn in per-identifier loops**: a string per identifier occurrence (C7), and the heavy capture
   API on files with parse errors (C6).

Confidence is high that each mechanism exists, since each is confirmed in the code. How much each one matters
depends on the shape of the tree. The estimates in section C are ranges, and section K holds the measurements.

**Limits.** No hardware counters were used, and profiling is coarse: the run's own per-phase CPU counters
(`cpu.<phase>.ms`), not a sampling profiler. The real work trees can't be measured here, so a synthetic workload
built to exercise each path stands in for them.

## B. CPU architecture overview

| Phase (summary key) | What it does | Parallel? |
|---|---|---|
| walk | lists the tree | no |
| buildLogs / buildTraces | parses the build log and file traces | no |
| defines | per-file -D sets (`AnalyzeDefines`), forced includes, probe | no |
| input+refscan | reads every file for `#include`s and names | ReadAhead (64 reads) |
| build-graph | the front end: macro pre-pass, per-file parse + passes 1-5, single-threaded resolve | 64-wide ReadAhead.Map |
| roots / reachability | root providers, one BFS | mostly no |
| stageN.emit | copy or prune kept files | ReadAhead.For (64) |
| stageN.check | `EmitClosure` + `EmittedLinkCheck` rounds (up to 8) | ReadAhead.Ordered (64) |
| stageN.headerCarve | `HeaderCarver` fixpoint over big headers | no |

The **synchronization points** are the `MemoryGate`/`ByteGate` monitors (taken once per file, so cheap), the
ReadAhead ordering lock (once per item), and `IoMeter`'s concurrent dictionary (about 100 ns per op). None of
them is contended in a way that matters.

There is **no idle CPU**: the only timer is the heartbeat, at most once per 15 s and skipped while one is
running. The run is a batch.

## C. Ranked findings

The ranking is by expected effect on a large firmware run (a build log, mostly C, a file-level stage plus a pruned
stage).

| Rank | Finding | Severity | Estimated CPU savings | Confidence | Effort | Risk |
|---|---|---|---|---|---|---|
| 1 | C1 `#if` evaluator throws per identifier | High | 5-30% of build-graph on `#if`-dense trees | High | S | Low |
| 2 | C6 parse-error files use the heavy capture API | High | 10-40% of the parse of an error file | Medium | S | Low |
| 3 | C7 a string per identifier occurrence | Med-High | 3-10% of build-graph, mostly GC | Medium | S | Low |
| 4 | C5 same-name locals scanned per use | Med-High | 0-20% of a big file's parse | High | S | Low |
| 5 | C2 -D analysis quadratic per command | High | defines phase down to O(D); seconds to tens of s | High | S | Low |
| 6 | C3 include-search `List.Contains` | High | up to ~1e9 compares on big logs | High | S | Low |
| 7 | C4 forced include re-read per command | High | 1 read instead of ~20k | High | S | None |
| 8 | C10 emitted files re-scanned every check round | High (conditional) | rounds 2-8 near-free | High | M | Low-Med |
| 9 | C9 emit closure re-reads headers per wave | High-Med | check phase, per Close | High | S | Low |
| 10 | C8 C++ constructor gate re-tokenizes every header per plan | High (C++) | one header pass per run instead of per plan | High | M | Low |
| 11 | C16 `CodeOnly` built 3-7 times per file | Medium | 2-6 file-sized passes per file | High | S-M | Low |
| 12 | C14 line numbers by scanning from offset 0 | Medium | O(matches × size) to O(matches log n) | High | S | None |
| 13 | C13 drop/keep overlap is pairwise | Medium | 25M compares per 10k-def file | High | S | Low |
| 14 | C15 nested initializer lists rescanned at each depth | Medium | 2-3x on table files | High | S | Low |
| 15 | C17 pass 1d runs the scan with declarators | Medium | one regex pass per error file | High | XS | None |
| 16 | C11 HeaderCarver fixpoint re-streams big headers | High (multi-GB headers) | (depth+1) passes down to ~1 + increments | High | M | Medium |
| 17 | C12 pruned stages rebuild the tree each round | Med-High | rounds re-write only changed files | High | M | Medium |
| 18 | C18 file-scope invocation analysis per invocation | Medium | memoized per (macro, arg) | High | S | None |
| 19 | C19 LineMaps clones the define table; dead + uncertain maps scan twice | Med-Low | one scan instead of two in verify | High | S | Low |
| 20 | C20 TemplateInstances regex per directive, line count from 0 | Medium | linear | High | S | Low |
| 21 | C21 macro wrapper fixpoints rescan all bodies | Medium | worklist | Medium | M | Med-Low |
| 22 | C22 `AfterInclude` walks the undef set per include | Medium | dirty list | High | S | Low |
| 23 | C23 asm includes: tree scan per unresolved include, twice | Medium | basename index | High | S | Low |
| 24 | C24 paths normalized again in ~10 passes, and per stage | Medium | one per-file record | High | M | Low |
| 25 | C25 file traces parsed twice | Medium | one parse | High | S | Low |
| 26 | C26 extra whole-tree reads (root names, section roots) | Medium | 1-2 tree decodes | Medium | M | Medium |
| 27 | C27 flat define list rebuilt 6 times, once per stage | Low-Med | | High | S | None |
| 28 | C28 `Hidden()` normalizes per include dir × name | Low-Med | | High | S | Low |
| 29 | C29 analysisOnly / emit-from walk again, redo identical stages | Medium (those modes) | | High | M | Low |
| 30 | C30 failure cases scan the graph per build error | Medium (failure path) | | High | S | None |
| 31 | C31 stub discovery quadratic per file | Low-Med | | High | S | Low |
| 32 | C32 pruned files read and scanned twice per round | Medium | | High | M | Medium |
| 33 | C33 interpreted static regexes; `MacroDeclarator` with no pre-check | Low-Med | | Medium | S | None |
| 34 | C34 prune decision computed twice, and `HeadDefinesType` blanks whole bodies | Medium | | High | S | Low |
| 35 | C35 IncludeClosure resolves includes again on every emit | Low-Med | | High | S | None |
| 36 | C36 64-wide parallelism on CPU-bound parse and scan | Hypothesis | context switches, cache | Low | S | Low |
| 37 | C37 low-impact items (section D, last entry) | Low | | High | XS each | None |

The savings in this table overlap (C1, C6, C7, C5, C15 and C16 all come out of build-graph), so they must not be added together.

## D. Detailed findings

Each finding gives **Where** (file:line at 1.0.271), **Now** (what the code does), **Waste** (why the CPU is wasted and how often), **Fix** (the recommended change), **Risk** (the correctness risk), **RAM** (the effect on memory) and **Validate** (how to check the fix). Each is confirmed from the code unless marked *hypothesis*.

**C1. `#if` evaluator throws per identifier.** High.
- **Where:** Core/Preprocess/PreprocessorScanner.cs:518, :527, :535-552 (`TryParseNumber`), :374-385.
- **Now:** `ParsePrimary` calls `TryParseNumber(tok)` before `IsIdentifier`. For `FOO`, `long.Parse` throws `FormatException`, which is caught. A macro's value goes the same way in `ResolveIdentifier`. An expression it can't parse throws and is caught in `EvaluateCondition`.
- **Waste:** a first-chance exception costs about 10-50 µs, with a stack trace. It happens for every identifier in every `#if`/`#elif`, in every file, on every `LineMaps` call: the front end, and again per verify stage.
- **Fix:** return false at once when the first character isn't a digit. Parse digits with `TryParse` (radix and suffix as today).
- **Risk:** low; the set of accepted literals must stay the same. Covered by the preprocessor tests plus a new literal-matrix test.
- **RAM:** none.
- **Validate:** `cpu.build-graph.ms`, `stageN.cpu.check.ms`.

**C2. `AnalyzeDefines` is quadratic per command.** High.
- **Where:** Cli/CarveCommand.cs:602-629.
- **Now:** for each distinct name in a command, it rescans all of that command's defines and calls `SpecName` on each, which allocates a substring.
- **Waste:** D² per command. It runs per file group and again over all commands: 20k commands × 200² × 2 is about 1.6e9 substrings.
- **Fix:** one pass per command into name → spec set.
- **Risk:** low; keep "two specs for one name in one command varies".
- **RAM:** less.
- **Validate:** `cpu.defines.ms`.

**C3. Include search built with `List.Contains`.** High.
- **Where:** CarveCommand.cs:1576-1592.
- **Now:** for each (command, `-I`) pair it runs `GetFullPath`, `GetRelativePath` and `Replace`, then `mine.Contains` and `incUnion.Contains` on lists.
- **Waste:** about 1M pairs × (|mine| + |incUnion|) string compares.
- **Fix:** a `HashSet` alongside each list (the list keeps the search order), and memoize the resolution per (directory, include).
- **Risk:** low (order kept).
- **RAM:** one set.
- **Validate:** `cpu.build-graph.ms` / `time.*`.

**C4. Forced include found and read per command.** High.
- **Where:** CarveCommand.cs:671-693.
- **Now:** for each (command, `-include`) it builds candidates, calls `File.Exists` on each, reads the whole file, and runs an interpreted regex over it.
- **Waste:** one shared `config.h` is read 20k times.
- **Fix:** cache resolution by (directory, include list, name) and macro names by full path; use a static compiled regex.
- **Risk:** none; the tree is fixed for the run.
- **RAM:** a name set per distinct forced include.
- **Validate:** `cpu.defines.ms`.

**C5. Same-name locals scanned per use.** Med-High.
- **Where:** Frontend/TreeSitterFrontEnd.cs:1492-1498 (`IsLocalUse`), called at :2818.
- **Now:** `locals[name]` holds every interval for that name in the file, and each use scans them all.
- **Waste:** uses × declarations per name. For example, `hdev` in 3k functions is 72M compares in one file.
- **Fix:** the intervals are laminar, so sort them and binary-search with parent links (as `ScopeIndex` does); or, simpler and enough here, use a per-name forward cursor, since uses arrive in document order.
- **Risk:** low; it is the same set test.
- **RAM:** an int per local.

**C6. Parse-error files use the heavy capture API.** High.
- **Where:** TreeSitterFrontEnd.cs:2806-2811, `NodesInsideError` :3224-3247.
- **Now:** on `root.HasError` every identifier goes through `_idents.Execute(root).Captures`, which allocates a match, a list, a node and a closure per capture. `NodesInsideError` then calls `n.Type` (a fresh string) up to twice per node of the whole tree.
- **Waste:** every identifier of every file with a parse error; macro-heavy firmware has many.
- **Fix:** always use `LeanCaptures`, and make `NodesInsideError` return identifier ranges keyed by (start, end); use `IsError`.
- **Risk:** low; identifier leaves are unique by range.
- **RAM:** much less gen0 churn.

**C7. A string per identifier occurrence.** Med-High.
- **Where:** TreeSitterFrontEnd.cs:2759, :2817 (`tree.GetText`).
- **Now:** a `Substring` per call and per identifier, then interned by the name pool.
- **Waste:** on the order of 10¹⁰ short-lived strings over a 100 GB tree.
- **Fix:** look the name up in the pool from the source span (`NamePool.Get(source, start, end)`), as macro bodies already do.
- **Risk:** none.
- **RAM:** less churn.

**C8. C++ constructor gate re-tokenizes every header per plan.** High (C++ trees).
- **Where:** CarveCommand.cs:2127-2136 (`NewCtorGate`, called from `PlanFor` :2712); Core/Reachability/ConstructorGate.cs:52-83, :174-206.
- **Now:** every plan (the why plan, each stage kind, each extra check round) builds a gate that reads and tokenizes every header in the tree. `ReleasedBy` builds `_ctorsByType.Keys.ToHashSet()` per file.
- **Waste:** a full header pass per plan, plus types × files set inserts.
- **Fix:** a run-wide cache of rel → constructor type names the file instantiates; gates replay it. Build the type set once per gate.
- **Risk:** none; the inputs are fixed for the run.
- **RAM:** small.

**C9. Emit closure re-reads and re-tokenizes a header for each wave it gains a node in.** High-Med.
- **Where:** Core/Emit/EmitClosure.cs:49-76, :106-119; FileTreeEmitter.cs:255-271; CarveCommand.cs:2615-2624 (`SourceText`, uncached).
- **Waste:** kept headers × waves × closes.
- **Fix:** cache rel → definitions emitted (the scan, not the kept-filtered result).
- **Risk:** none.
- **RAM:** small.

**C10. Emitted files re-scanned every check round.** High when rounds > 1 or there are several stages.
- **Where:** EmittedLinkCheck.cs:164-165, :531-549; CarveCommand.cs:2926-2966, :3476-3514.
- **Now:** `LinkCheckCache` serves only paths under the source root. Emitted copies live in the stage directory, so every round re-reads, re-tokenizes and re-maps dead lines for every emitted file, although file-level copies are byte-identical to the source.
- **Fix:** a verbatim copy is checked through its source path (cached); a rewritten file is keyed by its content hash.
- **Risk:** low-med; the dead-line map is a pure function of (rel, text) for the run.
- **RAM:** the cached uses per file (watch `held.*`).

**C11. HeaderCarver fixpoint re-streams big headers.** High for multi-GB register headers.
- **Where:** Core/Emit/HeaderCarver.cs:92-109, :149-156, :402, :498, :538, :82-83.
- **Now:** each pass re-reads every big header and redoes every line. Per line it allocates: a `StringBuilder` copy per line, a substring per identifier, a fresh empty-params set per line, and 2×len substrings in `Wanted`.
- **Fix:** skip defines already expanded; re-scan non-define lines only when the paster set grew; use span lookups.
- **Risk:** medium; the fixpoint must give the same result (HeaderCarver tests).
- **RAM:** a set of processed names.

**C12. Pruned stages rebuild the whole tree each round.** Med-High.
- **Where:** CarveCommand.cs:3507-3512; FileTreeEmitter.cs:99-201; `EmitClosure.DefinitionsByFile` (rebuilt each call though `defsByFile` exists).
- **Fix:** rewrite only files whose cut signature (ranges, stubs, removed names) changed, and pass `defsByFile` in.
- **Risk:** medium; the signature must cover every input.
- **RAM:** small.

**C13. Drop/keep overlap is pairwise.** Medium.
- **Where:** FileTreeEmitter.cs:232-233.
- **Now:** `list.Any(d => d.Drop && kept.Any(overlap))` runs per kept .c file, per emit and per wave; about 25M compares for a 10k-definition file.
- **Fix:** an interval sweep (sorted spans, prefix max of the kept ends).
- **Risk:** low.

**C14. Line numbers found by scanning from offset 0.** Medium.
- **Where:** TreeSitterFrontEnd.cs:2616-2617 (aliases), :2634-2635 (linker names), :2652-2653 (asm labels), :2729-2730 (C++ range-for/bindings), :2299.
- **Waste:** O(matches × file size).
- **Fix:** `TextLines.LineOf(offset)`, a binary search over the line starts it already holds.
- **Risk:** none.

**C15. Nested initializer lists rescanned at each depth.** Medium (High on table files).
- **Where:** TreeSitterFrontEnd.cs:2840-2874 (pass 5).
- **Now:** every `initializer_list` capture is scanned, and the inner lists sit inside the outer, so each line of a 3-level table is scanned 3 times. Each line scan also allocates several strings.
- **Fix:** skip a capture inside the last scanned list.
- **Risk:** low; the inner lines are a subset with the same dead/`#` rules.

**C16. `CodeOnly` built 3-7 times per file.** Medium.
- **Where:** TreeSitterFrontEnd.cs:2390 (via `DefinerMacros.Uses`), :2419, :2768, :2782 (every TU), :831; ImplicitInt.cs:107, :232; HeadNormalizer.cs:54; ParamListConditionals.cs:37.
- **Fix:** compute it once per text, lazily, and share it where the input string is the same instance.
- **Risk:** low with a reference-equality gate.
- **RAM:** fewer transient copies.

**C17. Pass 1d runs the full scan with declarators.** Medium.
- **Where:** TreeSitterFrontEnd.cs:2368.
- **Now:** `EmittedLinkCheck.Scan(text, header:)` defaults to `declarators: true` (`MacroDeclarator` over the whole file), but only Definitions are read.
- **Fix:** `declarators: false`.
- **Risk:** none; the other internal callers already pass false.

**C18. File-scope invocation analysis repeated per invocation.** Medium.
- **Where:** TreeSitterFrontEnd.cs:2785-2791, :359-371; Core/Preprocess/FileScopeInvocations.cs:72-96.
- **Now:** for every invocation it runs `FunctionMacro` (a regex replace, a split and `CodeOnly`) and `UseOf`, which builds a new regex per call; the 15-entry regex cache misses.
- **Fix:** memoize per (macro, argument index); use IndexOf plus a word-boundary check in `UseOf`.
- **Risk:** none.

**C19. LineMaps clones the define table, and verify scans twice.** Med-Low.
- **Where:** PreprocessorScanner.cs:66-83; CarveCommand.cs:2850, :2866.
- **Now:** `DeadLineMap` and `UncertainLineMap` each run a full `LineMaps`, each cloning the table.
- **Fix:** one pass returning both maps.
- **Risk:** low.

**C20. TemplateInstances.** Medium.
- **Where:** Core/Preprocess/TemplateInstances.cs:385-390, :281-294.
- **Now:** a static `Regex.Replace` per directive in `StripComment`, and `s[..m.Index].Count('\n')` per match.
- **Fix:** guard with `Contains("/*")`; keep a running line count.
- **Risk:** low.

**C21. Macro wrapper fixpoints rescan every body.** Medium.
- **Where:** PasteMacros.cs:73-93, DefinerMacros.cs:128-197; built 3 times.
- **Fix:** precompute call sites; use a worklist.
- **Risk:** med-low (the `MaxPerMacro` order).

**C22. `MacroTable.AfterInclude` walks the undef set per include.** Medium.
- **Where:** MacroTable.cs:56-67.
- **Fix:** a dirty list of names set since the last include.
- **Risk:** low.

**C23. Asm includes.** Medium.
- **Where:** CarveCommand.cs:2428-2453, called at :2410 and :2417.
- **Now:** a scan of every tree file per unresolved include, `Excluded()` per file, done twice.
- **Fix:** one pass, a basename index, `Excluded` last.
- **Risk:** low.

**C24. Paths normalized again across passes.** Medium.
- **Where:** `CmdRel` at 9 call sites; `Excluded` at 10; `GetFullPath` on paths already full; per stage in `InfrastructureEmitter.Select` (:130-200, :581-623) and `MatchGlob`.
- **Fix:** one record per command and per tree file, made once.
- **Risk:** low.
- **RAM:** about 100 B per file.

**C25. File traces parsed twice.** Medium.
- **Where:** CarveCommand.cs:950-972, :2223-2271.
- **Fix:** one parse feeding both phases.
- **Risk:** low (keep exit 2 on a read error).

**C26. Extra whole-tree reads.** Medium.
- **Where:** CarveCommand.cs:1056/1087 (root names), :2104 (linker-section roots re-read every parsed file).
- **Fix:** gather both during the input scan.
- **Risk:** medium (phase order, early failure).

**C27.** The define list is flattened at :789, :1558, :1805, :1845, :1925 and :3609 (per stage, with a regex on each spec). Compute it once.

**C28.** `Hidden()` (:1820-1827) runs `GetFullPath` per include dir × angled name. Index by basename.

**C29.** analysisOnly and `--emit-from` walk the tree a second time (EmitFrom.cs:55-69) and emit identical file-level stages separately. Reuse the walk; copy stage 1.

**C30.** FailureCases.cs:93-143 scans the graph per build error. Use a name index.

**C31.** `StubsIn` (CarveCommand.cs:2696-2703) is O(defs²) per stubbed file. Group by span.

**C32.** `Pruned()` (EmittedLinkCheck.cs:460-483) and `ScanEmitted` (:531-546) read and scan each rewritten file twice per round. Merge them.

**C33.** EmittedLinkCheck.cs:784-814 regexes are interpreted. :869 runs `MacroDeclarator` on every emitted file even when the run has no alias macro. Use `Compiled` and gate it.

**C34.** FileTreeEmitter.cs:264-271 computes the prune decision again per wave. `HeadDefinesType` (:696-714) blanks up to 64 KB per range before looking for the first stop character, and its rare path builds an uncached regex. Stop at the first stop line.

**C35.** FileTreeEmitter.cs:378-394 re-resolves every include path per emit. Cache rel → resolved.

**C36 (hypothesis).** The parse (`ReadAhead.Map`, 64 tasks) and the link check (`ReadAhead.Ordered`, 64 workers) run CPU-bound work 64 at a time on 12 cores, because the window was sized for network reads. That oversubscribes the CPU: more context switches and cache pressure, and up to 64 trees in memory. **Validate:** compare total CPU at window 64 and at the core count.

**C37 (low items).** Each is small and only worth doing alongside a nearby fix:
- HeadNormalizer `AtFileScope()` runs LINQ per token (HeadNormalizer.cs:60, :78), and ParamListConditionals scans its stack per line.
- `.Type` and `GetChildForField(string)` allocate per call in node walks (`HasCall`, `IsFileScopeStatic`).
- `IsTranslationUnit` allocates a substring per call (:1929).
- There are two full node scans for include edges (:637, :1613).
- The symbol-budget gate runs two queries twice (:2190).
- `EmptyDefineConditions` uses `Split('\n')` (:115).
- `CmmFrontEnd` does work its only caller discards.
- `Digraphs` allocates a substring per character.
- Parser allocations: `ResolveIdentifier` re-tokenizes, and `Tokenize` allocates operator strings.
- `IsUnknown` runs a regex for every name not in the table.
- `BlankAlwaysDead` uses Split/Join.
- `DefinerMacros.IsWordChar` runs a regex.
- `CompilerPreprocess` runs a regex per line marker.
- `ComputedInclude` and `FileScopeInvocations` build regexes per call.
- `BuildLogScraper` concatenates continuations (quadratic in the run length) and tokenizes non-compile lines.
- `NameScan` type init runs 3×65536 regexes.
- Failure bundles re-read and re-anonymize per case.
- `LineStarts` is computed 4-5 times per scan.
- `Sees()` and the `definers` `Contains`.
- `MapPath` recomputes its prefixes per call.

**Checked, no material waste:**
- ReachabilityEngine: one BFS.
- CarvePlan: one sort.
- SoundnessCheck, CarveReport.
- The tokenizer core (NamePool, presized lists).
- ScopeIndex, ParentFinder, NodeKids, LeanCaptures on clean parses, UseList.
- DefineLines, PasteIndex, ExternalIncluders (caches per name and per directory).
- DefinedNameScan (chunked, hand scanner).
- MacroBodies.Parse.
- StagedOutput: verbatim copies short-circuit on time.
- IoMeter (about 100-200 ns per op).
- The heartbeat timer, the MemoryGate/ByteGate monitors, and CodeGraph's interning and edge dedupe.

## E. Quick wins

C1, C2, C3, C4, C5, C7, C13, C14, C17 and C22 are each a few lines, low risk, and covered by existing tests.

## F. Major architectural opportunities

- **One run-wide cache of per-file facts derived from fixed text** (C8, C9, C10, C12, C32, C34, C35). The source tree doesn't change during a run, yet each plan, round and stage re-derives from the text. A single cache keyed by (rel, text identity) would serve the emit closure, the constructor gate, the link check and the pruner.
- **Per-command and per-file records made once after the walk and the log parse** (C7/CL7, C24, C27): relative path, absolute directory, resolved include dirs, excluded flag, extension class.
- **One CodeOnly/line-map product per file** shared by the front-end passes (C16, C19).

## G. CPU savings estimate

**Phase 1 estimates.** On the benchmark workload, `build-graph` is about 75% of CPU, and C1, C5, C6, C7, C15, C16 and C17 all act on it. Expected combined: 15-35% of build-graph (low confidence; first-chance exception cost varies a lot with stack depth). On a build-log-heavy real tree (20k commands, 200 -D, 50 -I): `defines` and the include-search set-up fall from tens of seconds to under a second (C2, C3, C4; high confidence, from complexity). The check phase: rounds after the first nearly free (C10), and for C++ one header pass instead of one per plan (C8). No idle CPU to recover.

**Phase 2 measured results.** See section K.

## H. Benchmark and validation plan

**Workload.** It is generated deterministically and kept outside the repository:
- 30 module directories × 40 C files, plus 150 C++ files and `main.c`, about 9 MB in total;
- per-module headers with 300 `#if defined(CFG_n) && CFG_n > 0` blocks;
- a build log with 150 `-D`, 31 `-I` and `-include config.h` per command;
- static helpers and locals named `i`, `ret`, `p`;
- a function-pointer table per file;
- a parse error in every 5th file (240 files);
- C++ classes with constructors;
- two stages: `file`, and `prune` (`carveSourceFileContents = true`);
- `memoryStopMb = 4000`.

**Method.**
- The Release build is run as a separate process with `CODECARVER_TIMING=1`.
- Each run records process CPU time (`TotalProcessorTime`), wall time, and every `cpu.*`/`time.*` summary key.
- The builds being compared run interleaved (A, B, C, A, B, C, ...) for 3-5 rounds, and the minimum and median of each key are reported. Back-to-back runs of one build drifted by up to 50% with machine load; interleaving gives every build the same conditions.
- Three generated trees: `w` (the workload above, 1,351 files), `L` (log-heavy: 6,000 C and 10 C++ files, 300 `-D` per command, a 30.8 MB build log) and `T` (12 files, to catch start-up costs).
- Timing runs hold a ComputeWarden CPU lease, so no other heavy job is running.
- The machine has 12 logical cores; the runtime is .NET 8.

**Correctness.**
- The full test suite runs after every fix.
- A new regression test is added wherever a fix touches logic the suite doesn't pin.
- The kept/dropped counts of the benchmark must be identical before and after each fix.

## I. Coverage report

**Read in full:**
- CarveCommand.cs, EmitFrom.cs, TreeSitterFrontEnd.cs, and the front-end helpers (HeadNormalizer, ImplicitInt, ParamListConditionals, CmmFrontEnd, CmmTraceClosure, CSharpFrontEnd, CppFrontEnd, Digraphs, DefineLines, TextLines, UseList, ParentFinder, LeanCaptures, NodeKids).
- EmittedLinkCheck, EmitClosure, ConstructorGate, ReachabilityEngine, CarvePlan, DecisionsReport, PrecisionStats, SoundnessCheck, FileTreeEmitter, HeaderCarver, StagedOutput, InfrastructureEmitter, StubBodies, CarveReport, BuildSupportEmitter.
- Preprocess: PreprocessorScanner, MacroTable, TemplateInstances, DefinerMacros, PasteMacros, MacroBodies, CompilerPreprocess, ComputedInclude, FileScopeInvocations, DefinedNameScan.
- Util: SourceText, NameScan, IoMeter, CodeTokens, ReadAhead, MemoryGate, ByteGate.
- BuildLogScraper, ExternalIncluders, IRootProvider.
- Diagnostics: AnonymizedBundle, DiagnosticReport, UseShape, ReproBundle, BuildErrors.
- FailureCases, and Graph/CodeGraph.

**Skimmed:** CarveResolver, CarveTomlConfig, InfraClassifier, MacroProbe, MacroDensity, RootNameCheck, FileLookup, NamePool.

**Not reviewed:**
- DefinitionHead (diagnostics only), CFrontEnd queries, ParseMemory, OutputPath, KeepReason.
- Util: SourceWalk, RealPath, NameSet, SystemMemory.
- Frontend: FileAccessTrace, GeneratedCode, LinkFlags, TraceFile, AsmSymbols.
- Preprocess: CommandLineMacros, C99Inline, DynamicLookup.

These are small, and their callers were reviewed.

## J. Remaining unknowns

- How much of build-graph is tree-sitter's own native parse (not reducible here) and how much is the managed passes. The managed-side findings can only shrink the latter.
- The real work trees' shape: share of files with parse errors, `#if` density, command-line lengths, check-round counts. Their summaries report these counts (`files.withParseErrors`, `stageN.check.rounds`), so the next work run will show which findings matter there.
- C36 needs a measurement at several window sizes, under a lease.

## K. Fixes applied

Each fix ran the full suite before it was pushed. Where it touched logic the suite did not cover, it came with a new test, and that test was checked to fail with the fix broken. The kept and dropped counts were the same on every benchmark tree before and after each fix (`keep.files.reached` 210 on `w`, 410 on `L`).

| Fix | Version | Change | Test |
|---|---|---|---|
| C1 | 1.0.273 | `#if` evaluator tells an identifier from a number without throwing | existing |
| C2 | 1.0.274 | per-file define analysis in one pass per command | existing |
| C4 | 1.0.275 | forced includes searched and read once per (directory, -I list, name) | `ForcedInclude_SameNameUnderAnotherIncludeDir_IsItsOwnLookup` |
| C3 | 1.0.276 | include search: sets beside the lists; each (base, include) resolved once | existing |
| new | 1.0.277 | dynamic PGO off in the CLI runtime config (below) | n/a |
| C5, C17 | 1.0.278 | local-name test by binary search over sorted scopes; pass 1d scans without declarators | `SameNameLocals_HideTheFunctionOnlyInTheirScopes` |
| C14 | 1.0.279 | line of an offset by binary search over line starts | `TextLinesTests` |
| C13 | 1.0.280 | drop/keep overlap by sorted sweep, not pairwise | `DropRanges_OverlapFallback_MatchesPairwiseRule` |
| C7 | 1.0.281 | call and identifier names interned straight from the parsed text | `NamesAfterNonAsciiText_AreReadAtTheirOffsets` |
| C22 | 1.0.282 | `AfterInclude` re-checks only names set or undefined since the last include | `AfterInclude_Incremental_MatchesFullPass` |
| C6, C15, C18 | 1.0.283 | identifiers in parse-error files read lean, inside-ERROR test by range; nested initializer lists not rescanned; macro argument use worked out once per (macro, argument), without a per-call regex | `FunctionInErrorWrappedTable_IsKept_ViaFile` (non-ASCII case), `NestedInitializerLists_EveryDepthCounts_CommentsDoNot`, `UseOf_MatchesTheRegexVersion` |

**Measured (interleaved, minimum of 3-5 rounds, ms).** Tree `L` (log-heavy), base = 1.0.272:

| key | base | C1 | C2 | C4 | C3 |
|---|---:|---:|---:|---:|---:|
| `time.defines` | 23,295 | 23,357 | 2,131 | 961 | 966 |
| `time.input+refscan` | 5,083 | 5,085 | 5,075 | 5,136 | 2,860 |
| `stage0.cpu.check` | 4,750 | 3,359 | 3,359 | 3,218 | 3,000 |
| `stage1.cpu.check` | 3,921 | 2,359 | 1,750 | 2,031 | 1,734 |
| `pass.scopes` | 2,655 | 1,752 | 1,689 | 1,557 | 1,568 |
| wall, whole run | 47.8 s | 48.0 s | 25.5 s | 24.3 s | 22.2 s |
| CPU, whole run | 31.4 s | 30.3 s | 26.4 s | 25.4 s | 22.8 s |

On `L`, the run is 2.15× faster in wall time and uses 27% less CPU. Tree `w` (`#if`-heavy): `time.defines` 1,925 → 180 ms, `pass.scopes` 3,022 → 2,291 ms (C1), wall 9.25 → 7.65 s.

**New finding: dynamic PGO (1.0.277).** On `w`, C4 and C3 each cut their own phase but raised build-graph CPU (11.7 → 14.5 s). Neither the compiled regex nor the GC mode explained it (each tried as a separate build), while `TieredCompilation=0` removed it. The cause was dynamic PGO: instrumented tier-0 code ran the parse on dozens of threads at once, and its re-JIT was about a third of the run's CPU. It shows up as soon as an earlier phase finishes quickly and the parse starts while code is still at tier 0. It is now off in the CLI's runtime config (`DOTNET_TieredPGO=1` turns it back on):

| tree | key | PGO on | PGO off | tiered compilation off |
|---|---|---:|---:|---:|
| `w` | CPU | 19.3 s | 12.6 s | 10.8 s |
| `w` | wall | 7.47 s | 6.45 s | 6.56 s |
| `L` | CPU | 22.5 s | 21.0 s | 22.6 s |
| `L` | wall | 21.7 s | 20.9 s | 21.6 s |
| `T` | wall | 1.18 s | 1.14 s | 1.56 s |

Tiered compilation stays on: turning it off costs 0.4 s of start-up on a small tree.

**C5, C17, C14, C13, C7, C22, C6, C15, C18: timed together, no measurable change on these trees.** One interleaved batch under the CPU lease, 3 rounds, one build per release from 1.0.277 to 1.0.283 plus 1.0.285 (another session was using 6 cores at normal priority, so absolute times are higher than the tables above; compare within this table only). Minimum of 3 runs:

| tree | key | 1.0.277 | .278 | .279 | .280 | .281 | .282 | .283 | .285 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `w` | wall, s | 8.56 | 9.10 | 8.31 | 8.68 | 8.25 | 8.76 | 8.41 | 8.31 |
| `w` | CPU, s | 26.8 | 27.7 | 27.2 | 27.4 | 26.4 | 26.3 | 27.2 | 26.2 |
| `w` | `pass.scopes`, ms | 2,476 | 2,429 | 2,470 | 2,499 | 2,443 | 2,429 | 2,452 | 2,391 |
| `L` | wall, s | 25.2 | 21.8 | 25.0 | 24.7 | 25.0 | 25.0 | 25.7 | 24.8 |
| `L` | `stage0.cpu.check`, ms | 3,937 | 3,890 | 4,593 | 4,156 | 4,015 | 3,859 | 4,234 | 4,453 |

Every difference is inside the run-to-run spread (`L`'s medians ranged up to 44 s of CPU under the other load), and no build failed verification. That is what was expected: each of these fixes replaces a linear scan or a per-call allocation that only grows on shapes these trees barely contain (many drop ranges in one file, deep tables, include-heavy macro tables, parse-error files). They are kept for the complexity bound and because each is pinned by a test against the old behaviour; none adds work on any path, and 1.0.285 shows no regression.

**Reverted:** none. The compiled-regex part of C4 was suspected of the build-graph rise and tested as a separate build; it was not the cause and stays.

**Deferred:**
- **C8, C9, C10, C12:** caches across plans, waves and check rounds. On `L` the whole check phase is 0.46 s + 0.33 s wall, so they need a C++-heavy, many-round tree to show a gain and a cache-key design to stay sound. That is a larger change than this pass.
- **C11:** HeaderCarver fixpoint; medium risk, needs multi-GB headers to matter.
- **C16:** sharing one `CodeOnly` per text needs a reference-equality gate through four helper classes.
- **C19:** the double line-map scan runs only on the failure paths (`LineMaps` for write-ups, the uncertain map per violation), so it costs nothing on a clean run.
- **C20, C21:** low-to-medium gains, and C21's worklist must keep the `MaxPerMacro` order.
- **C26, C32, C36 and the remaining Low findings:** small or unmeasured. C36 (read-ahead window) needs timings at several window sizes under a lease.
