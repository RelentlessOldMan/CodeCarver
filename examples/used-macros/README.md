# Used macros

A small firmware-shaped tree whose register header is built from paste macros, carved from what its real GCC build
used ([`docs/USED-MACROS.md`](../../docs/USED-MACROS.md)).

```
src/inc/kq_regs.h      20 KB: 8 blocks x 6 registers x 5 fields, each with _SHFT and _BMSK, read through KQ_CAT/KQ_FIELD
src/inc/kq_config.h    KQ_HAS_DMA 0, KQ_UART_MODE 2
src/main.c, uart.c     uart.c pastes the UART control fields
src/dma.c              its register code is under #if KQ_HAS_DMA: off in this configuration
src/memmap.txt.in      a text file run through gcc -E; names two block bases
collect/du-wrap.sh     the collector: runs the real gcc, then the same command as -E -dU into one file
collect/bin/gcc        puts du-wrap.sh in front of gcc on PATH
run.sh                 build with the collector, carve, build and run every carved stage
inputs/                the capture checked in: build.log and du/*.du
```

## Run it

```sh
sh run.sh path/to/codecarver.dll        # needs gcc, make and dotnet (or codecarver.exe from WSL)
```

```
max: kq_regs.h 19726 -> 809 bytes, 13 defines kept, 529 dropped
safe: ok
max: ok
```

The max stage keeps the 13 defines the compiles used: the paste macros, the UART fields `uart.c` builds, the two
block bases `memmap.txt.in` names, and the include guard. The header is 96% smaller. Both carved trees build with the
same `make` and print the same output.

Without `usedMacros`, the static carve on the same tree keeps 19. It must also keep the DMA fields behind
`#if KQ_HAS_DMA`, because it can't be sure which way that `#if` goes for every paste. On real register maps the gap
between the two is far wider.

Without a build machine, `codecarver carve src --config carve.toml` carves from the capture in `inputs/`. The paths
in the capture are under `/build/used-macros`, which `pathMap` maps onto this checkout.

## The same tree with pp-trace

This sample is built with GCC, but its commands are plain enough for Clang too. With LLVM installed, replace
`usedMacros` in `carve.toml` with `ppTrace = "C:/Program Files/LLVM/bin/pp-trace.exe"` (or the path on your
machine) and carve again: pp-trace runs the log's 3 compile commands and the max stage keeps the same 13 defines.
For a Clang build that is the whole job: nothing to collect, no wrapper. See
[USED-MACROS.md](../../docs/USED-MACROS.md#clang-builds-pp-trace).

## Adapting it to a real build

The wrapper is an example of what to collect, not a drop-in. Read its `ADAPT:` comments against your build:

- **Driver names.** Every compiler, cross compiler and `cpp` the build calls needs a stand-in in `collect/bin`
  (`arm-none-eabi-gcc`, `cc`, `cpp`...), each the same one-line shim.
- **Finding the real compiler.** The wrapper takes the next one on `PATH`. A build that calls compilers by absolute
  path, or through `ccache` or a launcher, needs the wrapper hooked in there instead, or `CODECARVER_REAL_CC`.
- **One source per command.** A command that compiles several sources needs one `-E -dU` run per source.
- **Options that write files.** Dependency files (`-MD`, `-MF`), `-save-temps` and vendor listing options must be
  removed from the second run, or it overwrites the build's own outputs.
- **Every target.** Collect every target and configuration that will use the carved tree, with the build log of
  the same build. The run says `NOT trusted` and names the reason if a compile command has no file.

After a change, check that `world.usedMacros.trusted = 1`, and that `commandsCovered` equals `commands`, in the
max stage's `codecarver/summary.txt`.
