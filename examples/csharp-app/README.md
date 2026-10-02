# csharp-app — C# file-level carving

A tiny C# console app showing how CodeCarver carves **C#**. C# is its own graph — it can't merge with a
C/C++ carve — and carving is **file-level**: a `.cs` file is kept if any reached method lives in it, and
dropped otherwise. There are **no stages**: sound intra-file method pruning in C# needs real semantic
analysis (overload resolution, interfaces, reflection) that only a compiler front-end like Roslyn provides,
so CodeCarver answers *"which files does the build need for these entry points?"* and stops there — already
a big win on a large solution. C# also ignores `#if` defines here, so no `[builds]` are needed.

## Run it

```
# from the repo root, build once:  dotnet build -c Debug
./run.ps1      # Windows
./run.sh       # Linux/macOS
# or directly:
codecarver carve src --config carve.toml
```

Output lands in `out/{carved,codecarver}` (git-ignored; regenerate with `run`). The carved tree is a
complete buildable project — `dotnet run --project out/carved/tinyapp.csproj` prints `Hello, world!`.

## What each file demonstrates

| File | Shows |
|---|---|
| `src/Program.cs` | entry `Main` — the root; `new Greeter().Greet()` and `calc.Add()` are the call edges |
| `src/Greeter.cs` | reached via `new Greeter().Greet(...)` → **kept** |
| `src/Calculator.cs` | `Add()` reached, `Subtract()` not — but file-level carving **keeps the whole file** (sound; no method a call might need is dropped) |
| `src/LegacyReport.cs` | nothing reachable references it → the **whole `.cs` file is dropped** |
| `src/tinyapp.csproj` | infrastructure — copied verbatim; SDK-style `*.cs` globbing means the dropped file just vanishes from the build |

## What a run prints

```
  roots   : Main
  nodes   : 3/10 kept (30%), 7 carved
  files   : 3/4 kept, 1 dropped
  dropped : LegacyReport.cs
  verify  : (soundness check is C/C++ only; skipped for language 'csharp')
  emitted : 3 files -> out\carved  [file-level (whole kept files)]
  size    : 1,916 B -> 1,620 B  (15% smaller)
```

Verified: the carved project builds **and runs** (`dotnet run` → `Hello, world!` / `2 + 3 = 5`) with
`LegacyReport.cs` removed.
