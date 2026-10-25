# Used macros: let the build say which defines it needs

The max stage's header carve (`carveHeaderFileContents = true`) drops the `#define`s in the big headers that no kept
file needs. Without help it works this out by modelling the preprocessor: every `#if`, and every paste macro
(`CAT(KQ_, n)`) that can build a name. On register-map trees with thousands of paste macros the model can't rule much
out, so it keeps nearly everything, and it can take hours to find that out.

The compiler already knows the answer. GCC's `-E -dU` prints a `#define` for every macro a compile expanded or tested
and an `#undef` for every name it tested and found undefined. If each compile of the real build prints that once,
the carve keeps a big header's define only if some compile used it. The carve doesn't need its own model of `#if`
or `##`.

This is GCC only: Clang accepts `-dU` and prints nothing for it. It costs one more preprocess per compile.

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
2. every translation unit the carve keeps has a compile command in the logs.

A compile the carve hasn't heard from could use a define the others didn't. If either check fails, the run says why
(`NOT trusted (...)`) and the header carve models the paste macros itself, as it does without the key. Names from every
file are still read. Names count even from files that aren't complete or don't match a command, so an extra file
can keep more, never less.

## What it keeps

When it trusts the files, a big header keeps:

- each `#define` some compile used or tested (`keptByUsed`), and
- each `#define` named in a kept file that isn't C or C++ source, such as a linker script, a debugger script or a
  data file, plus the names in those defines' bodies, followed for up to 16 rounds (`keptByOther`). A debugger
  script can evaluate a macro from the debug info, so `.cmm` files count.

All non-define lines stay as they are: typedefs, enums, inline functions and `#if` lines.

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
| `world.usedMacros.unitsWithoutCommand` | kept translation units the logs have no compile for |
| `world.usedMacros.trusted` | `1` when the header carve used the files |
| `stageN.headerCarve.fromUsedMacros` | `1` when this stage's header carve used them |
| `stageN.headerCarve.keptByUsed` / `.keptByOther` | defines kept because a compile used them, or a non-C file names them |
| `stageN.headerCarve.used.otherFiles` / `.otherNames` | non-C files read, and the names they gave |

## Without the files

The static header carve still runs when the files are missing or not trusted. It has a time limit,
`[advanced] headerCarveMinutes` (30 by default, `0` for none). Past it, the carve gives up and leaves the big headers whole, as it does past
its memory limit (`stageN.headerCarve.abandonedOverTime = 1`), and the stage goes on.
