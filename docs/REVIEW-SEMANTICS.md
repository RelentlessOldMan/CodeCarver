# CodeCarver semantic correctness review (C/C++)

Reviewed at `1.0.242+5cf6a6ccc`, 2026-10-16, against `CodeReview_CodeCarver.md`. Phase 1 (audit) is
sections A–M; Phase 2 (fixes) is section N, updated as fixes land.

Method: five independent auditors (preprocessor/build config; C semantics and linkage; C++; output rewriting
and determinism; reachability, roots and fallbacks) each wrote small adversarial trees with a build log,
carved them at a file-level stage and at a `carveSourceFileContents = true` stage, then compiled, linked and
ran the original and the carved tree (w64devkit gcc/g++ on the host; arm-none-eabi with a
`--gc-sections` superset oracle for linker-script cases) and compared output. All names in repros are invented.

Evidence tags: **[REPRO]** reproduced with the CLI; **[CODE]** read end to end; **[SUSPECT]** plausible, unconfirmed.

## A. Executive summary

The engine (graph, BFS, staged emit, file-level closure) is sound for what it is told. The defects are in
what it is told: regex scanners at the edges that miss common spellings, a `#if` model with several holes,
and a C++ front end that has no notion of the calls the language makes for you (operators, destructors,
vtables, static initialization). Every one of the false removals below ended with `verify : OK` and exit 0,
because verify only fails on a name whose definition it recognises in a dropped file, and it recognises the
same shapes the front end does.

Largest false-removal risks, in order:

1. Keep attributes in other spellings (`__constructor__`, `__used__`, `__section__`, `[[gnu::section]]`,
   nested parentheses, function-pointer declarators): constructors, vector tables and `.init_array` entries dropped.
2. C++ static-initialization registration, operators, destructors and vtables: dropped wholesale.
3. Closed-world `#if` holes: system-header macros, `#elifdef`, directives after a comment, multi-line comments
   in a condition, a file's `#define` undone by a later `#include`, unioned per-build defines.
4. A TU that was not parsed (too big, unreadable) is dropped instead of kept whole.
5. Keep and registration macros defined outside the tree; `weakref`; assembly reached through `.include`.

Largest over-retention: file-scope initializer references charged to the whole file (E8), struct designators
counted as references (B12), anonymous-namespace and qualified C++ calls resolved by last name (C11).

Confidence: high for every [REPRO] finding (each has a scratch tree that fails to link or changes output).
Limits: no MSVC, IAR, TI, AVR or PIC toolchain was available, so non-GNU findings are carve-only; C++20
(concepts, modules, coroutines) was not covered in depth.

## B. Architecture overview

Source walk → build-log scrape (per-file `-D/-U/-I`, response files) → macro world (ambient `#define`s from
tree and `-I` headers, compiler probe, exact mode via the real compiler) → `PreprocessorScanner` dead-line
maps → tree-sitter parse per file (`TreeSitterFrontEnd`, C and C++ queries) → graph (File, Function, Global,
Macro, Type nodes; Calls, References, AddressTaken, DefinedIn, Includes edges) → roots (entry points,
implicit keep attributes, keep macros, asm, linker-script KEEP, traces, binaries) → BFS → per-stage plan →
file-level emit closure (unreached code in kept files keeps its callees) → emit (whole files, or pruned
functions/tables; header `#define` carving) → `EmittedLinkCheck` (verify: token scan of the emitted tree for
uses defined only in dropped files) → reports.

## C. Ranked findings

| Rank | ID | Finding | Severity | Correctness risk | Precision | Conf. | Effort |
|---|---|---|---|---|---|---|---|
| 1 | K1 (E4,B1,B2) | Keep/section attribute spellings, nested parens, fn-pointer declarators missed | Critical | silent behaviour change, lost vector table | – | High | M |
| 2 | P1 (A11,E1,E2) | Unparsed TU (over maxParseBytes, unreadable) dropped | Critical | link fail | – | High | S |
| 3 | X1 (C1) | C++ namespace-scope objects with dynamic init in unnamed files dropped | Critical | registrations lost | – | High | M |
| 4 | X2 (C2) | Operators, conversion operators, destructors, replacement new/delete not definitions | Critical | link fail / allocator silently replaced | – | High | M |
| 5 | X3 (C3,D5) | No vtable model: virtuals not called by name dropped | Critical | link fail / override silently lost | – | High | M |
| 6 | W1 (A1,D4) | Closed world treats system-header macros and MinGW `WIN32` as undefined | Critical | link fail | – | High | M |
| 7 | W2 (A7) | A file's `#define` survives a later `#include` that undefines it | Critical | link fail | – | High | S |
| 8 | W3 (A4) | Manual defines of several `[builds.x]` unioned as certain | Critical | link fail | – | High | S |
| 9 | W4 (A2) | `#elifdef` / `#elifndef` unhandled | Critical | link fail | – | High | S |
| 10 | M1 (A9,B3) | Keep/registration macros from `-D` or headers outside the tree ignored | Critical | registrations lost | – | High | M |
| 11 | H1 (D7) | Header carving drops `NDEBUG`/feature-test macros read only by system headers | Critical | silent behaviour change | – | High | S |
| 12 | L1 (B4) | `weakref("x")` target not followed | Critical | feature silently off / crash | – | High | S |
| 13 | O1 (D2) | Output-overlap guard ignores junctions/symlinks | High | deletes source | – | High | S |
| 14 | W5 (A3,A10) | Directive after a comment; multi-line comment inside `#if` | High | link fail | – | High | S |
| 15 | W6 (A5,A6) | Function-like `-D` keyed with params; `-U` of a built-in ignored | High | link fail | – | High | S |
| 16 | W7 (A8) | Headers of a TU absent from the log stay closed-world | High | link fail | – | High | S |
| 17 | S1 (E6,B8,D10) | Assembly/linker-script `#include`/`.include` targets not followed | High | link/assemble fail | – | High | M |
| 18 | S2 (D12) | `.globl x\nx:` inside one asm literal not a definition | High | link fail | – | High | S |
| 19 | S3 (E7) | Linker-script `KEEP()` with nested parens | High | lost table | – | High | S |
| 20 | L2 (B5) | `alias` with parenthesised params or on variables | High | link fail | – | High | S |
| 21 | X4 (C5,C6) | Explicit template specialization not a definition; explicit instantiation pruned | High | link/compile fail | – | High | M |
| 22 | X5 (C4) | Range-for `begin/end`, structured-binding `get` implicit calls | High | link fail | – | High | S |
| 23 | X6 (C7) | Lambda/function-pointer parameter shadows a function for the rest of the body | High | link/compile fail | – | High | S |
| 24 | X7 (C8) | Intra carve deletes brace-initialised class objects with ctor side effects | High | silent | – | High | S |
| 25 | R1 (D6,D9) | Prune leaves orphan bodies across `#if` branches | High | compile fail | – | High | S |
| 26 | B7 | Unreached `static` header function at -O0 not closed over | High | link fail | – | High | S |
| 27 | B9 | Rename/paste-macro definitions not resolved by real symbol | High | link fail | – | High | M |
| 28 | E3 | Computed `#include` of a `.c` | High | build fail | – | Med | M |
| 29 | T1 (E5,B10) | Non-GNU keeps (`__root`, `#pragma RETAIN/required`), ISR conventions | High | dropped handlers | – | Med | M |
| 30 | S4 (B6) | Section bracketed by symbols without KEEP and without gc | High | empty table | – | Med | M |
| 31 | R2 (D1) | Dead function whose return type defines a struct | Med/High | compile fail | – | High | S |
| 32 | R3 (D8) | Stub casts attribute words as parameters | Med/High | compile fail | – | High | S |
| 33 | X8 (C9) | Out-of-line definitions qualified 5+ deep | Medium | link fail | – | High | S |
| 34 | V1 (C10,A12) | Verify blind to C++ uses and to header dead-line uses | Medium | hides others | – | High | L |
| 35 | R4 (D3) | BOM lost when line 1 is dropped | Medium | encoding | – | High | S |
| 36 | R5 (D11) | Stage names differing only by case collide | Medium | overwritten stage | – | High | S |
| 37 | E8 | File-scope initializer refs charged to the File node | Medium | – | over-retention | High | M |
| 38 | B12 | Struct designators counted as references | Medium | – | over-retention | Med | S |
| 39 | C11 | Anonymous namespace / qualified calls resolved by last name | Medium | – | over-retention | High | M |
| 40 | B11 | Console prints pre-closure files/dropped lines | Medium | misleading | – | High | S |
| 41 | E9 | `--why` answers the first stage only, unlabeled | Low | misleading | – | High | S |
| 42 | E10 | Excluded compiled TU used by kept code: no warning | Low | silent | – | High | S |
| 43 | E11 | Empty entry point / exclude path accepted | Low | – | – | High | S |
| 44 | B13 | Unused function-like macro body counts as a use | Low | – | over-retention | High | S |

## D. Detailed findings

Repro trees live in the review scratch area (one directory per case, with sources, `carve.toml` and the
compile log); the shape of each is given here so it can be rebuilt as a test.

**K1 — Keep attributes missed by spelling (Critical, [REPRO]).**
`__attribute__((__constructor__))`, `__destructor__`, `__used__`, `__section__(".x")`, `__attribute((section))`,
`[[gnu::section(".x")]]`, attributes with two levels of parentheses (`used, aligned(sizeof(void *))`,
`constructor(101 + (0))`), and keep attributes on function-pointer declarators
(`void (* const vectors[])(void) __attribute__((section(".isr_vector"))) = {…}`,
`static void (*const ia)(void) __attribute__((used, section(".init_array"))) = early_a;`) are not rooted.
Observed: a constructor that sets a flag is dropped and the program prints 0 instead of 42; a vector table is
removed at both stages. Cause: `KeepKeyword` uses `\b` (fails inside `__x__`); `AttrBlock` allows one nesting
level; `NameBeforeAttr`/`NameAfterAttr` cannot read `(*const name[])(…)` (TreeSitterFrontEnd.cs:67-125,
IRootProvider.cs:134-141). Fix: one attribute scanner shared by the front end and root provider: balanced
parentheses, `__x__` normalised to `x`, all three attribute spellings, declarator name from the innermost
identifier of a parenthesised declarator. Test: one file per spelling with an observable side effect.

**P1 — Unparsed TU dropped (Critical, [REPRO]).** A `.c` over `maxParseBytes` is reported "kept whole via
#include-closure" and then dropped; an unreadable `.c` is reported "kept whole, not parsed" and dropped. A
placeholder replaces it, verify says OK with a "not checked" note, link fails. Cause: the skip/unreadable path
creates no nodes and nothing roots a TU (CarveCommand.cs skip-parse and `ReadRel` paths). Fix: an unparsed
file that is a compiled TU (or any non-header code file) is rooted whole. Test: TU over the cap called from main.

**X1 — C++ static-initialization registration dropped (Critical, [REPRO]).** Files holding only
`static Registrar r{"B"};`, `namespace { Registrar r("C"); }`, `static int rf = reg_ret("F");`,
`static auto re = []{…}();` etc. are dropped; the registry prints nothing. Cause: namespace-scope objects are
kept only when their file is kept; nothing roots the file. Fix: root namespace-scope objects with dynamic
initialization (class type with a constructor, call or lambda initializer) in C++ TUs.

**X2 — Operators, destructors not definitions (Critical, [REPRO]).** `Vec operator+(Vec,Vec)`,
`Vec::operator-() const`, `Meters::operator double() const`, `Shape::~Shape() = default;` (key function) and a
replacement `operator new/delete` live in files that are dropped. Cause: `CppFrontEnd` DefsQuery has no
`operator_name`, `operator_cast`, `destructor_name` patterns. Fix: capture them as functions; keep operator and
destructor definitions whenever their class/operand type is used (conservative), and root replacement
`operator new/delete`.

**X3 — No vtable model (Critical, [REPRO]).** Intra carve prunes `Base::b` that only the vtable references; a
`streambuf` override called only by the library is dropped. `EdgeKind.VtableEntry` exists but nothing emits it.
Fix: virtual/override member functions of a class are rooted when the class is constructed (ConstructorGate) or
otherwise kept; in-class virtual definitions are never pruned.

**W1 — System-header macros undefined in closed world (Critical, [REPRO]).** `#ifdef O_BINARY` after
`<fcntl.h>`, `#ifdef SIGPIPE` after `<signal.h>`, `#ifdef WIN32` on MinGW: callee files dropped. Cause: angled
includes and system dirs are never scanned; `NotFromTheBuild` is a short fixed list. Fix: a name tested in a
TU that includes an angled header not found in the tree, and defined by no visible file or flag, is unknown.

**W2 (A7) — `#define` then `#include` that `#undef`s it (Critical, [REPRO]).** Cause: file-local `Set` wins over
ambient and `#include` is not modelled. Fix: at each `#include`, names that any file `#undef`s or defines make
the file-local value unknown.

**W3 (A4) — Per-build defines unioned (Critical, [REPRO]).** `[builds.a] defines=["BOARD_A","LED=1"]`,
`[builds.b] defines=["BOARD_B","LED=0"]` are merged and applied as certain. Fix: a name that differs across
builds or is missing from one is unknown.

**W4 (A2) — `#elifdef`/`#elifndef` (Critical, [REPRO]).** Not handled in `PreprocessorScanner.HandleDirective`.

**M1 (A9,B3) — Keep macros outside the tree (Critical, [REPRO]).** `-DAUTO_INIT=__attribute__((constructor))`,
or `SYS_INIT(fn,…)` from an SDK header under `-I` outside the root: files dropped, behaviour changes. Cause:
keep-macro scan covers tree files only. Fix: scan `-D` values and the out-of-tree headers already read.

**H1 (D7) — Header carving drops `NDEBUG` (Critical, [REPRO]).** Fix: never strip reserved (`_X`, `__x`) names
or the well-known feature-test/configuration macros.

**L1 (B4) — `weakref` (Critical, [REPRO]).** `static int plugin(void) __attribute__((weakref("plugin_impl")));`
— target file dropped. Fix: treat `weakref("x")` like `alias("x")` in front end and verify.

**O1 (D2) — Junction output (High, [REPRO]).** `outputDirectory` is a junction to an earlier output whose
`carved/` is the source: the source is wiped. Fix: compare resolved final paths.

**W5 (A3, A10) (High, [REPRO]).** `/* old */ #if 0` is not seen as a directive, so its `#else/#endif` corrupt
the frame stack; a `/* … */` spanning lines inside `#if defined(A) /* … \n … */ && defined(B)` truncates the
condition to certainly-true. Fix: recognise directives after a leading comment; a directive whose comment is
open at end of line joins the next line.

**W6 (A5, A6) (High, [REPRO]).** `-DTRACE(x)=x` is recorded under the key `TRACE(x)`; `-U__STRICT_ANSI__` of a
probed built-in is ignored. Fix: strip params in the spec name; carry `-U` into the per-file table.

**W7 (A8) (High, [REPRO]).** A header included by a TU that no compile command names is still evaluated
closed-world. Fix: such headers use the open table.

**S1 (E6, B8, D10) (High, [REPRO]).** `startup.S` `#include "handlers.inc"` (`.quad uart_isr`), `.include
"body.s"`, and headers included only from `.S`/`.ld.in` files: roots and headers lost. Fix: follow includes from
assembly and linker-script sources.

**S2 (D12) (High, [REPRO]).** `__asm__(".globl asm_defined\nasm_defined:\n…")` in one literal: defining file
dropped. Fix: an escape (`\n`, `\t`) or `;` counts as a separator before `.globl` and labels.

**S3 (E7) (High, [REPRO]).** `KEEP(*(EXCLUDE_FILE(*crt0.o) .mytab.*))` stops at the first `)`. Fix: balanced body.

**L2 (B5) (High, [REPRO]).** `alias` on a function with a function-pointer parameter, or on a variable. Fix:
balanced parameter list; variable aliases.

**X4 (C5, C6) (High, [REPRO]).** `template<> int codec<int>(int v){…}` not a definition; intra carve removes a
template whose `template int twice<int>(int);` remains. Fix: capture `template_function` declarators; an
explicit instantiation references its template.

**X5 (C4) (High, [REPRO]).** `for (int x : r)` needs `begin`/`end`; `auto [u,v] = p;` needs `get`.

**X6 (C7) (High, [REPRO]).** A lambda parameter or a function-pointer parameter named `other` hides the global
`other` for the rest of the enclosing body (`CollectLocals` scopes it to the enclosing block).

**X7 (C8) (High, [REPRO]).** `Registrar arr[] = {{"X"}, {"Y"}};` pruned as an "aggregate table".

**R1 (D6, D9) (High, [REPRO]).** A head with alternative bodies under `#ifdef/#else`, or two heads whose `{` sits
on its own line inside each branch: prune leaves an orphan body. Fix: a dropped span must have balanced
`#if` nesting, else keep it.

**B7 (High, [REPRO]).** `static int hdr_static(void){ return dep_b(); }` in a header, unused, built at -O0:
gcc still emits it and needs `dep_b`. `EmitClosure.RetainedInHeader` excludes statics. Fix: treat static
non-inline header functions as emitted.

**B9 (High, [REPRO]).** `#define spi_xfer spi1_xfer` then `int spi_xfer(…){}`; another file calls `spi1_xfer`
without the header. Also `int DRV_NAME(uart)(void)` via a paste macro. Fix: resolve unresolved uses through
the rename map; expand a function-like macro used as a declarator name.

**E3 (High, [REPRO]).** `#include XSTR(IMPL_FILE)` with `-DIMPL_NAME=engine` — the included `.c` is dropped.

**T1 (E5, B10) (High, carve-only).** IAR `__root`, TI `#pragma RETAIN(x)`, `#pragma required=x`, MSVC
`#pragma comment(linker,"/include:x")`, AVR `ISR(vec){…}`, msp430 `interrupt(V)`, `#pragma vector=` +
`__interrupt`, XC32 `__ISR(…)`: all dropped.

**S4 (B6) (High, [REPRO]).** A section bracketed by `__start = .; *(.drivers) __end = .;` without KEEP, linked
without `--gc-sections`: the table is emptied.

**R2 (D1), R3 (D8), X8 (C9), R4 (D3), R5 (D11)** — as in the table: `struct pt {…} make_dead_pt(void){…}` loses
`struct pt`; stub emits `(void)unused;`; `a::b::c::d::E::m()` not captured; BOM dropped with line 1; `[stages.Fast]`
and `[stages.fast]` write the same folder.

**V1 (C10, A12) (Medium, [CODE]).** Verify skips names after `.`/`->`, knows no operator/destructor/
specialization definitions, and is silent about uses on dead lines inside headers. It is why every finding
above is silent. Recommendation, not a quick fix: a real compile/link of the emitted tree when a toolchain is
known is the only independent check.

**E8, B12, C11, B13 (precision); B11, E9, E10, E11 (diagnostics)** — as in the table.

## E. False-removal risks

K1, P1, X1, X2, X3, W1–W7, M1, H1, L1, L2, S1–S4, X4–X8, R1–R3, B7, B9, E3, T1. Trigger conditions are in each
finding; the consequence is a link/compile failure of the carved tree, or (K1, X1, X2 replacement allocator, X3
library-called override, X7, H1, L1, S4) a carved program that builds and behaves differently.

## F. Excessive-retention opportunities

E8 (a whole file's initializers keep every function any initializer names), B12 (designators `.init`/`.read`
keep every same-named global function), C11 (anonymous-namespace helpers and `A::run` keep `B::run`), B13.

## G. Parsing and language coverage gaps

C++ operators/conversions/destructors, explicit specializations and instantiations, implicit calls (range-for,
structured bindings), vtables, static initialization; `#elifdef`; directives after comments; non-GNU keep
syntax; asm include chains.

## H. Dependency graph improvements

Emit `VtableEntry` edges; attribute file-scope initializer references to the declaration they initialise;
resolve qualified calls by scope; file-local anonymous namespaces.

## I. Source rewriting improvements

Dropped spans must keep `#if` balance (R1); a function that defines a type in its return type keeps the type
(R2); stub parameter names from the parser (R3); BOM preserved (R4).

## J. Adversarial test corpus

Executed: ~120 trees across the five areas (preprocessor 13, C 30+, C++ 31, rewriting 25, reachability 21), each
built and run original vs carved. The regression tests added in Phase 2 are the permanent form of these.

## K. Architectural recommendations

1. One attribute/declarator scanner shared by front end, root providers and verify (K1, L1, L2, T1).
2. An unparsed or unread code file is always kept whole (P1).
3. A real link of the emitted tree as the verify step whenever a toolchain is known (V1).
4. A C++ "language calls for you" model: constructors, destructors, operators, vtables, static init (X1–X5).

## L. Coverage report

Checked and correct (summarised from the auditors): earlier review fixes PP1–PP5, BL1–BL3, N1, R1, H1, F1, V1
hold; BFS, cycles, cyclic includes, shadowing by locals, K&R, token-paste definers, function-pointer tables,
same-named statics across TUs, weak/strong pairs, `alias`, `cleanup`, inline-asm references, standalone `.S`
references, ENTRY/EXTERN/PROVIDE, `KEEP(*(SORT(…)))`, multi-declarator globals, braces in strings/comments,
CRLF/Latin-1 preservation, path refusal (textual), determinism across runs, thread counts and path forms;
hostile input (deep nesting, huge lines, binary files, malformed TOML/logs) without crash or hang; C++ static
members, member-function pointers, NSDMI/default arguments, namespace aliases, export-macro class heads.

## M. Remaining unknowns

Non-GNU toolchain behaviour (only carve-level evidence); C++20 concepts/modules/coroutines; SFINAE-only
dependencies; whether real trees hit P1 (generated `.c` blobs over 20 MB) and K1 (`__used__` spelling is common
in kernel-style code).

## N. Fixes applied

Each fix began with a regression test that failed without it, and the full suite passed before each release.
File-level tests can be masked by the verify fallback (it re-keeps the file defining a name kept code uses), so the
graph-side fixes are also pinned by graph-level tests.

| Finding | Fix | Release |
|---|---|---|
| K1, S3 | Keep attributes in every spelling | 1.0.243 |
| P1 | A source file the carve could not look inside is kept whole | 1.0.244 |
| X1 | C++ objects that run code before `main` are roots | 1.0.245 |
| X2 | C++ operators, conversions and destructors are definitions | 1.0.246 |
| X3 | The virtuals of every class the carve keeps instantiated are kept | 1.0.247 |
| W2, W3, W4 | `#elifdef`/`#elifndef`; disagreeing per-build defines and `#include` make macros unknown | 1.0.248 |
| W1 | Names a missing system header may define stay unknown in the closed world | 1.0.249 |
| M1 | Keep and register macros defined outside the tree are honoured | 1.0.250 |
| H1, L1, L2 | Header carving keeps system-read macros; `weakref` and more alias shapes | 1.0.251 |
| O1 | The output-overlap guard follows junctions and symlinks | 1.0.252 |
| W5, W6, W7 | Directives after comments, `-U` and `F(x)` defines, headers of unlogged files | 1.0.253 |
| S1, S2 | What assembly `#include`s is followed; symbols defined by top-level `asm` | 1.0.254 |
| X4–X8 | Specializations, instantiations, range-for, structured bindings, parameter scope (X7 was already fixed by X1) | 1.0.255 |
| R1 | The intra carve removes whole `#if` branches only | 1.0.256 |
| B7 | Static functions in headers count as emitted | 1.0.257 |
| B9 | Definitions spelled through a rename or paste macro | 1.0.258 |
| E3 | Computed `#include`s are followed (object and function-like macros, `#`, `##`; an unknown macro matches any name) | 1.0.259 |
| T1 | Keep and interrupt spellings of other toolchains (`#pragma RETAIN`/`required`/`vector`, `__root`, `__interrupt`, `ISR()`, Keil `interrupt N`) | 1.0.260 |
| S4 | Input sections between `sym = .` assignments are tables, KEEP or not | 1.0.261 |
| R2 | A dead definition whose head defines a used struct/union/enum is kept whole | 1.0.262 |
| R3 | Stub parameter names skip attribute syntax and trailing attribute macros | 1.0.263 |
| R4 | The UTF-8 BOM survives when line 1 is dropped | 1.0.264 |
| R5 | Section names that differ only by case are a config error | 1.0.265 |
| E11 | Empty list items, and excludes that name no directory, are config errors | 1.0.266 |
| E10 | Warning when `excludeDirectories` removes a file the build log compiles | 1.0.267 |
| E9 | `--why` answers every stage, labeled | 1.0.268 |
| B11 | The console's `files`/`dropped` lines come from each stage's final plan; an empty verify says so | 1.0.269 |
| B12 | Designators (`.reset = f`) and members are not references | 1.0.270 |

### Deferred

- **V1 (verify blind spots).** Architectural, as section K says: the only independent check is a real compile and
  link of the emitted tree when a toolchain is known. Not attempted here.
- **E8 (initializers charged to the file node).** Tried and reverted. Charging a table's references to the table
  alone lets the intra carve cut an unreferenced table, and a vector table is exactly that: no C code names it, but
  the link keeps it. The arm-gcc build test broke. The fix needs to know which tables the link retains (KEEP,
  section, `used`, `-u`), and to charge the file only for those.
- **C11 (qualified and anonymous-namespace calls resolved by last name).** Graph nodes carry unqualified names, so
  `A::run` and `B::run` share one name. Precise resolution needs qualified node identities throughout the C++ front
  end. It over-retains but is sound.
- **B13 (unexpanded function-like macro bodies count as uses).** The link check reads every `#define` body as code on
  purpose (CheckClosureTests pins it). Telling an unexpanded macro apart needs each file's macro-use set, which the
  scan drops to save memory. It also has to handle a `.c` that another file `#include`s, or dropping the static
  breaks that build. The cost is one kept static per such macro.
