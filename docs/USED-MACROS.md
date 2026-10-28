# Used macros: let the build say which defines it needs

The max stage's header carve (`carveHeaderFileContents = true`) drops the `#define`s in the big headers that no kept
file needs. Without help it works this out by modelling the preprocessor: every `#if`, and every paste macro
(`CAT(KQ_, n)`) that can build a name. On register-map trees with thousands of paste macros the model can't rule much
out, so it keeps nearly everything, and it can take hours to find that out.

The compiler already knows the answer. GCC's `-E -dU` prints a `#define` for every macro a compile expanded or tested
and an `#undef` for every name it tested and found undefined. If each compile of the real build prints that once,
the carve keeps a big header's define only if some compile used it. The carve doesn't need its own model of `#if`
or `##`.

This is GCC only: Clang accepts `-dU` and prints nothing for it. It costs one more preprocess per compile. For a
Clang build, the carve can ask Clang itself instead (see [Clang builds: pp-trace](#clang-builds-pp-trace)).

## What to collect

Collect it on the machine that runs the real build, during that build: every target, every configuration, every
compile, and every other preprocessor run (a linker script or a text file run through `gcc -E`, `.S` files, `cpp`).
Each preprocessor run writes **one file** to a folder of its own:

```
#codecarver-cwd /build/tree/src           optional, first line: the directory the compile ran in
# 0 "drivers/uart.c"                      the -E -dU output's #-lines: line markers, then
# 0 "<built-in>"
#define KQ_UART_CTRL_OFF 0x0               #define / #undef for each macro used or tested
#undef KQ_HAS_DMA
...
#codecarver-ok                            last line, written only when the -E -dU run exited 0
```

- Keep the line markers. Don't pass `-P`, because the first marker names the source the file belongs to.
- Write `#codecarver-ok` only after the compiler exited 0. A file without it counts as cut short.
- The text lines of the `-E` output aren't needed. `grep '^#'` keeps the file small.
- Any file name works. The carve reads every file under the folders it is given.

[`examples/used-macros`](../examples/used-macros) is a worked example: a small tree, a wrapper that sits in front of
`gcc` and writes these files, and a script that builds, carves and rebuilds every carved stage (CI runs it). The
wrapper is meant to be adapted, not used as is. Its `ADAPT:` comments mark what depends on the build: the driver names,
how it finds the real compiler, one source per command, and the options that write files of their own.

## The config

```toml
[builds.main]
buildLogs  = ["build.log"]          # required: the log of the same build
usedMacros = ["du/"]                # files or folders of them
```

`usedMacros` needs `buildLogs` in the same build: the log's compile commands are what the files are checked against.
Paths in the files are mapped with `[advanced] pathMap` like the log's. Several builds (one per target) are unioned,
which is what you want: a define any target used is kept.

## When the carve trusts the files

The carve trusts the names only when:

1. every compile command in the build logs is matched, by its source file, by a complete file (a source compiled
   twice, say in two configurations, needs two files), and
2. in each stage, every source file the stage keeps has a compile command in the logs.

A compile the carve hasn't heard from could use a define the others didn't. If the first check fails, the run says why
(`NOT trusted (...)`) and the header carve models the paste macros itself, as it does without the key. The second is
checked per stage, on the files that stage keeps: a tree holds many source files no build compiles, and while the
carve drops them they need no command. A stage that keeps one says so (`headerCarve.keptUnitsWithoutCommand`) and
models the paste macros itself; the other stages still use the names. Names from every
file are still read. Names count even from files that aren't complete or don't match a command, so an extra file
can keep more, never less.

## What it keeps

When it trusts the files, a big header keeps:

- each `#define` some compile used or tested (`keptByUsed`), and
- each `#define` named in a kept file that isn't C or C++ source, such as a linker script, a debugger script or a
  data file, plus the names in those defines' bodies, followed for up to 16 rounds (`keptByOther`). A debugger
  script can evaluate a macro from the debug info, so `.cmm` files count.

All non-define lines stay as they are: typedefs, enums, inline functions and `#if` lines.

## Clang builds: pp-trace

For a Clang build, nothing needs collecting during the build. LLVM's `pp-trace` (from clang-tools-extra, in the
`bin` folder of an LLVM release) runs Clang's preprocessor on a compile command and prints every macro it expanded and
every name an `#ifdef`, `#ifndef`, `#elifdef`, `#elifndef` or `defined` tested: the same names `-dU` gives. The
carve runs it on each compile command of the build log, on the machine that runs the carve:

```toml
[builds.main]
buildLogs = ["build.log"]                                  # required: the commands pp-trace runs
ppTrace   = "C:/Program Files/LLVM/bin/pp-trace.exe"       # or a name on PATH
```

- **Install the LLVM release your build's Clang comes from**, on the carve machine. Any host works: Clang
  preprocesses for any target, so a Linux build's log can be traced on Windows. A different version can answer
  `__has_builtin`, `__has_include` and its predefined macros differently, so the carve prints the version it ran
  (`world.ppTrace.version`). Nothing is bundled or downloaded.
- The tree must be readable at the paths the log names, after `[advanced] pathMap`, like for the build's own
  preprocessor run. A command doesn't run (`missingPaths`) when the directory it ran in, its source, or an `-include`
  or `-imacros` file is missing here; the `missing.*` keys say which. A missing include directory is skipped as Clang
  skips it, and the command runs. Outside the carve root it is the build machine's toolchain folders
  (`ranWithoutOutsideDirs`). Inside, the tree the build compiled didn't have it either: a directory the build never
  had, or one it made for its output, such as generated headers (`ranWithoutDirsInRoot`, `missingDirsInRoot`, and of
  those `missingDirsInRootFromBuild`: at, above or under a folder some `-o` writes to). If a header only such a
  directory held is needed, the compile fails and says so. Generate those headers in the tree before the carve, or
  the commands that include them fail.
- A header outside the tree that this machine lacks (a host system header, included by a host-side unit that the target's
  toolchain has no copy of) doesn't fail the command. It's run again with an empty header in its place, searched after
  every real include directory, up to 16 such headers per command (`succeeded.pastStubbedHeaders`, `headersStubbed`).
  The carve carves no define of such a header. All that's lost is a branch of the unit on that header's own macros.
  This never applies to a header the tree has elsewhere, a C standard library header, or a command missing an
  include directory inside the root (a header the build makes): those still fail it.
- The `failedBy.*` keys say why commands failed. Many `headerNotFound.cLibrary` means the build's C library isn't
  found here. When the log names its folders (`-isystem`), map them to a copy on the carve machine with
  `[advanced] pathMap`. When it doesn't, the build's driver found it by itself, beside the compiler, and pp-trace's
  Clang looks beside pp-trace instead. Then say where it is with `ppTraceArgs`:

  ```toml
  [builds.main]
  ppTraceArgs = ["--sysroot=D:/llvm-embedded/lib/clang-runtimes/aarch64-none-elf"]   # or ["-isystem", "D:/libc/include"]
  ```

  `ppTraceArgs` are added to every command, after its own flags, with paths on the carve machine. Only the C
  library's headers are needed, and the same C library is enough; the folder layout needn't match the build machine's.
- An option pp-trace's Clang doesn't know (a vendor compiler's own flag) is an error it prints and goes on from: by
  itself it doesn't fail a command, which still exits 0 (`optionErrors` counts the runs that printed one). If one
  does stop a command (`failedBy.option`), `ppTraceDropOptions` takes it out of every command: a pattern per
  argument, `*` for any text, or an option and its value as two words (`"-Xkq *"`). Take out only options that
  don't change what the preprocessor sees: one that defines a macro or adds an include directory changes the names.
- pp-trace parses the code after preprocessing it, and that parse reports errors a build's compiler wouldn't: a
  builtin, attribute or inline asm only the vendor compiler knows. Those don't fail a command. Clang's error limit is
  off (`-ferror-limit=0`, after the command's own flags), so the preprocessor runs to the end of the source whatever
  the parse reports, and each error's category (`-fdiagnostics-show-category=name`) says which stage reported it. A
  run that exits 1 with only the parse's errors ("Semantic Issue", "Parse Issue", "Inline Assembly Issue", ...) and
  rejected options counts as clean (`succeeded.pastParseErrors`). An error the preprocessor reports ("Lexical or
  Preprocessor Issue", such as an `#if` it couldn't evaluate, or an `#error`) still fails it: what it preprocessed
  isn't what the build did. A warning made an error (`[-Werror,-Wfoo,...]`, by a `#pragma clang diagnostic error`:
  the carve takes `-Werror` off the command) changes nothing the preprocessor puts out, at any stage, so it doesn't
  fail a run either (`succeeded.pastWarningsAsErrors`). `errorFlags` names the warning flags the errors carried.
- A driver named for its target (`aarch64-none-elf-clang`) gets that target passed as `--target`. `clang++` and the
  file's extension pick C++.
- `.S` and `.sx` files run too: pp-trace parses them as C after preprocessing, so their parse errors are expected;
  only a fatal error (a missing include) fails one. `.s` and `.asm` files have no preprocessor and are skipped
  (`notPreprocessed`).
- Text files the build runs through the preprocessor (a linker script, `clang -E` on a template) aren't compile
  commands in the log. Their names come in as for any kept non-C file (`keptByOther`).
- `ppTrace` and `usedMacros` say the same thing; set one of them. One carve runs one pp-trace.

The carve trusts the names when every command ran clean (exit 0, or only the parse's errors, within 30 minutes each). Otherwise it says why and the
static carve runs. As for `usedMacros`, a stage that keeps a source file with no compile command models the paste
macros itself.

**Memory.** Each pp-trace holds about 0.5 KB for every define in the headers its compile includes: 2 GB for a compile
that includes 3.8 million. That memory is pp-trace's, not the carve's, so the runs share what `memoryStopMb` leaves
the carve (or the machine's memory without it), each counted with any process it starts. The `compiler` key's runs
share it the same way. They run at most one per core but one: one at first, one more at
once for each that finishes, and a new one starts only when the running ones, each counted at no less than the
most one has needed so far, plus that again, fit. If the running ones grow past it, the newest are stopped and run
again later (`mem.rerunForMemory`). One run that alone needs more than the whole
budget runs alone (`mem.overBudgetAlone`): it needs what one compile of the build needed.

## Summary keys

| Key | Meaning |
|---|---|
| `world.usedMacros.files` | files read |
| `world.usedMacros.incomplete` | files without `#codecarver-ok` |
| `world.usedMacros.noSource` | complete files with no line marker (`-P`, or not `-E` output) |
| `world.usedMacros.noMacros` | complete files with no `#define`/`#undef` (not `-dU`, or Clang) |
| `world.usedMacros.duplicates` | files the same as an earlier one, byte for byte (counted once) |
| `world.usedMacros.names` | distinct names used or tested |
| `world.usedMacros.commands` / `.commandsCovered` | compile commands in the logs, and how many a file matched |
| `world.usedMacros.unitsWithoutCommand` | source files in the tree the logs have no compile for (those a build trace shows unbuilt aside); only kept ones count, per stage |
| `world.usedMacros.trusted` | `1` when the header carve used the files |
| `world.ppTrace.version` | the `LLVM version` line pp-trace printed, or `unknown` |
| `world.ppTrace.commands` / `.succeeded` / `.failed` | commands pp-trace was to run, ran clean, and didn't |
| `world.ppTrace.succeeded.pastParseErrors` | of `succeeded`, runs whose only errors came from the parse after preprocessing (or a rejected option, or a warning made an error) |
| `world.ppTrace.succeeded.pastWarningsAsErrors` | of `pastParseErrors`, runs where some error was a warning made one |
| `world.ppTrace.succeeded.pastStubbedHeaders` / `world.ppTrace.headersStubbed` | runs that lacked only headers outside the tree and ran clean with an empty one in each one's place; and those headers, distinct |
| `world.ppTrace.missingPaths` / `.refused` / `.incomplete` / `.timedOut` | of the failed: a path missing here, a flag that loads other code, not all of it in the log (an unreadable response file), over the time limit |
| `world.ppTrace.missing.workDir` / `.source` / `.forcedInclude` | of `missingPaths`: what was missing |
| `world.ppTrace.failedBy.crash` / `.headerNotFound` / `.error` / `.option` / `.noMessage` / `.notStarted` | of the commands that ran and failed within the time limit, why, by the first that applies: pp-trace crashed, an include wasn't found, another error, only an option the driver rejected (which alone doesn't fail a run), no error printed, pp-trace didn't start |
| `world.ppTrace.failedBy.error.errorLimit` / `.hashError` / `.preprocessor` / `.other` | of `failedBy.error`: Clang stopped at its error limit (the preprocessor too; only when `ppTraceArgs` sets one), an `#error` fired (this Clang's predefined macros or target differ from the build compiler's), another error the preprocessor reported, another error still (no category, or one neither the parse's nor the preprocessor's) |
| `world.ppTrace.failedBy.error.preprocessor.noFlag` | of `failedBy.error.preprocessor`, runs where some preprocessor error had no warning flag (an error by itself, such as an `#if` that can't be evaluated) |
| `world.ppTrace.errorKinds` | what the preprocessor's errors said, each with the runs that printed one, most first, separated by ` \| ` (`function-like macro '' is not defined:30 \| invalid preprocessing directive:4`), or `none`: Clang's wording with what it quoted emptied and any word not all lower-case letters made `…`, and an `#error`'s or `#warning`'s text left out (`#error or #warning`), so nothing of the source |
| `world.ppTrace.errorFlags` | Clang's warning flags on the errors runs printed, each with the runs that printed one, most first (`-Winvalid-token-paste:12,...`), or `none`: Clang's own names, nothing of the source |
| `world.ppTrace.failedBy.headerNotFound.cLibrary` / `world.ppTrace.headersNotFound` | of `headerNotFound`, those whose header is the C standard library's (`stdio.h`, `string.h`, ...); and the distinct headers not found |
| `world.ppTrace.headersNotFound.inTree` / `.includedOutside` / `.withoutBuildDir` / `.withoutOutsideDir` | of `headersNotFound`: those a file of the tree ends in (the tree has it, in a directory the command doesn't search); those a file outside the carve root includes (a toolchain's header); those a command failed on that ran without an include directory inside the root where the build writes its output (a header the build makes), or without one outside the root. The console (never the summary) also lists each header not found, with the include directories outside the root that its failing commands named and this machine lacks: where to point `pathMap` or `ppTraceArgs`. It holds names and paths, so it stays on that machine |
| `world.ppTrace.optionErrors` | runs, clean or not, that printed an option the driver rejected |
| `world.ppTrace.args` | arguments `ppTraceArgs` added to each command |
| `world.ppTrace.dropped.commands` / `.options` / `.patternsUnused` | of `ppTraceDropOptions`: commands that had one, arguments taken out, patterns that matched nothing |
| `world.ppTrace.ranWithoutOutsideDirs` / `.ranWithoutDirsInRoot` | commands that ran with an include directory missing here, outside the carve root / inside it |
| `world.ppTrace.missingDirsInRoot` / `.missingDirsInRootFromBuild` | distinct include directories inside the carve root missing here, and of those, the ones at, above or under a folder some `-o` writes to |
| `world.ppTrace.notPreprocessed` | `.s` and `.asm` commands, not run |
| `world.ppTrace.names` / `.unitsWithoutCommand` / `.trusted` | as for `usedMacros` |
| `world.ppTrace.mem.budgetMb` | the memory the runs shared |
| `world.ppTrace.mem.peakOneMb` / `.peakAllMb` / `.peakRunning` | the most one run held, the most all held at once, and the most at once |
| `world.ppTrace.mem.rerunForMemory` / `.overBudgetAlone` | runs stopped and run again to stay in the budget; runs that alone needed more |
| `stageN.headerCarve.fromUsedMacros` | `1` when this stage's header carve used them (from either key) |
| `stageN.headerCarve.keptUnitsWithoutCommand` | source files this stage keeps that no compile command compiles; above `0`, the stage models the paste macros itself |
| `stageN.headerCarve.keptByUsed` / `.keptByOther` | defines kept because a compile used them, or a non-C file names them |
| `stageN.headerCarve.used.otherFiles` / `.otherNames` | non-C files read, and the names they gave |

## Without the files

The static header carve still runs when the files are missing or not trusted. It has a time limit,
`[advanced] headerCarveMinutes` (30 by default, `0` for none). Past it, the carve gives up and leaves the big headers whole, as it does past
its memory limit (`stageN.headerCarve.abandonedOverTime = 1`), and the stage goes on.
