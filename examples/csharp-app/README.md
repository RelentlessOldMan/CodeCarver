# csharp-app — C# file-level carving

A tiny C# console app showing how CodeCarver carves **C#**. C# is its own graph — it can't merge with a
C/C++ carve — and carving is always **file-level**: a `.cs` file is kept or dropped whole. The kept set is
closed over names: a kept file that mentions an identifier keeps every type and method of that name, so a
file is dropped only when nothing kept refers to anything in it. Files with top-level statements count as
entry points. Sound intra-file method pruning in C# needs real semantic analysis (overload resolution,
interfaces, reflection) that only a compiler front-end like Roslyn provides, so `carveSourceFileContents` is
ignored for C# (with a note) and there are no stages here. CodeCarver answers *"which files does the build need
for these entry points?"* and stops there — already a big win on a large solution. Code reached only through
reflection or string-based dependency injection is invisible to it: list such files in `forceKeepFiles`. C#
also ignores `#if` defines here, so no `[builds]` are needed.

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
| `src/Calculator.cs` | `Add()` reached, `Subtract()` not — C# carving is file-level, so the **whole file is kept** |
| `src/LegacyReport.cs` | nothing reachable references it → the **whole `.cs` file is dropped** |
| `src/tinyapp.csproj` | infrastructure — copied verbatim; SDK-style `*.cs` globbing means the dropped file just vanishes from the build |

## What a run prints

```
  roots   : Main
  nodes   : 10/14 kept (71%), 4 carved
  files   : 3/4 kept, 1 dropped
  dropped : LegacyReport.cs
  verify  : (emitted-tree link check is C/C++ only; skipped for language 'csharp')
  world   : open-world (both #ifdef branches kept) — no build log or compiler given
  stage   : (single)  [source-contents=whole, header-contents=whole]
  emitted : 3 files -> out/carved  [file-level (whole kept files)]
  passthru: 1 non-code file(s) copied verbatim (482 B) — complete buildable project
  size    : 1,916 B -> 1,620 B  (15% smaller, saved 296 B)
```

Verified: the carved project builds **and runs** (`dotnet run` → `Hello, world!` / `2 + 3 = 5`) with
`LegacyReport.cs` removed.
