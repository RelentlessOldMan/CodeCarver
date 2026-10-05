# CodeCarver review — handoff for fixes

Reviewed at `1.0.130+9320b95` (main, clean tree), 2026-10-04. Reviewer: Claude Fable 5.1.
Audience: the engineer (Opus) who will fix what is listed here. Nothing in this review changed product
code; this file is the only output.

## 0. How to use this document

- Every finding has an ID, a severity, an evidence tag, the location, a repro, the fix, and the tests that
  must exist when it is done. Work in the order given in section 9.
- Evidence tags:
  - **[REPRO]** — reproduced with the built CLI on a scratch tree. The recipe is in the finding.
  - **[CODE]** — verified by reading the code path end to end; not executed.
  - **[SUSPECT]** — plausible from the code, not confirmed. Confirm with the given recipe before fixing.
- Severity:
  - **S0** — the carve silently drops something the real build/run needs, destroys user data, or leaks
    proprietary names from an artifact that is described as safe to share.
  - **S1** — wrong or misleading result that the user can notice (failed build, wrong label, ignored option).
  - **S2** — robustness, scale, precision, maintainability.
  - **S3** — nit.
- Standing rules for this repo (do not relitigate):
  - "Commit" means commit and push to `main` in one go. After every push report the version
    (`1.0.<commit count>+<sha>`).
  - Never write commit messages into `.git\`. Use `git commit -m` or a temp file in the repo root.
  - The real target tree is proprietary. Never ask the owner for a trace, log, source file, path list or any
    artifact. All validation here uses in-repo examples, scratch trees, or public corpora.
  - The Discord `code-carver` channel is read-only unless the owner directs a post.
  - ComputeWarden gates machine-saturating work (oracles, sweeps, corpus generation). A single
    `dotnet build`, a small carve, or the fast test suite is not gated.
  - Design contract: correct by construction, over-approximate at anything unresolved, never drop
    infrastructure the tool does not model. A fix that makes a carve tighter at the cost of soundness is
    wrong. A fix that makes it looser to become sound is right.
- Every S0/S1 fix lands with a regression test that fails before the fix. Section 8 lists the test work.

## 1. The short version

The engine (graph, BFS, staged output) is small and sound. The unsoundness is at the edges of the engine:
the `#ifdef` model, the build-log scraper, the name tables that ignore configuration, and the emit step
that writes more code than reachability accounted for. The built-in `verify` line cannot detect any of it.

Seventeen defects were reproduced where the carved tree is missing something the real build needs while the
run prints `verify : OK`:

| ID | What happens | Evidence |
|---|---|---|
| V1 | `verify` can never fail: it re-checks the same call list the edges were built from | [CODE] |
| F1 | Whole-file emit keeps unreached functions whose callees were dropped; link fails | [REPRO] |
| PP1 | Closed-world treats macros `#define`d in headers as undefined | [REPRO] |
| PP2 | Closed-world treats compiler built-ins (`__GNUC__`, `__cplusplus`, `__ARM_ARCH`) as undefined | [REPRO] |
| PP3 | A `#define` inside an unknown `#if` branch is taken as definite | [REPRO] |
| PP4 | A compiler that cannot be probed yields an empty table and closed-world on top of it | [REPRO] |
| PP5 | `#if(` with no space is not a directive; its `#else` flips the enclosing frame | [REPRO] |
| BL1 | `gcc a.c && gcc -DFOO b.c` on one log line gives `a.c` the `FOO` define | [REPRO] |
| BL2 | `@response-file` and `-include` defines are ignored | [REPRO] |
| N1 | A real function is discarded when any function-like macro in the tree has the same name | [REPRO] |
| R1 | Registration through a macro-wrapped `section` attribute is not rooted despite `KEEP()` | [REPRO] |
| K1 | `forceKeepFiles` cannot restore a dropped code file or a dropped `.cmm` | [REPRO] |
| CM1 | An ambiguous `DO name` binds to the first basename match and drops the right script | [REPRO] |
| E1 | `carveSourceFileContents` rewrites non-UTF-8 sources and corrupts bytes in string literals | [REPRO] |
| H1 | Header carving strips a `#define` used only by assembly or a linker script | [REPRO] |
| CS1 | C# with `carveSourceFileContents = true` prunes methods; docs say it does not | [REPRO] |
| O1 | An existing `<outputDirectory>/carved` that CodeCarver did not create is deleted | [REPRO] |

Two more things the owner should know before the next real-tree run:

- **T1 [REPRO]:** a build file-access trace roots every file the build opened, whole. With
  `buildTraceFiles` set, the carve keeps everything the build compiled and `carveSourceFileContents`
  removes nothing from those files. The multistage example README still shows the pre-trace numbers.
- **D1 [CODE]:** the crash diagnostic zip, which the tool tells the user is safe to send, includes
  front-end warnings that carry repo-relative file paths and `.cmm` names.

## 2. Soundness — the carve drops something needed (S0)

### V1. `verify` is a tautology  [CODE]

- Where: `src/CodeCarver.Core/Reachability/SoundnessCheck.cs:15-45`,
  `src/CodeCarver.Frontend/TreeSitterFrontEnd.cs:309-328`.
- What: `CallSites` is the same `pendingCalls` list that `ResolveUse` turned into `Calls` edges for every
  same-named function. `Calls` is always followed. So for any kept caller, every in-scope callee name
  already has a kept definition, and `KeptCallingDropped` cannot return a violation. All sixteen repros in
  section 1 print `verify : OK`.
- Fix: add an independent, text-level check over the **emitted** tree, and make it the thing the `verify`
  line reports. It must not use the graph's edges.
  1. For every code file the carve dropped, collect candidate function-definition names with a tokenizer
     that does not depend on tree-sitter captures (identifier followed by `(`...`)` and `{` at brace
     depth 0, after stripping comments, strings and preprocessor lines).
  2. For every emitted code file (after pruning), collect all identifiers outside comments and strings.
  3. A name defined in a dropped file, defined in no emitted file, and referenced in an emitted file is a
     violation: "emitted code references `X`, defined only in dropped `file`".
  4. Exit code 3 as today. List up to 20, write all to `codecarver/verify.txt`.
  5. Keep the old graph check as a second line if wanted, labelled as an internal consistency check.
- This check catches F1, PP1, PP2, PP3, PP4, BL1, BL2 and N1 as reproduced. Land it first: it is the
  safety net for everything else and for bugs not yet found.
- Tests: one per repro above asserting exit code 3 before the specific fix, 0 after.

### F1. File-level emit is not link-closed  [REPRO]

- Where: `src/CodeCarver.Core/Reachability/CarvePlan.cs:28-36` (a file is kept if any node is reached),
  `src/CodeCarver.Core/Emit/FileTreeEmitter.cs:23-51` (kept files copied whole).
- Repro:
  ```
  src/main.c : void a(void); int main(void){ a(); return 0; }
  src/a.c    : void c(void); void a(void){}  void b_unused(void){ c(); }
  src/c.c    : void c(void){}
  carve.toml : entryPoints=["main"], languages=["c"]
  ```
  Result: `c.c` dropped, `a.c` emitted whole. `gcc *.c` on the carved tree:
  `undefined reference to 'c'` in `b_unused`.
- The same hole exists at the pruned stage for every unreached span `EmitPruned` declines to remove
  (overlapping spans, unbalanced braces, the whole-file fallback at `FileTreeEmitter.cs:106-108`).
- Fix: make the plan closed over what is actually emitted.
  1. File-level stages: after the first reachability pass, add every `Function` and `Global` node of each
     kept non-header file as a root (new `RootKind.EmittedWhole`), recompute, repeat until no new file is
     kept.
  2. Pruned stages: have `EmitPruned` first compute (without writing) which unreached spans it will keep,
     root those nodes, recompute, repeat until stable, then write.
  3. This means the plan differs per stage. Compute a plan per distinct emit policy, and write
     report/manifest/decisions/repro from that stage's plan.
- Owner decision D-A (section 7): builds that link with `--gc-sections` tolerate this today. The default
  must be the closed plan; an opt-out can be added if the owner wants the tighter result.
- Tests: the repro as a scenario test (file-level and pruned), plus a BuildVerify link test.

### PP1. Closed-world ignores macros defined in headers  [REPRO]

- Where: `src/CodeCarver.Core/Preprocess/PreprocessorScanner.cs:37-68, 108-113, 286-310`. The table is
  cloned per file and only sees `-D` flags plus that one file's own `#define`s. `#include` is not followed.
- Repro:
  ```
  src/config.h : #define USE_FAST 1
  src/main.c   : #include "config.h" ... #ifdef USE_FAST fast(); #else slow(); #endif
  src/fast.c, src/slow.c
  cc.json      : main.c and fast.c compiled with -DX=1
  carve.toml   : buildLogs=["cc.json"]
  ```
  Result: `fast.c` dropped, `slow.c` kept. The real build calls `fast()`.
- This is the common case in firmware: feature macros live in config headers, not on the command line.
- Fix, step 1 (sound, do now): a macro that is `#define`d or `#undef`'d anywhere in the scanned tree, and
  is not concretely defined in the current file's table, is **unknown**, never "undefined".
  1. In the pre-pass (`BuildScopeMacros`, `TreeSitterFrontEnd.cs:398-460`) collect every macro name with a
     `#define` or `#undef` in any file. Hand the set to the scanner as a shared read-only "ambient
     unknown" set consulted by `IsUnknown` when the name is not in `_macros`.
  2. The big and macro-dense headers are skipped by the reader (`ReadRel` returns `""`), and they are
     exactly the files that define macros. Stream them separately: first collect the identifiers used in
     any `#if/#ifdef/#ifndef/#elif` across parsed files (a small set), then stream each skipped header's
     `#define NAME` lines and add the ones that are in that set. This bounds memory on multi-GB headers.
  3. Apply the same rule in `ExprParser.ResolveIdentifier` and the `defined` branch.
- Fix, step 2 (precision, later): resolve conditionals per translation unit in include order, using the
  TU's `-I` dirs, so a header's defines are actually known. A header's dead lines are then the lines dead
  under every including TU.
- Tests: the repro; a nested variant (macro defined in a header included by a header); a dense-header
  variant (macro defined in a >1 MB register header).

### PP2. Closed-world ignores compiler built-in macros  [REPRO]

- Where: same scanner; `src/CodeCarver.Cli/CarveCommand.cs:312-318` (probe gets only manual defines),
  `src/CodeCarver.Core/Preprocess/MacroProbe.cs:43` (always `-x c`).
- Repro: `main.c` calls `gcc_path()` under `#ifdef __GNUC__`, `other_path()` under `#else`; build log
  `gcc -DX=1 -c main.c`; no `compiler`. Result: `g.c` dropped, `o.c` kept.
- Three separate problems:
  1. No probe: every built-in is "undefined".
  2. Probe present: it runs as C, so `__cplusplus` is undefined for C++ files and mixed C/C++ carves.
     Code inside `#ifdef __cplusplus` in a shared header is treated as dead.
  3. Probe present: it runs without the TU's target flags, so `__ARM_ARCH`, `__thumb__`, `__ARM_FP`,
     `__OPTIMIZE__`, `__STDC_VERSION__` reflect the compiler's defaults, not the build.
     `#if __ARM_ARCH >= 7` can resolve the wrong way.
- Fix:
  1. In closed-world, an absent name that is reserved to the implementation (`__x`, or `_` followed by an
     uppercase letter) is unknown unless a probe table that matches the TU supplied it. Also treat
     `__has_include`, `__has_attribute`, `__has_builtin` and friends as unknown.
  2. Probe per flag signature. From each compile command take the language (`-x`, or by extension),
     `-std=`, `-m*`, `-march/-mcpu/-mfpu/-mfloat-abi`, `-O*`, `-f*`, `--target`/`-target`, `--sysroot`.
     Group TUs by signature, probe once per signature, cache, and build each file's table on its
     signature's base. A file with no command, or a header reached from TUs with different signatures,
     gets the intersection, with differing names marked unknown.
  3. C++ files probe with `-x c++`.
- Tests: the repro; `#ifdef __cplusplus` class in a header shared by a C and a C++ TU; a fake compiler
  script that prints different `__ARM_ARCH` depending on `-mcpu`.

### PP3. A `#define` under an unknown condition is treated as definite  [REPRO]

- Where: `PreprocessorScanner.cs:95-101, 115-127`.
- Repro (open world, any manual define to switch resolution on):
  ```
  #ifdef MAYBE
  #define USE_FAST 1
  #endif
  #ifdef USE_FAST  -> fast();  #else -> slow();
  ```
  Result: `slow.c` dropped. `MAYBE` is unknown, so `USE_FAST` is only possibly defined.
- Fix: track certainty separately from liveness.
  1. Add `Certain` to `Frame`: true only when the parent is certain and this branch was selected by a
     definite `True` with no earlier unknown branch in the same chain. An unknown `#if` gives live but
     uncertain branches, and so does its `#else`.
  2. `#define` in a live, certain context: `Set`. In a live, uncertain context: force the name to unknown
     (remove any definition, add to `_unknown`). `MacroTable.MarkUnknown` currently refuses when the name
     is defined; add a forcing variant.
  3. `#undef`: certain → remove and record as explicitly undefined (so PP1's ambient set does not turn it
     back into unknown); uncertain → force unknown.
- Tests: the repro; `#undef` under an unknown branch; nested certain-inside-uncertain.

### PP4. A failed compiler probe still closes the world  [REPRO]

- Where: `MacroProbe.cs:46-63` (exit code ignored; an error yields an empty, non-null table),
  `CarveResolver.cs:121-127` (naming a compiler sets `ClosedWorld`), `CarveCommand.cs:139, 313-318`.
- Repro: `[builds.m] compiler = "git"`, no build log. Output: `closed-world ... have compiler git`,
  `0 define(s)`, and `g.c` dropped from the PP2 tree. No warning.
- Also: when the compiler cannot be started at all, the warning says the probe is ignored but
  `closedWorld` stays true from the resolver.
- Fix:
  1. `MacroProbe.Probe` returns null when the exit code is non-zero or no `#define` line was parsed.
  2. A named compiler that cannot be probed is a configuration error: exit 2 with a message that the
     `compiler` key needs a GCC/Clang-compatible driver (`-dM -E`).
  3. Derive `ClosedWorld` in `CarveCommand` after inputs are actually loaded: true only if a probe
     succeeded or at least one compile command was parsed. Make `WorldReason` reflect that.
  4. Read stdout and stderr concurrently and enforce the timeout with a kill; see RB3.
- Related: only `cv.Compilers[0]` is probed (`CarveCommand.cs:142`). PP2's per-signature probing replaces
  this; until then, warn when more than one distinct compiler is configured.
- Tests: fake compiler that exits 1; nonexistent compiler; compiler that prints nothing.

### PP5. Directive keywords are split on whitespace only  [REPRO]

- Where: `PreprocessorScanner.cs:363-368` (`SplitKeyword`), `:70-103`.
- `#if(X)`, `#elif(X)`, `#if!defined(X)`, `#endif/*x*/`, `#else//x` are not recognised. An unrecognised
  `#if(` pushes no frame, so its `#else` flips the **enclosing** frame and its `#endif` pops it.
- Repro (FreeRTOS house style):
  ```
  app.h  : #ifndef APP_H / #define APP_H
           #if( CFG_A == 1 )  static inline void run(void){ a(); }
           #else              static inline void run(void){ c(); }
           #endif / #endif
  main.c : #include "app.h"  int main(void){ run(); return 0; }
  cc.json: gcc -DCFG_A=0 -c main.c
  ```
  Result: `c.c` dropped. The real build calls `c()`.
- Fix: the keyword is the identifier run after `#` and optional whitespace; the rest of the line is the
  argument. Strip trailing comments. `HeaderCarver.StartsWithHash` and `MacroDensity.IsDefineDirective`
  already do the whole-word test; make the scanner agree.
- Also from the same read of the scanner, untested and worth pinning: `defined X` without parentheses;
  `-1 < 0u` (C says false, the scanner says true — return unknown for unsigned suffixes);
  `0xFFFFFFFFFFFFFFFF > 0`; C++ `#if true`; directive-looking lines inside `/* */`; unbalanced
  `#endif`/`#elif`/double `#else`; CRLF fixtures (every test uses `\n`); a function-like `-D F(x)=x` is
  stored under the name `F(x)` (`MacroTable.cs:38-43`) so `#ifdef F` is false.
- Tests: `DeadLineMap_IfWithAttachedParen_IsADirective`; the nested-under-include-guard case above;
  `#endif/*x*/` after `#if 0`; one theory for the list above.

### BL1. Chained commands on one log line share flags  [REPRO]

- Where: `src/CodeCarver.Core/Frontend/BuildLogScraper.cs:58-83, 295-328`.
- Repro: log line `gcc -DBAR -c a.c -o a.o && gcc -DBAR -DFOO -c b.c -o b.o`. `a.c` is recorded with
  `FOO`. With `#ifdef FOO fa(); #else fb();` in `a.c`, `fb.c` is dropped. The real build calls `fb()`.
- Fix: split each logical line into simple commands at unquoted `&&`, `||`, `;`, `|` before looking for a
  compiler; carry a leading `cd DIR` forward across the split. Handle separators attached to tokens
  (`dir&&gcc`).
- Tests: the repro; `cd x && gcc ... && gcc ...`; quoted `"&&"` inside a `-D` value.

### BL2. Response files and forced includes are ignored  [REPRO]

- Where: `BuildLogScraper.cs:170-205`.
- Repro A: `gcc -DBAR @flags.rsp -c a.c` with `flags.rsp` containing `-DFOO`. `fa.c` dropped.
- Repro B: `gcc -DBAR -include force.h -c a.c` with `force.h` defining `FOO`. `fa.c` dropped.
- Fix:
  1. `@file`: resolve against the command's directory, tokenize, splice in place (recursively, with a
     depth cap). If the file cannot be read, mark that command's define set as incomplete.
  2. `-include F`, `-imacros F`, MSVC `/FI F`: record on the command. PP1's ambient set already makes the
     macros they define unknown; with PP1 step 2 they are processed first.
  3. Also parse `-Wp,-DFOO`, `-Xpreprocessor -DFOO`, `--define-macro`, `-D FOO` (already), `/D FOO`.
  4. A command with an incomplete define set must not contribute "absent means undefined" for its file:
     treat that file as open-world.
- Tests: both repros; nested response file; missing response file.

### BL3. Unrecognised compilers and empty logs  [REPRO]

- Where: `BuildLogScraper.cs:25-36, 209-238`; `CarveCommand.cs:233-238, 337-358`.
- Repro: text log `armcc -DX=1 -c main.c` / `iccarm -DX=1 g.c`. Zero commands are parsed, `#ifdef`
  resolution is silently off, and the output still says
  `world : closed-world (dead #ifdef branches dropped) — have 1 build log(s)`. Malformed JSON behaves the
  same way.
- This case is sound (nothing is dropped) but the label is false and the user loses the tightening without
  being told. With the project's "never hardcode a vendor" rule, the fixed compiler list is also a design
  gap: `armclang`, `armcc`, `iccarm`, `cl2000`/`armcl`, `ccrx`, `cctc`, `dcc`, `ccarm` (GHS), `xc8` are
  not recognised in text logs.
- Fix:
  1. A configured build log that yields zero compile commands is an error (exit 2), naming the file.
  2. Report how many commands mapped to a file under the carve root. Zero of N is an error that suggests a
     path-root mismatch (see TR3 for the remap option).
  3. Add `[builds.X] compilerNames = ["armcc", ...]` for text logs, and a generic fallback: any command
     with a recognised source file plus `-c` or at least one `-D`/`-I` is a compile command.
  4. Report the number of kept translation units that appear in no compile command. Under closed-world
     their `#ifdef`s were resolved against the universal set, which is only right if the log is complete.
     Owner decision D-B: treat such files as open-world by default.
- Tests: each bullet.
- [SUSPECT] `IsCompiler` treats any token whose file name starts with `gcc`/`clang`/`g++` as the compiler
  (`echo Building gcc_helper.c foo.c`). `/Users/x/a.c`, `/Data/...`, `/Include/...` are parsed as MSVC
  `/U`, `/D`, `/I`. Both degrade precision only; fix while in the file.

### N1. A function-like macro anywhere in the tree deletes same-named functions  [REPRO]

- Where: `TreeSitterFrontEnd.cs:163-169, 435, 1069`.
- Repro:
  ```
  src/a/uart.h : #define uart_write(x) hal_uart_write(x)      (target A)
  src/b/main.c : void uart_write(int x); int main(void){ uart_write(1); return 0; }
  src/b/uart.c : void uart_write(int x){ (void)x; }           (target B)
  ```
  Result: `b/uart.c` dropped. The definition is rejected as a "macro-invocation misparse", so the call
  resolves only to target A's macro.
- A tree that holds 24 targets will have this collision.
- Fix:
  1. Do not reject by name alone. Reject only when the candidate also looks like the misparse the rule was
     written for: no return-type specifier on the `function_definition`, or call-shaped parameters
     (`HasCallShapedParameters`), or nested inside a function body. The existing fmt `FMT_CATCH` test must
     stay green.
  2. In `ResolveUse` (`TreeSitterFrontEnd.cs:582-607`) link a name to every kind that defines it —
     functions, macros and globals — instead of the first kind found. Today a name that is both a
     function and a macro never follows the macro body, and a name that is both a macro and a global never
     references the global.
- The same tree-wide, configuration-blind table drives `_blankRegex` (`TreeSitterFrontEnd.cs:442-456`): a
  valueless `#define NAME` anywhere blanks every `NAME` token in every file for parsing. [SUSPECT] If
  another target has a real function `NAME`, calls to it disappear. Do not blank an occurrence that is
  directly followed by `(`.
- Tests: the repro; macro and global with one name; valueless macro and function with one name.

### R1. Macro-wrapped registration is not rooted  [REPRO]

- Where: `src/CodeCarver.Core/Frontend/IRootProvider.cs:106-195`, `TreeSitterFrontEnd.cs:36-46, 370-384`.
- Repro:
  ```
  init.h   : #define INITCALL(fn) static void (*__init_##fn)(void) \
                 __attribute__((section(".initcalls"), used)) = fn
  driver.c : #include "init.h"  static void drv_init(void){}  INITCALL(drv_init);
  link.ld  : SECTIONS { .initcalls : { KEEP(*(.initcalls)) } }
  ```
  Result: `driver.c` and `init.h` dropped. The image builds and links, and the driver is gone. This is the
  worst kind of failure because nothing reports it.
- Cause: both scanners look for a literal `__attribute__((...))` next to the declared name. Here the
  attribute is in the macro body and the name next to it is the macro's own.
- Fix: collect "keep macros" in the pre-pass — any macro whose body contains `section(`, `used`,
  `retain`, `constructor`, `destructor`, `__root`, `#pragma location`, or another keep macro (transitive).
  Every invocation of a keep macro at file scope roots the file node of the file it appears in, and every
  identifier in its argument list gets an address-taken reference from that file node. Also recognise
  `[[gnu::used]]`, `[[gnu::section(...)]]`, `__attribute(...)`, `__declspec(allocate(...))`.
- Tests: the repro; two-level macro wrapping; the attribute spelled `[[gnu::used]]`.
- Related limits to document in USAGE (not fixable generically): only GNU `ld` scripts (`.ld`, `.lds`,
  `.ldscript`) are read for `KEEP`; IAR `.icf`, ARM scatter `.sct`, TI `.cmd` and preprocessed
  `.ld.S`/`.lds.h` are not. Only `.s`/`.S`/`.asm` are scanned for symbol references.

### K1. `forceKeepFiles` cannot restore a dropped file  [REPRO]

- Where: `src/CodeCarver.Core/Emit/InfrastructureEmitter.cs:141-144`. The dropped check runs before the
  forced check. `CarveCommand.cs:969-971` folds trace-dropped `.cmm` into the same dropped set.
- Repro: F1 tree with `forceKeepFiles = ["c.c"]`. `c.c` is still absent and nothing is printed.
- The template, USAGE, three READMEs and the `.cmm` closure warning all name `forceKeepFiles` as the
  remedy for a wrong drop.
- Fix:
  1. Resolve forced globs once in `CarveCommand`, before reachability.
  2. Forced files that are graph files become `ExplicitFile` roots, so their callees and includes come
     with them.
  3. Forced `.cmm` are added to the closure seeds.
  4. In `InfrastructureEmitter.Select` a forced file is never skipped as dropped.
  5. Print the number of files each glob forced.
- Tests: forced dead `.c`; forced unobserved `.cmm`; forced file under an excluded directory (works today,
  keep it working).

### CM1. Ambiguous `DO` binds to the first basename match  [REPRO]

- Where: `src/CodeCarver.Frontend/CmmFrontEnd.cs:83-104`.
- Repro: `t2/flash.cmm` contains `DO init.cmm`; both `t1/init.cmm` and `t2/init.cmm` exist; the run trace
  observes `t2/flash.cmm`. Result: `t1/init.cmm` kept, `t2/init.cmm` dropped.
- Fix: resolve the argument as a path first (relative to the calling script's directory, then to the carve
  root). If that fails, bind to **all** basename matches. Strip quotes from the argument. Keep the warning.
- Tests: the repro; `DO ../common/x.cmm`; quoted path with a space.

### CM2. `.cmm` scripts are dropped on absence of evidence  [CODE]

- Where: `CarveCommand.cs:933-971`, `src/CodeCarver.Frontend/CmmTraceClosure.cs`.
- What: with a run trace, any `.cmm` that was neither observed nor statically reachable from an observed
  one is dropped. One trace is one scenario. Scripts reached through `DO &var`, menus, dialog buttons or a
  different flash mode are dropped with a warning at most. The config template says of runs: "Tightens +
  audits; never drops what it didn't see" (`CarveTomlConfig.cs:223`). That sentence is false for `.cmm`.
- This contradicts the project's keep-by-default rule for infrastructure. Owner decision D-C.
- Recommended default: if any kept script contains an unresolved dynamic `DO`, drop nothing and say why.
  Add an explicit opt-in (for example `[runs.X] dropUnobservedCmm = true`) for the current behaviour.
  Fix the template sentence either way.

### E1. Pruning corrupts non-UTF-8 source  [REPRO]

- Where: `FileTreeEmitter.cs:141` (`File.ReadAllText` / `File.WriteAllText`),
  `src/CodeCarver.Core/Emit/HeaderCarver.cs:102, 121-122`.
- Repro: a Latin-1 `main.c` with `"\xB5s \xB0C"` in a string literal plus one dead function,
  `carveSourceFileContents = true`. In the carved file the bytes `B5` and `B0` are each replaced by
  `EF BF BD`. The string the firmware displays has changed.
- Fix: read and write with `Encoding.Latin1` (byte-transparent) wherever a file is rewritten, or operate on
  bytes. Detect UTF-16 by BOM and copy such files whole with a warning. `HeaderCarver` also rewrites every
  line ending to `\n`; preserve the original endings.
- Tests: byte-for-byte equality of every surviving line for Latin-1, Shift-JIS, UTF-8 with BOM and CRLF
  inputs, for both the pruned emitter and the header carver.

### H1. Header carving strips defines used outside C  [REPRO]

- Where: `CarveCommand.cs:1104-1119` (header carve runs before infrastructure is copied),
  `HeaderCarver.cs:43-55, 100-116`.
- Repro: `regs.h` with 40,000 unused defines plus `STACK_TOP` and `USED_BY_C`; `main.c` uses `USED_BY_C`;
  `startup.S` includes `regs.h` and uses `STACK_TOP`; both carve toggles on. Result: `regs.h` shrinks to
  two defines; `STACK_TOP` is gone.
- With `#ifndef STACK_TOP / #define STACK_TOP <default>` in the startup file this builds and silently
  changes the stack.
- Fix:
  1. Run the header carve after `InfrastructureEmitter.Copy`, so assembly, linker scripts, `.inc`, and
     other pass-through text files seed the needed set. Skip binary files (NUL byte in the first 8 KB).
  2. Seed from the non-`#define` lines of the big header itself (typedefs, enums, inline functions).
  3. Collect `##` fragments from the big header's own kept defines.
- Tests: the repro; a define used only by a `typedef` in the same header; a define built by `##` inside
  the header.

### CS1. C# is pruned at method level  [REPRO]

- Where: `CarveCommand.cs:373-378` (the guard runs while `prune` is still false), `:1085, 1098`.
- Repro: `examples/csharp-app` with `carveSourceFileContents = true`. Output says
  `intra-file (unused functions removed)` and `Calculator.cs` loses `Subtract()`.
- The C# front-end's own comment says this is unsound without semantic analysis.
- Fix: apply the language guard inside the stage loop. For C#, force file-level and print one note.
- Tests: the repro asserts the file is byte-identical to the source.

### CS2. C# file-level carve drops files it needs  [CODE]

- Where: `src/CodeCarver.Frontend/CSharpFrontEnd.cs:21-36, 57-74, 106-111`. Kept files derive only from
  reached method nodes; there are no File nodes, no type-reference edges, and a call outside a method
  body (property getter, field initialiser, class-scope lambda) is discarded because `Enclosing` is null.
- Predicted drops: a type-only file (enum, interface, record, POCO); a class used only through an implicit
  constructor; `new List<T>()`/`new Ns.T()`; a method group; a base class or interface in another file;
  a top-level-statements program (no `Main`).
- The README calls the C# carve sound. Either add File nodes with type-reference edges (every identifier
  that matches a declared type name references that type's file), or document the limitation and warn.
- Tests: one theory row per pattern above; the csharp example built with `dotnet build` after the carve.

## 3. Data safety and privacy (S0/S1)

### O1. An unrelated existing output directory is deleted  [REPRO]

- Where: `src/CodeCarver.Core/Emit/StagedOutput.cs:35-45, 59-88, 96-141`. `IsCodeCarverOutput` has no
  callers. `Begin` writes the marker unconditionally.
- Repro: create `out2/carved/IMPORTANT.txt`, set `outputDirectory = "out2"`, run. The file is gone.
- Fix: before `Begin`, if the target directory exists and is not empty and its parent has no
  `.codecarver-output` marker, stop with exit 2 and a message that names the directory. Write the marker
  only when the target did not exist or was already marked. Apply to every stage directory.
- Tests: the repro (file survives, exit 2); a re-run into a marked directory still replaces it.

### D1. The "safe to send" crash package contains repo paths  [CODE]

- Where: `CarveCommand.cs:676` (`diag.Warn(w)` for every front-end warning), `:963` (`.cmm` warnings),
  `src/CodeCarver.Core/Diagnostics/DiagnosticReport.cs:86-113` (redaction only matches absolute paths).
- What: front-end warnings start with the repo-relative file path and can quote `.cmm` arguments.
  Exception messages can carry relative paths and symbol names (`KeyNotFoundException`, for example).
  The package is written on any unhandled exception and the console tells the user it "contains no
  source ... send this file to report the bug".
- Fix:
  1. Give warnings a category code. The diagnostic package records category and count only.
  2. Register the run's relative paths, entry-point names and config values with the report as sensitive
     strings and replace them at write time; additionally redact any token that contains a path separator
     or ends in a known source extension.
  3. Do the same for `exceptionMessage` and `innerException`.
- Tests: force a crash on a tree with distinctive file and symbol names; unzip; assert none of the names
  appears in any entry.

### D2. Stage names are used as path segments unchecked  [CODE]

- Where: `CarveCommand.cs:1087`, `CarveTomlConfig.cs:142-155`.
- `[stages."../../x"]` writes outside `outputDirectory`. Restrict build, run and stage names to
  `[A-Za-z0-9_-]+` in `ConfigLoader`.

### D3. Repro bundle keeps arbitrary extensions  [CODE]

- Where: `src/CodeCarver.Core/Diagnostics/ReproBundle.cs:49-58`. `Makefile.projectname` leaks
  `.projectname`. Keep the extension only if it is on an allowlist of known code/infra extensions.

## 4. Traces (S1)

### T1. A build file-trace roots every compiled file whole  [REPRO]

- Where: `CarveCommand.cs:845-847`, `IRootProvider.cs:36-48` (file mode roots every node in the file).
- Repro: `examples/multistage-firmware`, `--why diagnostic_selftest` →
  `ROOT[ExplicitFile]`. All three stages now produce 2,567 B; the README still shows 2,300 B for
  aggressive and max and says the function is stripped.
- Effect: with `buildTraceFiles`, the result is "everything the build opened", and intra-file carving does
  nothing in those files. This also contradicts the project's rule that an added input only tightens.
- Owner decision D-D. Recommended: an observed code file roots its **File node only**. The file is kept;
  with F1 in place the file-level stage closes over its functions correctly; the pruned stage can still
  remove functions unreachable from the entry points.
- Whatever is decided, state it in USAGE and regenerate the example README.

### T2. A trace that cannot be read is skipped  [CODE]

- Where: `CarveCommand.cs:823-829`. Any exception reading a trace (including out-of-memory on a multi-GB
  raw capture, since the whole file is read into one string and then split) prints a warning and the carve
  continues with fewer roots.
- Fix: stream with `File.ReadLines`; make `FileAccessTrace.Paths` take an `IEnumerable<string>`; a trace
  that cannot be read is exit 2, like a missing one.

### T3. A trace from a different root matches nothing, silently  [REPRO]

- Repro: a trace whose paths start with `/build/agent/repo/` against a carve root elsewhere. Output:
  `0 observed in-tree`, no warning, and for `.cmm`: "run trace opened none".
- The same failure applies to `compile_commands.json` with absolute paths from another machine
  (`CmdRel`, `CarveCommand.cs:275-285`), and to a Windows-captured file read on Linux or the reverse.
- Fix:
  1. Zero in-tree paths from a non-empty trace is a warning with its own category, and exit 2 unless the
     user opts out.
  2. Add `[advanced] pathMap = [{ from = "/build/agent/repo", to = "." }]`, applied to trace paths and to
     compile-command `directory`/`file`.
  3. Offer the mapping automatically: when no path is in-tree, find the longest path suffix that exists
     under the carve root for a sample of entries and print the implied prefix.

### T4. "Read outside the carve root" counts noise  [REPRO]

- Where: `CarveCommand.cs:835-838`.
- What: the counter increments for every candidate that ends in a source extension and is not an existing
  in-tree file. That includes every system header (`/usr/include/stdio.h`), and in a raw strace every
  failed include probe. On a real build the "missing dependency?" figure will be in the thousands.
- Fix: count only files that exist, that are outside the root, and that are not under a compiler or system
  include directory (take those from the probe's include search list when a compiler is configured, plus
  `/usr`, `/opt`, `Program Files`). Report translation units and headers separately.

### T5. Observed files are matched case-sensitively against graph paths  [CODE]

- Where: `IRootProvider.cs:39` (`wantFile` is ordinal) versus `observedRel` (case-insensitive).
  On Windows a trace path whose case differs from the directory walk is counted as "code rooted" and
  roots nothing. Normalise observed paths to the walk's spelling before rooting, and print the number of
  roots actually created.

### T6. Function traces do not understand C++ names or prefixed lines  [CODE]

- Where: `src/CodeCarver.Core/Frontend/TraceFile.cs:25-26`. `ns::Class::method` yields `ns`. A line that
  starts with an address or a timestamp is skipped. Take the last `::` component as the name, and accept
  an optional leading hex address. Report unmatched line counts.

### T7. Relative paths in strace captures  [CODE]

- A relative `openat` from a sub-make's working directory is resolved against the carve root
  (`CarveCommand.cs:833`). It either fails to match or matches a different file with the same relative
  path. The capture script should record absolute paths; see the scripts section.

## 5. Precision — sound but looser than it should be (S2)

These matter for the stated goal (a 24-target tree carved to one target).

- **P1. `#include` resolves by basename to every file with that name** [REPRO].
  `TreeSitterFrontEnd.cs:737-747`. Repro: `boards/a/board.h` and `boards/b/board.h`, `main.c` compiled
  with `-Iboards/a`; both headers and `b`'s own includes are kept. Resolve in compiler order: the
  includer's directory (quoted form), then the TU's `-I`/`-iquote`/`-isystem` dirs; fall back to all
  basename matches only when nothing resolves. For a header included by several TUs use the union of
  their search paths.
- **P2. `static` functions resolve across files** [CODE]. A call can only reach a `static` function in the
  same translation unit. Record the storage class and restrict resolution for statics defined in `.c`
  files to callers in that file. Statics in headers stay global.
- **P3. Every identifier inside a function body that matches a function name is an address-take** [CODE].
  `TreeSitterFrontEnd.cs:801-820`. A local named `read`, `init` or `count` keeps every such function.
  Skip identifiers declared as locals or parameters of the enclosing function. First print the
  indirection tax (run the plan with `MinimalUnsafe` and print the delta, as `demo` does) so the gain is
  measurable.
- **P4. Entry points cannot be qualified** [CODE]. `main` roots every `main` in the tree. Accept
  `path/file.c:symbol`.
- **P5. Every C++ constructor in the tree is a root** [CODE]. `IRootProvider.cs:207-220`. Root a
  constructor only when its type name is referenced from kept code, to a fixpoint.
- **P6. Assembly in every directory roots symbols** [CODE]. `CarveCommand.cs:720-732`. Identifiers from
  the startup files of other targets become roots. When a build log or build trace is present, use only
  the assembly files that appear in it.
- **P7. Exclude matching is a substring test on the absolute path** [REPRO]. `CarveCommand.cs:391-392,
  726-727, 740-741`, `BuildSupportEmitter.cs:30-31`. Repro: carve root under a directory named `tests`
  with `excludeDirectories = ["tests"]` → "no c source files found". Match on the root-relative path, by
  whole segments. Put the test in one helper and use it everywhere (the infrastructure pass already has
  the right version).

## 6. Robustness, scale, CLI, cleanup (S2/S3)

### Robustness and scale

- **RB1. `.cmm` closure holds every script's text at once** [CODE]. `CmmTraceClosure.cs:44-55`,
  `CmmFrontEnd.cs:40-45` (`files.ToList()`). The real tree has about 1,600 scripts and 11.9 GB; everything
  up to 20 MB each is resident simultaneously as UTF-16. Process one file at a time.
- **RB2. Graph memory** [CODE]. `CodeGraph.AddEdge` never de-duplicates; `pendingRefs` receives one
  `(node, string)` per identifier occurrence with a fresh string each time
  (`TreeSitterFrontEnd.cs:801-808`). De-duplicate per enclosing node with a set, intern names, and drop
  identifiers that match no definition before storing them (needs a two-phase build: definitions first).
- **RB3. Child-process handling** [CODE]. `CarveCommand.cs:1257-1274`, `MacroProbe.cs:46-51`: both call
  `ReadToEnd` before `WaitForExit(timeout)`, so the timeout never applies, and reading stdout to the end
  before stderr can deadlock. Read both asynchronously, wait with the timeout, kill the process tree.
- **RB4. Unprotected `FileInfo.Length`** [CODE]. `CarveCommand.cs:566, 728, 744`: a file that vanishes
  between the walk and the stat throws out of a LINQ pipeline and ends the run.
- **RB5. `EmitPruned` has no per-file I/O tolerance** [CODE]. `FileTreeEmitter.cs:140-145`; `Emit` has it.
- **RB6. Hidden files are skipped** [SUSPECT — no .NET 8 runtime on the review machine's WSL].
  `src/CodeCarver.Core/Util/SourceWalk.cs:22-33` skips `Hidden | System`. On Linux .NET reports every
  dot-file and dot-directory as hidden, so `.config` (Kconfig output), `.gitmodules`, `.clang-format` and
  any `.dir/` are neither scanned nor copied, and the "complete buildable project" claim fails there.
  Confirm on Linux; then skip only the VCS directories the garbage classifier already names.
- **RB7. Directory symlinks and junctions are skipped silently** [CODE]. Count them and warn once.
- **RB8. Case policy** [CODE]. Path sets are a mix of `Ordinal` and `OrdinalIgnoreCase`. On a
  case-sensitive file system two files that differ only by case collapse in the emitter's skip/dropped
  sets and one is not copied. Introduce one `PathComparer` chosen per platform and use it everywhere.
- **RB9. Prefix checks without a separator** [CODE]. `full.StartsWith(rootFull)` at `CarveCommand.cs:540,
  835` and `FileTreeEmitter.cs:206` accepts `/repo2` for root `/repo`. In `CopyUnscannedIncludes` the
  consequence is worse: an `#include "../src-gen/t.inc"` from root `.../src` passes the check, becomes
  `trel = "../src-gen/t.inc"`, and is copied to `Path.Combine(outDir, trel)` — outside the staged tree,
  with no belt-and-braces check (the infrastructure emitter has one at `InfrastructureEmitter.cs:64-66`).
  Use the `OutputPath.Normalize` approach and add the destination check.
- **RB9b. Whole-line span removal** [SUSPECT]. `FileTreeEmitter.cs:313-314` drops whole lines, so
  `int g = 1; int dead(void){return 0;}` on one line loses `g` when `dead` is pruned. Confirm; if so,
  skip spans that share a line with another declaration (keep more).
- **RB10. Unbounded warnings** [CODE]. `CarveCommand.cs:551, 559` print one line per unresolved or
  ambiguous include. Cap on the console, write all to `codecarver/warnings.txt`.
- **RB11. Ctrl-C handler added once per stage** [CODE]. `CarveCommand.cs:1096`; it disposes the staged
  output from another thread while the emitter is writing. Register once, set a cancellation flag.
- **RB12. Front-end writes to `Console.Error` directly** [CODE]. `TreeSitterFrontEnd.cs:293, 503`; the
  injected writers are bypassed. `DiagState` is static mutable state. Both break in-process test isolation.
- **RB13. Integer options** [CODE]. `CarveTomlConfig.cs:170-171` casts `long` to `int` unchecked and
  accepts negatives.
- **RB14. Concurrent runs to one output reap each other's staging directory** [CODE].
  `StagedOutput.cs:261-276`.
- **RB15. `.d` is classified as garbage** [CODE]. `InfraClassifier.cs:45-49` is described as airtight;
  `.d` is also D source and DTrace. There is no way to turn garbage pruning off (`pruneGarbage` is a
  constant), only per-file `forceKeepFiles`.

### A name-free summary for the one-way workflow (feature)

The owner can only send back aggregate numbers. Today they have to pick them out of console output that
also contains names. Write `codecarver/summary.txt` (and `.json`) that is guaranteed to hold no path,
file name or symbol: version, world, counts of nodes/files/roots by kind, per-stage sizes, verify result,
trace match counts, and warning counts **by category**. Add a test that asserts no input name appears in
it. This is the single most useful change for the feedback loop.

### CLI and config

- **U1. Relative paths in the config resolve against the current directory** [REPRO].
  `codecarver carve e17/src --config e17/carve.toml` from the parent directory → "not found: run.trace".
  Resolve `outputDirectory`, `entryPointsFile`, logs and traces against the config file's directory. The
  examples run from the config's directory, so they are unaffected.
- **U2. Messages name flags that no longer exist** [CODE]. 113 occurrences in 17 source files
  (`--roots`, `--out`, `--aux`, `--exclude`, `--build-log`, `--probe`, `--trace`, `--strict-roots`,
  `--keep-garbage`, `--diag*`, `--prune`, `emit-config`). User-visible ones: `Program.cs:29`,
  `CarveCommand.cs:317, 382, 552, 560, 699, 795, 857`, `CarveReport.cs:71, 79, 84, 124, 172, 181`,
  `InfrastructureEmitter.cs:66, 127, 132`, `StagedOutput.cs:64, 182`, `DiagnosticReport.cs:178, 188-189`.
  Replace each with the config key.
- **U3. No `--help`; no arguments runs `demo`** [CODE]. `Program.cs:10-31`. Add `help`/`--help`/`-h`,
  make it the default, list `carve`, `init`, `scan-log`, `version`, `demo` and the exit codes
  (0 ok, 1 runtime failure, 2 usage/config, 3 verify failed).
- **U4. The `init` template fails unedited** [REPRO, docs audit]. `buildLogs = ["make-n.log"]`,
  `excludeDirectories = ["tests","other_board"]` and `Reset_Handler` are live. Comment them out.
  `init --anything` silently writes `carve.toml`.
- **U5. `resolved-config.toml` is the input config with a comment header** [CODE]. Either write the
  resolved values or rename it.
- **U6. The executable is `CodeCarver.Cli.exe`; every doc says `codecarver`** [docs audit]. Set
  `<AssemblyName>codecarver</AssemblyName>` (and update scripts), or change the docs.
- **U7. `--stage` is ignored without `[stages]` and under `analysisOnly`** [docs audit]. Make both errors.
- **U8. `--why` is unreachable after an unresolved-root failure** [docs audit]. Handle `--why` before the
  strict-roots exit, and print near-miss names for an unresolved root.
- **U9.** `languages = ["cs","csharp"]` is rejected as "cannot merge"; normalise aliases first.
  `Version()` falls back to `0.1.0` while the csproj fallback is `1.0.0`.

### Dead code to remove

`CarveCommand.cs`: the second missing-log check (`:236`), the `--prune` note (`:374-378`, after CS1 moves
it), the roots-empty check (`:380-384`), `traceFormat`/`fileTraceFormat` and their regex branches,
`dumpSpans`, `diagPath`, `strictRoots`, the `"cmm"` language branches, the stale list in
`SanitizeCommandLine`. `BuildSupportEmitter.Copy` (only its two static helpers are used; move them).
`IFrontEnd`/`BuildConfig` (`src/CodeCarver.Core/Frontend/IFrontEnd.cs`) describe a front-end that was
never built; only `CompileCommand` is used. Unused `EdgeKind`/`RootKind`/`NodeKind` members.
`CarveCommand.RunCore` is 1,100 lines; when touching it for the fixes above, extract: input loading,
define-table construction, root discovery, per-stage emit.

## 7. Decisions for the owner

Do not guess these. Each has a recommended default that is the sound choice.

**Decided 2026-10-04:** the owner chose the recommended default for all six (D-A through D-F).

- **D-A (F1):** file-level output must link without `--gc-sections`. Recommended: yes, always close the
  plan over emitted code. Alternative: add an opt-out for builds known to use `--gc-sections`.
- **D-B (BL3):** kept source files that appear in no compile command. Recommended: open-world for those
  files, with a count in the summary.
- **D-C (CM2):** dropping `.cmm` that one run did not open. Recommended: keep all unless explicitly opted
  in; never drop when a kept script has a dynamic `DO`.
- **D-D (T1):** what a build file-trace means. Recommended: keep the file (root the File node), do not
  root its functions.
- **D-E (PP4):** a `compiler` that cannot be probed. Recommended: hard error.
- **D-F (U1):** config-relative paths. Recommended: relative to the config file.

## 8. Documentation findings

From a separate full audit of README, DESIGN, docs/, example READMEs and the `init` template against the
code. Items already covered above are referenced by ID.

### Wrong in a way that misleads

- `forceKeepFiles` described as "always keep": `docs/USAGE.md:95, 263-266`, `examples/README.md:10`,
  `examples/cmm-trace/README.md:51-53`, `examples/multistage-firmware/README.md:87-92`, template `:208`.
  Becomes true once K1 is fixed.
- C# "falls back to file-level": `docs/USAGE.md:258-259`, `README.md:32`,
  `examples/csharp-app/README.md:4-8, 29`. True once CS1 is fixed.
- Closed-world described as "correct because the build told us the real set": `docs/USAGE.md:118-119,
  274-275`, `docs/WORKREPO.md:150-152`, template `:214-215`. After PP1/PP2, document precisely what is
  known: `-D` flags, probed built-ins, and nothing else.
- `defines` alone is said to close the world: `docs/WORKREPO.md:147-150`,
  `examples/multistage-firmware/README.md:80-81`. Code: only `buildLogs` or `compiler` do.
- `examples/multistage-firmware/README.md`:
  - `:51-53` show 2,300 B for aggressive/max; actual is 2,567 B at every stage (T1).
  - `:27, 56` say `diagnostic_selftest()` is stripped; it is not.
  - `:28` says the dead `#else` is dropped; emitted text always contains both branches, since `#ifdef`
    resolution affects reachability only. State that plainly in USAGE as well.
  - `:32` lists `src/.git/`; it does not exist.
  - The one-line `stage : ... size ...` format does not exist.
- Commands that fail as written: `carve scan-log` (`docs/USAGE.md:290`), `carve init carve.toml`
  (`docs/WORKREPO.md:24`, `docs/SHAKEDOWN.md:29, 40`, `docs/ADAPTING.md:78`), `carve version`
  (`docs/SHAKEDOWN.md:24`). `docs/SHAKEDOWN.md:89-91` shows JSON syntax for TOML keys.
- Template: "ignores its own output when scanning source" (`:192-193`; an overlapping output is a hard
  error), "with a size comparison" (`:229-230`; none is printed), "Every INPUT and OPTION lives here"
  (`:190`; `analysisOnly` and `[advanced]` are absent), `carveHeaderFileContents` "strip unused #defines
  from kept headers" (`:211`; only headers over `maxParseBytes` or macro-dense headers ≥ 1 MB).

### Stale or missing

- Output artifacts: `decisions.txt` and `repro.graph.json` are not listed in `docs/USAGE.md:85`;
  `docs/SUPPORT.md:28, 46-47` says `report.txt` holds the verify result and the world (it holds neither);
  `examples/cmm-trace/README.md:65-66` says `.cmm` drops are in `report.txt` (they are only in
  `manifest.json` `droppedCmm`). Put verify, world and `.cmm` drops in `report.txt`.
- `docs/WORKREPO.md:57` says a diagnostic zip is written "on any failure"; it is written only on an
  unhandled exception. `:163` tells the reader to check traced functions against `keptFiles`.
  `:116-117` and `docs/SHAKEDOWN.md:46-47` give the `--why` advice that U8 makes possible.
- `[advanced]` keys, defaults and units are documented nowhere: `maxParseBytes` 20,000,000;
  `parseTimeout` seconds, default 20, 0 disables; `maxSymbolsPerFile` 50,000. Also undocumented: the
  `.codecarver-output` marker, the 500,000-node cap on `decisions.txt`, the `cs` alias.
- `DESIGN.txt` describes the pre-implementation plan (real preprocessor via `cc -E`, `--preprocessed`,
  `--map`, `--semantic`, "front-end and emitter are next"). Either rewrite it to the built design or mark
  it historical at the top; `README.md:33` points readers to it.
- `docs/TOOLING.md` reads as a spec; mark each item built or planned (response files, MSBuild binlog,
  MSVC probe, `-E` front-end, PE/COFF roots are not built).
- `docs/TESTING.md`: `-Big` "planned" (exists), `--preprocessed` mode (does not exist), Roslyn and
  LLVM/MinGW pins (not fetched), "tens of milliseconds" for a suite that includes BuildVerify.
- Linker-map oracle: `docs/WORKREPO.md:203-205` and `README.md:112` say it is ready;
  `docs/SHAKEDOWN.md:136-143` says it needs the removed `--dump-spans`. Nothing converts `decisions.txt`
  into the script's inputs. Write that converter or remove the claim.
- Scripts referenced but absent: `scripts\vendor-codespawner.ps1`, `make-firmware-corpus.ps1`. Present but
  undocumented: `carve-build-oracle.ps1`, `carve-perf-bench.ps1`, `wsl-syntax-check.sh`, `tools/capture/`
  in the README layout. `examples/cortexm-firmware` and `examples/tiny-firmware` have no README or config
  though `examples/README.md:11-12` lists them.
- `release.ps1:86` looks for `README.md` under `docs/`, so it is never bundled; shipped `USAGE.md` links
  to files that are not in the zip.
- `.gitignore` does not ignore `examples/cmm-trace/out/`. `examples/mixed-cpp-firmware/verify-build.sh:2`
  says "Not committed" and is committed.
- Smaller items: `docs/USAGE.md:27-35, 68-77, 136-137, 296, 304-317, 345`; `README.md:9, 101`;
  `docs/WORKREPO.md:88, 95-99, 190, 252, 270`; `docs/REPRODUCE.md:17, 73`; `examples/README.md:17-19`;
  `tools/codespawner/manifest-schema.md:117`; output-block formats in the cmm-trace and mixed-cpp READMEs.

### Prevent the drift

No test regenerates the output blocks of the multistage, mixed-cpp or csharp READMEs, and the stringlib
drift test is in the BuildVerify class that CI excludes. Add a fast test that runs each example through
the CLI and compares a normalised summary with a checked-in `expected-summary.txt` that the README
includes verbatim.

## 8b. Scripts, capture tools, CI, build and release

From a separate static audit. Tags: **[EXP]** confirmed by a small synthetic experiment (strace 5.16 in
WSL, Windows PowerShell 5.1, a scratch MSBuild project); **[CODE]** read; **[SUSPECT]** unconfirmed.
ProcMon itself was not available, so ProcMon runtime behaviour is [SUSPECT] where marked.

The capture scripts run on the owner's proprietary build machine and their output decides what the carve
keeps. Fix SC-A1 to SC-B4 before the next real-tree capture.

### `tools/capture/capture-file-trace.sh`

- **SC-A1 (S0) [EXP] Relative opens lose their working directory.** Lines 55, 60, 72-76; consumer
  `CarveCommand.cs:833`. `sh -c 'cd sub && cat rel.txt'` is logged as `openat(AT_FDCWD, "rel.txt")`, and
  the carve resolves `rel.txt` against the carve root. Under `make -C dir` or recursive make, compiler
  opens are dropped, or match a different file of the same relative name, or count as "outside the root".
  Fix: run strace with `-y` and take the path from the decoded return descriptor
  (`sed -nE 's/.*\) = [0-9]+<(.*)>$/\1/p'`), which is absolute. Notes: `-y` gives the real path, so the
  carve root must be compared as a real path too; strip a trailing ` (deleted)`.
- **SC-A2 (S1) [EXP] A failing wrapped command discards the whole capture.** Lines 27, 40-41, 60. Under
  `set -e` the script exits with the build's status and the EXIT trap deletes the raw file. Fix: capture
  the status, consolidate anyway, warn that the trace may be partial, exit with that status at the end.
- **SC-A3 (S1) [EXP for bash] `--pid` plus Ctrl-C writes nothing.** Lines 54-55. `|| true` does not keep
  bash alive through SIGINT. Fix: `trap : INT` around the strace call in that branch.
- **SC-A4 (S1) [EXP] A failed attach reports success.** Lines 55, 72-79: "wrote out (0 unique path(s))",
  exit 0. Fix: fail when the raw file is missing or empty, and when zero paths result; validate the pid.
- **SC-A5 (S1) [CODE] Empty array under `set -u` aborts on bash < 4.4.** Lines 45-48, 55, 60. That is the
  same host vintage as strace < 5.2, so the fallback path never works. Use
  `${status_opt[@]+"${status_opt[@]}"}`.
- **SC-A6 (S1) [EXP] Non-ASCII and quoted paths are mangled.** Lines 72-75; `FileAccessTrace.cs:26,
  54-59`. strace prints `café.c` as `caf\303\251.c`; the file is then never matched and shows up as an
  unobserved drop candidate. Decode `\NNN`, `\"`, `\\` in the script and in `FileAccessTrace.Paths`.
- **SC-A7 (S1) [EXP] Exec-only files are invisible.** `-e trace=open,openat` misses an in-tree tool that
  is only executed. Add `execve`, and `openat2`/`creat` where supported (probe as for `status=`).
- **SC-A8 (S2) [EXP]** On strace < 5.2 a failed open split across `<unfinished ...>`/`resumed` lines
  survives the `= -1` filter. Join by pid before filtering, or rely on the SC-A1 form.
- **SC-A9 (S2) [CODE]** Write-only and directory opens count as observed. Drop `O_WRONLY` and
  `O_DIRECTORY` lines.
- **SC-A10 (S3) [CODE]** `LC_ALL=C sort -u`; status lines to stderr; accept `--raw` in any position;
  consider `--seccomp-bpf`.
- **SC-A11 (S1) [CODE] No `.sh` file is executable in git** (mode 100644 for all of them). The README's
  `./capture-file-trace.sh` fails on a Linux clone. `git update-index --chmod=+x` each one.

### `tools/capture/capture-file-trace.ps1`

- **SC-B1 (S0) [CODE] The capture is system-wide.** Lines 53, 78-83. Every process on the machine is
  recorded. Any indexer, antivirus scan, IDE or `git status` that touches the repo during the build makes
  those files "observed", and observed code is rooted. The output file also lists files opened by
  unrelated programs (browser profile, mail store), and with `-Raw` the `.pml` holds every process's
  command line. Fix: start the build with `Start-Process -PassThru`, build the descendant PID set from
  `Process Create` rows, keep only rows from that set; or ship a `.pmc` filter via `/LoadConfig` with
  "drop filtered events". State the scope in the README.
- **SC-B2 (S1) [EXP] `-Out` resolves against the process directory, not `$PWD`.** Line 48. Use
  `$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)`.
- **SC-B3 (S1) [CODE] The build's exit code is ignored** and the build string runs in the script's own
  scope. Line 59. Run it in a script block, record `$LASTEXITCODE`, warn, exit with it.
- **SC-B4 (S1) [SUSPECT] No elevation check; fixed 800 ms start delay; no wait after `/Terminate`.**
  Lines 53-54, 67, 72. Assert administrator, use `/WaitForIdle`, wait for the ProcMon process to exit
  before `/OpenLog`.
- **SC-B5 (S1) [EXP] `-Out build.pml` makes the backing file and the output the same path**, and the
  script then deletes it. Line 49. Use `"$Out.pml"`.
- **SC-B6 (S1) [CODE] The suggested TOML line is invalid** (`"C:\Users\..."` in a basic string). Line 96.
  Print forward slashes or a literal string.
- **SC-B7 (S2) [CODE]** The filter keeps attribute-only and write opens and misses `Load Image`. Line 79.
- **SC-B8 (S1) [CODE]** Customised ProcMon columns or a stale filter give zero rows and a success
  message. Check the CSV header, pass `/NoFilter` or `/LoadConfig`, fail on zero paths.
- **SC-B9 (S3)** `Import-Csv` is slow on multi-GB files (use a stream reader); `-Encoding UTF8`; plain
  `$pml` argument for PowerShell 7; a Ctrl-C leaves the `.pml`.

### Capture README and packaging

- **SC-C1 [CODE]** `tools/capture/README.md` says the scripts consolidate "as they capture"; both write
  the full raw capture first. It should also state: a clean full build is required (an incremental or
  ccache build opens almost nothing); ProcMon needs admin and sees the whole machine; what happens on a
  failed build.
- **SC-C2 [CODE]** `release.ps1:69-84` does not ship `tools/capture`, which shipped `USAGE.md` tells
  users to run.

### Oracles and sweeps — results that can be wrong

- **SC-D1 (S1) [CODE, observed]** Five scripts run whatever Release DLL exists
  (`corpus-compile-sweep.ps1:11`, `cpp-oracle-sweep.ps1:10`, `differential-carve.ps1:31`,
  `carve-perf-bench.ps1:44-48`, `presets/embedded-arm.example.ps1:23`). On the review machine the Release
  DLL was `1.0.124+aa81c28fd-dirty` while HEAD was `1.0.130`. One shared helper: build Release
  incrementally and check the DLL's version against `git rev-parse`.
- **SC-D2 (S1) [EXP]** `2>&1` on a native command under `$ErrorActionPreference='Stop'` terminates on
  PowerShell 5.1: `differential-carve.ps1:30, 61`, `fuzz.ps1:23, 77-78`,
  `presets/embedded-arm.example.ps1:22, 49-50`. Use the `Continue` pattern the other scripts use.
- **SC-D3 (S1) [CODE]** `corpus-compile-sweep.ps1:46-51, 82-83` prints "0 regressions", exit 0, when WSL
  or gcc is unavailable and nothing was compared. Count that case and exit non-zero.
- **SC-D4 (S1) [CODE]** Carve exit codes are unchecked and output directories are reused
  (`corpus-compile-sweep.ps1:53, 62`, `cpp-oracle-sweep.ps1:45, 57`, and others): after a crashed carve
  the previous run's tree is compiled and reported clean. Delete the output first; fail on non-zero.
- **SC-D5 [CODE]** `carver-groundtruth-oracle.ps1:89-101, 141`: expected reachability uses all manifest
  roots, the carve uses only the first.
- **SC-D6 [CODE]** Comparison by basename: `carver-groundtruth-oracle.ps1:161-167`, `fuzz.ps1:56-57, 85`,
  `carve-build-oracle.ps1:119`. Key by relative path.
- **SC-D7 [CODE]** `carve-build-oracle.ps1`: `Remove-Item -Recurse` on a user-supplied `-OutDir`
  (`:90, 181`); fixed `/tmp` directory (`:115`); word-splitting `find` loop (`:118`); the negative test
  passes on any link failure (`:140-143`); manifest symbol names interpolated into bash unescaped
  (`:113, 124, 126`) — validate as identifiers.
- **SC-D8 [CODE]** `differential-carve.ps1:74-76`: `Compare-Object` ignores order, so the determinism
  claim is not tested. Compare hashes.
- **SC-D9 [SUSPECT]** `wsl-map-oracle.sh:33-34` reports SOUND on CRLF or UTF-16 input (no common lines).
- **SC-D10–D18 (S3) [CODE]** Fixed `/tmp` names in every WSL oracle; `wsl-cpp-oracle.sh:26, 28, 31`;
  drivers that do not exercise all listed roots (`oracle/simdjson_driver.cpp`, `oracle/fmt_driver.cpp`);
  dead `$env:` settings in `cpp-oracle-sweep.ps1:61-68`; exit-code overloading and fixed temp names in
  `fuzz.ps1`; `carve-perf-bench.ps1` leaves configs behind; `carve-build-report.ps1` always exits 0;
  TOML built by string concatenation in every script that writes a config.

### Build, version stamp, CI

- **SC-E1 (S1) [EXP]** `CodeCarver.Cli.csproj:28-51`: when git fails, the error text becomes the version
  (`1.0.fatal: ambiguous argument...`), so the "builds without git" comment is wrong. `git -C` also
  accepts any enclosing repository: a source copy unpacked inside another repo is stamped with that
  repo's count and SHA. Capture each `Exec` exit code, validate the values with a regex, and require
  `git rev-parse --show-toplevel` to be this repo.
- **SC-E2 (S3) [EXP]** `CodeCarver.Core` and `CodeCarver.Frontend` are stamped `0.0+<sha>`. Move the
  target to `Directory.Build.targets`.
- **SC-E3 (S1) [CODE]** CI is Windows-only, excludes BuildVerify, runs no example and lints no script.
  Add an `ubuntu-latest` leg (this also settles RB6 and the native-library load path), a job that runs
  the examples, and `shellcheck`/PSScriptAnalyzer.
- **SC-E4 (S3)** Add `permissions: contents: read` and `timeout-minutes`; pin actions by SHA, especially
  `actions/github-script` in the `pull_request_target` workflow.
- **SC-E5 (S3)** No lock file, no deterministic build settings (PDBs carry local paths), no `global.json`.

### Release, fetchers, vendored binary, hygiene

- **SC-F1 (S1) [EXP]** `release.ps1:95`: `Compress-Archive` on PowerShell 5.1 writes backslash entry
  names; several Linux extractors then create flat files and the native tree-sitter libraries are not
  found. Use `ZipFile.CreateFromDirectory`.
- **SC-F2 [CODE]** Release: README never bundled (`:81-83`); no LICENSE or third-party notices; internal
  runbooks ship; the gate tests Debug and ships Release; `origin/main\b` also matches `origin/main-x`.
- **SC-F3 (S1) [CODE]** `check.ps1:14` kills every `testhost` process on the machine, including other
  sessions' test runs. Filter by path.
- **SC-F4 (S1) [CODE]** `fetch-toolchains.ps1:18-22, 35-40, 60-61` downloads and executes a toolchain
  installer with no hash check. Pin SHA-256 values.
- **SC-F5 [CODE]** `fetch-corpus.ps1:56`: an interrupted fetch leaves a directory that is treated as
  complete forever; failures still exit 0.
- **SC-F6 [EXP]** `tools/codespawner/codespawner.exe`: unsigned, `GENERATOR_VERSION` says 1.0.9 and the
  binary says `1.0.0+d9402bf`, no source commit, checksum or licence recorded. Record all three.
- **SC-F7 [CODE]** `.gitignore`: add `presets/*.toml` (the preset writes the real repo path and root
  names there) and `examples/cmm-trace/out/`. `.gitattributes` covers only `*.sh` and the exe.
- **SC-F8 (S3)** The internal host name `\\IRISH\TestHole` appears in two scripts and `CLAUDE.md` of a
  public repo.

## 8c. Test suite

From a separate static audit of `tests/` against `src/`. The suite has about 150 fast tests plus 37
BuildVerify cases. It is deep on the 58-line `ReachabilityEngine` (about 20 tests, several duplicates)
and thin on the 1,288-line `CarveCommand.cs` where the behaviour above lives. Tags as in 8b.

### What runs in CI, and what cannot fail

- **TS1 (S1) [CODE]** `ci.yml:12, 28`: Windows only; the whole `BuildVerify` namespace is excluded, so no
  carved output is ever compiled on GitHub. No `timeout-minutes`, no `--blame-hang-timeout`, no coverage
  (coverlet is referenced and unused). `docs/TESTING.md` says Tier 2 runs "in CI" and skips "with a
  notice"; neither is true.
- **TS2 (S1) [CODE]** Compiler-free assertions sit behind toolchain gates inside the excluded namespace:
  the stringlib golden compare (`BuildVerifyTests.cs:60-110`), the only end-to-end HeaderCarver test
  (`:496-527`), the KEEP-section reachability for cortexm (`:689-716`), `PrunedCarve_*` (`:116-141,
  175-194`), the C++ try/catch and first-class-member cases (`:838-871, 891-928`). Split each into
  `X_Carve` (always runs, outside `BuildVerify`) and `X_Builds` (gated).
- **TS3 (S2) [CODE]** Silent skips report as Passed: `MacroProbeTests.cs:26, 46` (never exercises a
  successful probe in CI), `SourceWalkTests.cs:42, 64`, `CarveTomlRunTests.cs:358, 360`, the Windows-only
  returns in `StagedOutputTests` and `OutputPathTests`. Adopt a skippable-fact attribute and fail CI when
  the skipped count exceeds an allowlist.
- **TS4 (S1) [CODE]** `verify : OK` is asserted at `CarveTomlRunTests.cs:66, 437` and cannot fail (V1).
  Exit code 3 is unreachable. `SoundnessCheckTests` feed hand-built call sites only.
- **TS5 (S2) [CODE]** `FuzzTests.cs:50-51, 62-63` and `HostileInputTests.cs:22-23` cannot fail on a managed
  bug: every per-file exception is caught by the front-end and turned into a warning plus force-keep.
  Also assert no `extraction failed` warning and an empty `ForceKeepFiles` unless the case expects one; add
  NUL and lone-surrogate inputs to the generator.
- **TS6 (S2) [CODE]** `DeterminismTests.cs:49-69` runs the same ordered input twice. Order-dependent
  spots: `BuildScopeMacros` first-definition-wins, ambiguous `DO`, node ids in the repro graph, stage ties.
  Add `Carve_IsInvariantUnderFileOrderPermutation` (seeded shuffles; kept/dropped sets and (kind, name,
  file) triples identical).
- **TS7 (S2) [CODE]** Names that claim more than they check: `DeterminismTests.Carve_KeepsReachable_
  DropsDeadCode` asserts no drop; `ReproBundleTests.Build_NodeAndEdgeCountsMatchGraph` checks nodes only;
  `Write_ScalesToLargeGraph_WithoutMaterializing` cannot observe materialisation;
  `StreamingBuildGraphTests` compares name sets, not graphs; `Carve_Why_ExplainsSymbol` passes on
  "CARVED"; `Carve_Stages_RunAll_ToSubdirs` uses a tree where aggressive equals safe, so `EmitPruned`
  dispatch is unverified; `Carve_MixedCAndCpp…` never asserts the dead class is dropped; the closed-world
  CLI test covers only the "varies" case.
- **TS8 (S2) [CODE]** Hygiene: unguarded `Directory.Delete(recursive)` in `finally` across
  `FileTreeEmitterTests`, `HeaderCarverTests`, `IntraFilePruneTests`, `BuildVerifyTests` (an AV lock masks
  the real failure — use a shared retrying `TempDir`); `DiagnosticReportTests.cs:131-133` writes to `Z:\`;
  no `xunit.runner.json`, and `DiagState` plus the per-stage `CancelKeyPress` handler are process-global,
  so a second in-process CLI test class would race — use one `[Collection("cli")]` or make `DiagState`
  per-run; a crashed CLI test leaves `CodeCarver_diag_*.zip` in `%TEMP%`.
- **TS9 (S2)** `BuildSupportEmitter.Copy` is dead product code; four `FileTreeEmitterTests` and three
  BuildVerify tests exercise it. Retarget them to `InfrastructureEmitter`.

### Tests that must exist (by finding)

Each finding in sections 2–6 lists its tests. The ones the audit adds beyond those:

- Privacy: `Redact_PathUnderHome_RemovesWholePath` (today `C:\Users\bob\work\SecretProj\x.c` becomes
  `%USERPROFILE%\work\SecretProj\x.c` and the existing test at `DiagnosticReportTests.cs:75-95` passes
  with `secret-project` still present); `Redact_PathWithSpaces_FullyRemoved` (the regex stops at the first
  space); `Redact_PosixPathUnderUnlistedRoot_Removed` (`/work`, `/srv`, `/data`, `/c/…`);
  `Carve_UnexpectedFailure_WritesDiagZip_WithNoSourceNames` (trigger: `outputDirectory` under a regular
  file; tree with `secret_dir/secret_blob.h` as a 60-line hex fragment and `secret_entry`; assert no name in
  any zip entry); `Repro_EveryStringValue_MatchesTokenGrammar` (property over a real front-end graph with
  randomised names).
- Traces: `Carve_FileTrace_CaseMismatchedPath_StillRootsCodeFile`;
  `Carve_BuildFileTrace_ObservedDeadCodeFile_IsKept`; `Carve_FileTrace_SourceOutsideRoot_Reports…`;
  `Copy_ObservedGarbage_IsNotPruned`; `Carve_RunTrace_NoCmmObserved_KeepsAllCmm_AndSaysSo`;
  `Paths_FormatEquivalence` (the same random path set rendered as strace, ProcMon CSV and plain list;
  include PID-prefixed lines, `= -1 ENOENT`, `<unfinished ...>`, octal escapes, a comma in a path, device
  paths, an unquoted CSV); a consolidation test for each capture script on a checked-in raw sample
  (`--consolidate-only <raw> <out>` mode makes this cheap).
- `.cmm`: `TraceClosure_AmbiguousDoBasename_KeepsAllCandidates`; `TraceClosure_UnreadableKeptScript_
  Warns` (`CmmTraceClosure.cs:55` turns a read failure into empty text silently); a `DoTarget_Resolution`
  theory (quoted target, `&dir/static.cmm`, `DO` inside `PRINT`, `ChDir.DO`, lowercase, `~~~~/`, CRLF);
  assert the dynamic-DO warning text at `CarveTomlRunTests.cs:389` instead of discarding stderr.
- HeaderCarver: `Carve_KeepsDefinesUsedByHeadersOwnNonDefineLines`;
  `Carve_ConditionContinuationLine_IdentifiersKept` (`HeaderCarver.cs:107` yields only the first physical
  line of a `#if … \` condition, so `defined(B)` on the continuation does not keep `B`);
  `Carve_MaxStage_DefineUsedOnlyByStartupAsm_IsKept` (with `[advanced] maxParseBytes = 200`, which also
  covers the untested `[advanced]` plumbing); `Carve_ReadOnlyHeader_DoesNotThrow` (`File.Delete` at
  `HeaderCarver.cs:88` on a read-only Perforce copy); `Carve_PreservesLineEndings`.
- `CarveCommand` minimal set: usage errors; `--config`/`--stage`/`--why` without a value; missing trace
  inputs; `Carve_OutputInsideSource_Refused_SourceBytesUnchanged` at the aggressive stage; `CmdRel` with
  a relative `directory`, out-of-tree commands, text logs, multiple logs; probe path and probe-failed
  warning; manual `defines`; "no source files found"; big/dense/symbol-budget summary lines
  (`MacroDensityTests.cs:8` cites a CLI integration test that does not exist); unreadable file; the whole
  reference-include resolver (sibling, per-command `-I` base, basename fallback, ambiguity, unresolved,
  recursion, in-tree confinement — the eval-#4/#9 regressions are unpinned); unity/jumbo detection
  (eval-#11); the warning cap; "none of the requested roots"; asm, linker-KEEP, constructor, force-keep and
  function-trace roots through the CLI (`runTraceLogs` appears in no CLI test); `--why` unknown and carved
  symbols; the decisions cap; promote-failure message; manifest and resolved-config content; the crash
  handler; `Init` default path and write failure.
- Resolver and config: `[use] runs`; `[use] builds = []`; multiple compilers; `cs` alias and upper-case
  names; `languages=["cpp"]` on a tree with `.c` files (they are copied verbatim as infrastructure and
  their calls are not traced — warn); `entryPointsFile` missing or with inline comments; stage-order ties;
  `UnknownKey_InEverySection_IsError` (only top level and `[common]` are tested); every `[advanced]` key
  including out-of-range values; `GetBool` forms, and the fact that bare `= yes` is a TOML syntax error
  although the template says yes/no is accepted; `builds = "x"`, `[[builds]]`; a never-throws fuzz for
  `ConfigLoader.Parse`.
- Scraper: spaced `-D NAME`, `/D "X"`, `/U`, `-DNAME=`; quoted compiler path with spaces; relative `file`
  with `../`; missing `directory`; BOM; `ccache`/`sccache`; continuation joining; `cd DIR;`; nested
  Entering/Leaving; `-I` at end of line; `Scraper_ArgvRoundTrip` property.
- Front-end: `PerFileDefines` unit test; deterministic parse-budget timeout (`ParseBudgetMs=1`, 300 KB
  file); force-keep on thrown extraction; `LooksLikeIncludeFragment` boundaries, and a `.c` that looks like
  a fragment must still parse; `##` Prefix/Exact linking; `StripNonCode` across multi-line comments.
- Emitter: `EmitPruned_DefinitionSharingLineWithOtherDeclaration_KeepsTheOther`; dual-signature heuristic
  without gcc; 8 MB include-scan cap; locked source; CRLF preservation; `Begin_DoesNotReapStagingOf
  SiblingOutputWithPrefixName` (`.ccstaging-out-` also matches an output named `out-2`).
- Examples: no test loads any example's own `carve.toml`; multistage, mixed-cpp, csharp and tiny-firmware
  are exercised by nothing; `run.ps1`/`run.sh`/`verify-build.sh` are never run. Add
  `Examples_EveryCarveToml_LoadsAndResolves`, one `Example_X_MatchesReadme` per example asserting the
  README's numbers and file lists (which also fixes the README drift in 8a), `Example_CSharpApp_…_AndBuilds`
  (the SDK is present in CI, so this is a real build-verify with no extra toolchain), and
  `Examples_EmittedTreeAccountsForEverySourceFile`: every file under `src/` is in exactly one of emitted,
  `droppedFiles`, `droppedCmm`, `removedGarbageFiles`, or under an excluded directory. That last property
  also exposes that files copied by `CopyUnscannedIncludes` appear in no manifest list (the manifest
  records `plan.KeptFiles`, not `res.Written`) and that excluded files are listed nowhere.

### Properties worth adding (compiler-free, seeded, sub-second)

1. `dead_open ⊆ dead_closed` for random conditional soups; result length always `lines+1`; never throws.
2. `EvaluateCondition` agrees with a C-semantics reference whenever it returns a definite answer.
3. `EmitPruned` output lines are a subsequence of input lines; every kept span is present verbatim; every
   preprocessor line survives; headers are byte-identical.
4. Monotonicity at the CLI: adding an entry point, a trace name or a trace file never removes a C/C++
   file from the emitted tree; `kept(closed) ⊆ kept(open)`.
5. The independent link oracle (V1) over random call graphs with macro-wrapped calls, pointer tables and
   token paste.
6. `TraceClosure`: `Kept ⊇` an independent regex BFS; `observed ⊆ Kept`.
7. `HeaderCarver`: every non-`#define` line survives in order; every define named in a surviving line or a
   kept body survives.
8. Never-throw fuzzers for `ConfigLoader.Parse`, `BuildLogScraper.Parse`, `FileAccessTrace.Paths`,
   `TraceFile.Parse`, `CmmFrontEnd.BuildGraph`.

## 9. Order of work

Each step is one commit (commit and push, report the version).

1. **Safety net:** V1 (emitted-tree verify) with the sixteen repro trees as scenario tests that currently
   exit 3. O1 (output directory guard). D1 (diagnostic redaction).
2. **Preprocessor soundness:** PP5, PP3, PP1 step 1, PP2 step 1, PP4. After this, closed-world is looser
   and correct.
2b. **Capture scripts** (SC-A1–A7, SC-B1–B6, SC-A11, SC-C1) — before the next real-tree capture.
3. **Build-log soundness:** BL1, BL2, BL3.
4. **Name tables and roots:** N1, R1.
5. **Emit soundness:** F1, E1, H1, CS1, CS2, K1.
6. **`.cmm`:** CM1, then CM2 per the owner's decision; RB1.
7. **Traces:** T2, T3, T4, T5, then T1 per the owner's decision; regenerate the multistage README.
8. **Name-free summary** (section 6) and U1–U9.
9. **Precision:** P1, P7, P2, then P3–P6. Measure each on the public corpus before and after.
10. **CI and tests** (8b SC-E, 8c): Linux leg, BuildVerify split, example tests, skippable facts.
11. **Docs** (section 8) and the README drift test; dead-code removal; remaining RB and SC-D/SC-F items.

After steps 2–5 run the full fast suite, then the gated oracles once (ground truth, build report) under
ComputeWarden to confirm nothing regressed in keep counts beyond what the fixes intend.

## 10. Repro appendix

All repros were run with `src/CodeCarver.Cli/bin/Debug/net8.0/CodeCarver.Cli.exe carve src --config
carve.toml` from the directory that holds `carve.toml`, on Windows. Unless stated, `carve.toml` is:

```toml
outputDirectory = "out"
[common]
entryPoints = ["main"]
languages = ["c"]
```

The file contents for each repro are given inline in its finding. The link failure in F1 was confirmed
with gcc 11.4 under WSL. Linux behaviour of the CLI itself was not exercised: WSL on the review machine
has only the .NET 7 runtime, and CI runs on Windows only, so every path-handling claim marked [CODE]
deserves a Linux run once a .NET 8 runtime is available there.
