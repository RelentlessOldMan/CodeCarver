# hellbuild: the nightmare build

`eldritch` is nightmare *code* built by a plain script. `hellbuild` is the reverse: about 20 plain C files built
by a nightmare *build*. The build log and the build trace are what a carve reads, and this build makes both
as hard to read as real builds do.

## Run it

```
tools/eldritch/eldritch-oracle.ps1 -Example hellbuild              # needs gcc, make + strace (WSL on Windows)
tools/eldritch/eldritch-oracle.ps1 -Example hellbuild -SaveInputs  # ...and refresh inputs/
codecarver carve src --config carve.toml                           # just the carve, from the captured inputs
```

The oracle builds the original under strace, carves it with four input sets (log + trace, log, trace, none) at
four stages, then builds and runs every carved tree and requires the same output: `hell 11 22 37 5 40 5 6 8 9 33 12`.
`oracle.txt` lists what a log + trace carve must drop and what it must turn into placeholders. `HellbuildTests`
carves from `inputs/` on every push, and CI runs the whole oracle on Linux before every release.

## What's in it

`src/build.sh` runs `configure`, a few compiles make can't express, and then
`make -w -j4 -C <build dir> -f src/Makefile`, an out-of-tree, recursive, parallel make.

| Horror | What a carve sees | Files |
|---|---|---|
| Two sub-makes run at once (`make -j`), each compiling its own `util.c` with relative paths and its own `-D`. Their Entering/Leaving lines interleave, so each compile is printed while the *other* directory was entered last. | A guessed directory swaps the two files' flags: each `util.c` looks closed-world without its own `-D`, its real branch looks dead, and its helper is dropped. | `liba/`, `libb/` |
| Sources generated into the build directory: `table.c` from a sed template, `mkhooks.c` from a shell here-document | The only calls to `gen_hook_one`/`gen_hook_two` are in a file outside the tree that is gone by carve time. | `gen/` |
| A configure step writes `config.h` into the build directory and prints a probe compile of a `conftest.c` | `#ifdef HAVE_SPELL` is decided by a header the carve can't read. The probe names a file the tree never has. | `configure`, `cfg/` |
| Compiles behind a compiler-cache wrapper, a libtool-like launcher (`/bin/sh ltwrap --mode=compile ...`) and `sh -c '...'` | The compile, with its `-D`, is not the first word on the line, or is a single quoted word. | `tools/`, `lt.c`, `shc.c` |
| Quiet rules (`  CC q1.o`) and a makefile naming a source only by its stem: `$(addsuffix .o,q1 q2)` | No command at all. An unreached `q2.c` still has to exist. | `quiet/` |
| A response file the build writes and then loses; a compile through a symlink in the build directory | The `-D` and the source name are unreadable, or the path points outside the tree. | `rsp.c`, `linked/` |
| File names with a non-ASCII letter, a quote and a space: `café.c`, `it's here.c` | The log quotes them, and strace escapes them. | |
| A line-continued recipe, CMake-style `[ 42%]` progress lines | Noise. | `Makefile` |

The first five rows and `it's here.c` each broke a carve when they went in: a silent miss, a carved build that
failed, a false verify failure, or a decoy kept. The response file, the symlink, `café.c` and the noise were
already handled.
Add the next build horror the same way: make the program's output depend on it, and run the oracle.
