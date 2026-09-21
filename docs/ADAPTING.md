# Adapting & improving CodeCarver for your codebase

CodeCarver is meant to be pointed at codebases it has never seen — including ones whose source you
**can't share**. You don't need to hand over any real code to adapt or harden the tool for it. What
CodeCarver needs is a **profile**: an anonymized description of the *shapes and patterns* in your
codebase, plus (optionally) a few *renamed* snippets. The tool's behavior depends on code **patterns**
(how functions are declared, how dispatch works, which preprocessor tricks are used) — not on your
actual symbol names — so a pattern description is enough to configure it and to pre-harden it against
the things that trip carvers up.

This is the workflow to use whenever you want to **provide additional anonymized inputs to improve the
tool or adapt it to a new codebase**.

---

## How to provide input: fill in a Codebase Profile

Copy the template below into a `.md`, fill in what you know (leave the rest blank), and hand it to a
CodeCarver session. Everything here is generic — no proprietary details required.

```markdown
# Codebase Profile

## 1. Languages
- Language(s): [ C | C++ | C# | TRACE32 .cmm | other ]
- File extensions in play: [ e.g. .c .h .cpp .inc ]

## 2. Entry points (what to carve to)
- The functions / methods / subroutines / scripts you actually need kept.
  (Anonymized names are fine — "the public API is ~12 functions like <verb>_<noun>".)

## 3. Build configuration
- How the build selects features: [ -D flags | a config header | a build script | unknown ]
- Example feature macros (renamed ok): [ USE_FEATURE_A, TARGET=3, ... ]
- Compiler / toolchain family: [ gcc | clang | armcc/armclang | MSVC | IAR | GHS | other ]
- Is there a verbose build log or compile_commands.json available? [ yes/no ]

## 4. Layout
- Rough shape: [ single amalgamation file | few libs | deep tree | generated code | ... ]
- Directories to ignore when carving: [ tests, generated, third_party, examples, ... ]

## 5. Unusual patterns (the important part — this is what hardens the tool)
Tick anything present; add a note if you can:
- [ ] Token-paste name generation:  #define X(n) n##_handler  → names built by ##
- [ ] X-macros (a list #included repeatedly to generate code/tables)
- [ ] Dispatch tables: arrays of function pointers, hooks structs, registries
- [ ] Interrupt vector table / ISRs referenced only by address
- [ ] Indirect dispatch: function pointers, C++ virtual calls, TRACE32 `GOSUB &var`
- [ ] Macro-prefixed declarations: e.g.  LOCAL void f(...),  API int g(...),  T (name)(void)
- [ ] Functions whose return type is a bare typedef (no `*`):  MyType foo(...)
- [ ] Large data tables (lookup tables, S-boxes, string tables) worth pruning
- [ ] Inline assembly that references C symbols by name
- [ ] Heavy conditional compilation (#if / #ifdef woven through function bodies)
- [ ] Functions/symbols reached only from a macro body
- [ ] Other: ______

## 6. Anonymized reproduction snippets (optional but gold)
For any pattern above, paste a SMALL snippet with **all identifiers renamed** but the **exact syntax
preserved**. 10-20 lines that reproduce the shape is enough — the structure is what matters, not the
names. Example (renamed):

    #define REGISTER(name) name##_init, name##_step
    static const fn_t g_table[] = { REGISTER(alpha), REGISTER(beta) };
```

The single most valuable section is **#5 / #6**: the odd patterns. Every real bug found while hardening
CodeCarver came from a code *shape* (a declarator style, a macro trick, a dispatch mechanism) — never
from a specific name. A renamed 15-line snippet that reproduces a shape lets a session add it as a
permanent regression test.

---

## What a CodeCarver session does with a profile

Given a profile, a session will typically:

1. **Translate it to flags / a config file.** The profile maps directly:

   | Profile says | Becomes |
   |---|---|
   | Entry points | `--roots a,b,c` |
   | Feature macros | `--define X=1,Y` (or `--build-log` / `--probe cc`) |
   | Language | `--lang c` \| `cpp` \| `csharp` \| `cmm` |
   | Dirs to ignore | `--exclude tests,generated` |
   | Complete macro set | `--assume-defines-complete` |
   | All of it | a `carve.json` (see `docs/USAGE.md`) |

2. **Find a public analogue** — an open-source repo with the same shapes — and add it to the corpus so
   the "compile everything" harness exercises it.
3. **Add a regression** — turn each renamed snippet from #6 into a fixture the test suite keeps forever.
4. **Harden** — run the carve + compile harness, fix anything the new shape exposes, lock it in.

You get back: a `carve.json` for your codebase, plus a more robust tool — without any private code
leaving your side.

---

## What CodeCarver *never* needs from you

- Real source, real symbol names, real build logs, or real config values.
- Anything that identifies the project.

Patterns and shapes, anonymized, are enough. See [`USAGE.md`](USAGE.md) for the command reference and
[`DESIGN.txt`](../DESIGN.txt) for how the carving actually works.
