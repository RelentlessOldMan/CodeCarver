# Reproducing the corpus validation and bug-hunt

Everything CodeCarver is hardened against is a **public repo fetched by script** and exercised by a
**committed test/harness** — nothing here depends on private code. If you clone this repo you can
replicate both halves of what we do:

1. **Validate** — the committed build-verify tests carve real repos and compile the output.
2. **Hunt** — `fuzz.ps1` carves any repo and recompiles it to find *new* pruning bugs.

The scripts are PowerShell (the dev + CI OS is Windows; the tree-sitter grammars load as native
Windows DLLs). You need: `git`, the .NET 8 SDK, and 7-Zip (for the w64devkit archive).

## 1. One-time setup

```powershell
./fetch-toolchains.ps1   # pinned w64devkit gcc/g++ (host) + arm-none-eabi-gcc (embedded) -> .toolchains/
./fetch-corpus.ps1       # ~30 varied public repos -> .corpus/   (both dirs are .gitignored)
```

Both are idempotent (re-running skips what's present). `./check.ps1 -Big -Fetch` does both, builds, and
runs the whole suite in one go.

## 2. Validate (replay the committed checks)

```powershell
./check.ps1        # fast tests only (engine, front-ends, preprocess, emit) - no toolchain needed
./check.ps1 -Big   # ALSO the build-verify tests: carve real corpus repos and COMPILE the output
```

Build-verify cases skip cleanly when a toolchain or corpus repo is absent, so `-Big` is always safe.
These are the regressions behind every fix — e.g. `Cpp_ControlFlowMacro_TryCatch...`,
`RealRepo_AsmStartupAndLinkerScript...`, the per-repo `PrunedCarve_*_AllKeptFilesCompile`.

## 3. Hunt (the bug-finding loop)

`fuzz.ps1` is the exact loop that found the fmt / simdjson / pugixml / wren / janet / capstone bugs:
baseline-compile every source **uncarved**, carve `--prune --out`, recompile the carved output. A file
that compiled before but fails after is a carve bug (dropped symbol, shattered class, orphaned brace).

```powershell
./fuzz.ps1 -Repo .corpus/<name> -Roots "sym1,sym2" [-Lang c|cpp] [-Inc dir1,dir2]
# exit code = number of carve bugs (0 = CLEAN; 2 = repo needs its own build config, not fuzzable here)
```

### Invocations that replay each finding

With the fixes in place these are **CLEAN**; they document how each bug was found (check out the parent
of the fix commit to see it break). Roots are a representative public API; `-Inc` gives the include dirs.

| Repo | Command |
|---|---|
| cJSON | `./fuzz.ps1 -Repo .corpus/cJSON -Roots "cJSON_Parse,cJSON_Delete"` |
| wren (computed-goto phantom fn) | `./fuzz.ps1 -Repo .corpus/wren -Roots "wrenNewVM,wrenInterpret,wrenFreeVM" -Inc src/vm,src/include,src/optional` |
| janet (macro-defined body / ERROR table) | `./fuzz.ps1 -Repo .corpus/janet -Roots "janet_init,janet_deinit,janet_dostring,janet_core_env" -Inc src/include,src/core,src/conf` |
| quickjs (macro-call initializer) | `./fuzz.ps1 -Repo .corpus/quickjs -Roots "JS_NewRuntime,JS_NewContext,JS_Eval,JS_FreeRuntime" -Inc .` |
| capstone (generated .inc tables) | `./fuzz.ps1 -Repo .corpus/capstone -Roots "cs_open,cs_disasm,cs_close,cs_malloc,cs_free" -Inc include,.` |
| fmt (macro-opened namespace + FMT_CATCH) | `./fuzz.ps1 -Repo .corpus/fmt -Roots "vformat,format_system_error,report_error,vprint" -Lang cpp -Inc include,src` |
| tinyxml2 (class brace / header inline) | `./fuzz.ps1 -Repo .corpus/tinyxml2 -Roots "XMLDocument,LoadFile,Parse,Accept,XMLPrinter" -Lang cpp` |
| simdjson (constructor / template prefix) | `./fuzz.ps1 -Repo .corpus/simdjson -Roots "parse,load,iterate,get_object" -Lang cpp -Inc singleheader,include` |
| pugixml (constructor init-list callee) | `./fuzz.ps1 -Repo .corpus/pugixml -Roots "load_file,load_string,save_file,child,attribute" -Lang cpp -Inc src` |

### Embedded (ARM) end-to-end

`RealRepo_AsmStartupAndLinkerScript_EmittedAndLinks_WithArmGcc` (in the build-verify suite) carves
`dwelch67/stm32_samples`, emits the `.s` startup + `.ld`, and links with `arm-none-eabi-gcc`. Run it
with `./check.ps1 -Big` once the ARM toolchain + corpus are fetched.

## Notes

- **Test harnesses over-keep.** Carving a repo *including* its own test driver (e.g. tinyxml2's
  `xmltest.cpp`) can surface a dropped symbol: the driver is pulled in by a virtual-dispatch name
  collision, then references API it doesn't reach. This is the sound over-approximation biting a file
  you'd normally `--exclude`; the *library* files carve clean. Real carves name real entry points and
  exclude tests.
- **`0 baseline`** means the repo doesn't compile standalone here (needs its own generated headers or a
  config, e.g. mbedtls/PSA). Not a carve bug — just not fuzzable without its build system.
- Every corpus repo is **pinned to a commit SHA** in `fetch-corpus.ps1` (a commit is immutable, so a
  clone is byte-identical over time). `git clone --branch` can't take a raw SHA, so the script
  shallow-*fetches* the commit (`git fetch --depth 1 origin <sha>`; GitHub allows fetch-by-SHA). The
  only way a pin can fail is the upstream repo being deleted/made private — not drift. To advance a pin,
  swap in a newer SHA (`git ls-remote <url> HEAD`).
