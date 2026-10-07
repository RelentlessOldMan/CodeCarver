# eldritch — the nightmare

`death` is the big corpus: scale. `eldritch` is the opposite: one small program that packs every nasty C
construct we know of into ~180 small files. If CodeCarver can carve this and the carved tree still builds and prints the
same thing, it can carve a lot of real code.

## Run it

```
tools/eldritch/eldritch-oracle.ps1              # needs WSL with gcc + strace
tools/eldritch/eldritch-oracle.ps1 -SaveInputs  # ...and refresh inputs/ (the captured build log + trace)
codecarver carve src --config carve.toml        # just the carve, from the captured inputs (no gcc needed)
```

The oracle:

1. builds the original with real gcc under `tools/capture` (strace), keeping the echoed compile commands
   (`build.log`) and the trace (`build.trace`), and runs it for the expected output;
2. carves it with four input sets (log + trace, log only, trace only, nothing) at four stages
   (`safe`, `headers`, `aggressive`, `max`: every combination of carving inside .c files and inside headers);
3. builds every carved tree with its own `build.sh` and runs it. Each carve must exit 0, build, and print exactly
   what the original printed. With log + trace, the decoys must be gone and the compiled-but-unreached files must
   be placeholders.

The test suite carves it too (`EldritchTests`, from `inputs/`), so CI checks it on every push.

## What's in it

| Horror | Files |
|---|---|
| Function-like macros hiding same-named **decoy** functions in a file the build never compiles (no-op, redirect, `#if`/`#else`, variadic, chained, line-continued) | `include/log.h`, `macros.c`, `legacy/decoy.c` |
| A weak default overridden by a strong definition | `weak_default.c`, `board.c` |
| Four aliases: `__attribute__((alias))`, an alias **macro** from another header, `#pragma weak a = b`, asm `.set` | `aliases.c`, `include/compiler.h` |
| `_Pragma("weak a = b")`, a weak reference nothing defines (the decoy does) | `horrors.c` |
| A C-callable function written in assembly | `asm_fast.S` |
| `#if` worlds decided by a header **outside** the carve root (`-I`), a forced include (`-include`), a response file (`@flags.rsp`), the C library (`INT_MAX`), `__has_include`, and the built-in `linux` | `cfg_users.c`, `../sdk/include/` |
| One body, two heads split across `#ifdef` | `split_heads.c` |
| K&R with implicit int and a function-pointer parameter (`-std=gnu89`) | `knr.c` |
| `int f PROTO((int a, int b))` | `proto.c` |
| Calls only through a table with an `#if` inside the initializer | `dispatch.c` |
| Token-pasted names defined in one file, called in another | `include/ops.h`, `handlers.c`, `paste.c` |
| X-macros: one `.def` list expanded into functions and a table | `xmacro.c`, `rituals.def` |
| A `.c` `#include`d by another (unity build), same-named statics in two files | `unity.c`, `unity_part.c`, `parens.c` |
| A constructor nothing calls; a symbol named only by inline asm | `ctor.c`, `asm_target.c` |
| `twice` is a macro **and** a function; `(twice)(x)` bypasses the macro | `parens.c` |
| A function body that comes from an `#include` | `bodyinc.c`, `body.inc` |
| Functions defined by a macro (`DEFINE_GETTER`, a multi-line `DEFINE_PAIR`) | `definers.c`, `horrors.c` |
| Digraphs: `<% %> <: :>` | `digraph.c` |
| `_Generic` choosing the callee | `generic.c` |
| A function referenced only inside `__attribute__((cleanup(...)))` | `cleanup.c`, `cleanup_fn.c` |
| `static inline` in a header behind `#ifdef` | `include/inline.h`, `inlhelp.c` |
| `int EXPORT(name)(void)`, comments inside a head, a line-continued head | `wrapped.c`, `weird_heads.c` |
| A file compiled twice with different `-D`, defining a different function each time | `variant.c` |
| A function reached only through a `static const struct` initializer, a struct field named like a function | `cult.c`, `horrors.c` |
| An object-like rename (`#define summon_fn real_summon`) | `include/rename.h`, `rename.c` |
| A function returning a function pointer, a compound literal holding a function, strings full of code-like junk | `horrors.c` |
| `#line`, a non-ASCII identifier, unbalanced braces inside `#if 0`, a non-inline function defined in a header | `lined.c`, `unicode.c`, `if0.c`, `include/hdr_def.h` |
| A compiled file nothing calls (a placeholder), and a file that isn't C at all | `unused_compiled.c`, `legacy/garbage.c` |
| **Wave 3** | |
| Linker `--wrap`: `__wrap_beast` is named by nobody, `__real_beast` is the original | `beast.c`, `wrap_beast.c`, `wrap.c` |
| Asm labels: `int vessel(int) __asm__("deep_one")` (a call) and `int hidden_name(void) __asm__("surface_name")` (a definition) | `asmlabel.c`, `deep.c`, `asmdef.c` |
| A template header included twice under different `#define`s: `int T_CAT(TNAME, get)(void)` defines `red_get` and `blue_get` | `include/tmpl.h`, `tmpl.c`, `tmpl_user.c` |
| Functions registered by section placement and walked with `__start_`/`__stop_` | `include/rites.h`, `rites_a.c`, `rites_b.c`, `rites.c` |
| A self-referential macro, `#undef` before a real call, `#define`/`#undef` changing the `#ifdef` world mid-file | `include/selfref.h`, `spell.c`, `undef.c`, `world*.c` |
| `#if` arithmetic: function-like macros, `-1 < 0u`, character constants, octal, `?:` | `ifexpr*.c` |
| A BOM, CRLF, form feeds and a name split by backslash-newline; trigraphs (`-trigraphs`) | `crlf.c`, `trigraph.c` |
| A C file with another extension (`-x c`), GCC nested functions, computed `#include`s, braces from macros | `tome.inc`, `nested.c`, `curse.c`, `gate.c` |
| Storage class after the type, C2x `[[attributes]]`, a function-like macro's name used as a function pointer | `soup.c`, `attrs.c`, `fnref.c` |
| **Wave 4** | |
| GNU `extern inline` in a header (its body emits nothing; `gnu_twin.c` is the symbol) | `include/gnuinl.h`, `gnuinl.c`, `gnu_twin.c` |
| C99 `inline` in a header, emitted only by a file that declares it `extern` and calls nothing | `include/c99inl.h`, `c99inl_emit.c` |
| An ifunc: the resolver is named only in an attribute string, the implementation only by the resolver | `ifunc.c`, `ifunc_impl.c` |
| `-Wl,--defsym=omen_call=omen_real` on the link line | `defsym.c`, `defsym_real.c` |
| Command-line `-Dsecret_rite=true_rite '-DHIDE(n)=hid_##n'` renaming what a file defines | `dren.c`, `dren_user.c` |
| `#include_next`; headers found through `-iquote`, `-isystem`, `-idirafter` | `include2/next.h`, `dirs.c`, `quoted/`, `sys/`, `after/` |
| A file name with a space; a compile run from another directory with relative `-I` | `spaced out.c`, `deep/abyss.c` |
| Assembly calling a C function through a macro | `asm_caller.S`, `asm_callee.c` |
| A universal character name (`spéll`) and a `$` in identifiers | `ucn.c`, `dollar.c` |
| Lexer traps: a `//` comment continued by a backslash hiding a decoy, `"/*"` in strings, `'"'`, a spliced `*\`+`/` | `comments.c`, `ghost.c` |
| `#pragma push_macro`/`pop_macro` switching a rename | `pushpop.c` |
| C++ keywords as C names in a header (`new`, `class`, `delete`, `this`, `template`, `virtual`) | `include/cppkw.h`, `cppkw.c` |
| A definition inside a macro argument; a body from a macro (`int f(void) BODY(44)`); a parameter list split by `#ifdef` | `macarg.c`, `splitp.c` |
| A function found by `dlsym` (linked `-rdynamic`): dropping it still links, the run fails | `dl.c`, `dl_target.c` |
| `#pragma redefine_extname`; a raw Latin-1 byte in a string; a NUL byte in a comment | `redef.c`, `latin1.c`, `nulbyte.c` |
| **From the Book of Horrors** | |
| `#define static` and `#define return return audit_tick(),`: keywords rebranded, a call hidden in every `return` | `include/debug_kw.h`, `keywords.c` |
| `BEGIN`/`END`/`INTEGER`, `TRY`/`CATCH` made of loops, a body opened by a macro and closed by a brace | `pascal.c` |
| `char *(*(*priest(int))[10])(double)`, Duff's device, the one-letter-macro final boss | `priest.c`, `duff.c`, `boss.c` |

Every one of these was either already handled or found a real bug when it went in. Add the next horror here
the same way: make the original print something that depends on it, and run the oracle.
