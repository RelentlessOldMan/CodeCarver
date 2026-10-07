using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A call binds to what the compiler and linker bind it to, not to whatever function shares its name. Each case puts a
/// same-named DECOY function in a file the real build never compiles. Verify must not fail on the decoy, and the carve
/// must keep what the call really binds to.
/// </summary>
public sealed class NameBindingTests
{
    const string Decoy = "void log_it(int x){ (void)x; }\n";

    static void AssertOk(TreeCarve t, (int Code, string Out, string Err) r)
        => Assert.True(r.Code == 0, $"exit {r.Code}\n{r.Out}{r.Err}\n--- verify.txt\n{t.VerifyLog}");

    // ---- function-like macro hiding a same-named function ------------------------------------------------

    static TreeCarve MacroTree(string logH)
        => new TreeCarve()
            .W("log.h", logH)
            .W("main.c", "#include \"log.h\"\nint main(void){ log_it(1); return 0; }\n")
            .W("real.c", "void real_log(int x){ (void)x; }\n")
            .W("quiet.c", "void quiet_log(int x){ (void)x; }\n")
            .W("decoy.c", Decoy);

    public static IEnumerable<object[]> MacroShapes() => new[]
    {
        new object[] { "noop", "#define log_it(x) ((void)0)\n" },
        new object[] { "empty", "#define log_it(x)\n" },
        new object[] { "doWhile", "#define log_it(x) do { } while (0)\n" },
        new object[] { "redirect", "void real_log(int);\n#define log_it(x) real_log(x)\n" },
        new object[] { "ifElseBoth", "void real_log(int); void quiet_log(int);\n#ifdef VERBOSE\n#define log_it(x) real_log(x)\n#else\n#define log_it(x) quiet_log(x)\n#endif\n" },
        new object[] { "ifOnlyLiveBranch", "void real_log(int);\n#ifndef NO_LOG\n#define log_it(x) real_log(x)\n#endif\n" },
        new object[] { "variadic", "void real_log(int);\n#define log_it(...) real_log(__VA_ARGS__)\n" },
        new object[] { "spaced", "void real_log(int);\n  #  define   log_it( x )   real_log( x )\n" },
        new object[] { "continued", "void real_log(int);\n#define log_it(x) \\\n   real_log(x)\n" },
        new object[] { "chained", "void real_log(int);\n#define LOG_IMPL(x) real_log(x)\n#define log_it(x) LOG_IMPL(x)\n" },
    };

    [Theory]
    [MemberData(nameof(MacroShapes))]
    public void MacroHidesDecoy_LogAndTrace(string shape, string logH)
    {
        _ = shape;
        using var t = MacroTree(logH);
        t.Compile("main.c").Compile("real.c").Compile("quiet.c").TraceCompiled(t.S("log.h"));
        var r = t.Carve();
        AssertOk(t, r);
        Assert.False(t.Kept("decoy.c"));
        // The redirect target the live macro expands to is kept (VERBOSE is not defined: the #else branch is live).
        if (shape == "ifElseBoth") { Assert.True(t.Kept("quiet.c"), r.Out + r.Err); Assert.False(t.Kept("real.c"), r.Out + r.Err); }
        else if (logH.Contains("real_log(") ) Assert.True(t.Kept("real.c"), r.Out + r.Err);
    }

    [Theory]
    [MemberData(nameof(MacroShapes))]
    public void MacroHidesDecoy_TraceOnly(string shape, string logH)
    {
        // No build log: the trace can't be cross-checked, so verify itself must see the call is a macro.
        _ = shape;
        using var t = MacroTree(logH);
        t.TracePaths = new() { t.S("main.c"), t.S("real.c"), t.S("quiet.c"), t.S("log.h") };
        var r = t.Carve(log: false);
        AssertOk(t, r);
        Assert.False(t.Kept("decoy.c"));
    }

    [Theory]
    [MemberData(nameof(MacroShapes))]
    public void MacroHidesDecoy_NoBuildInputs(string shape, string logH)
    {
        // Open world, nothing skipped: the decoy is parsed. Keeping it is safe; failing is not.
        _ = shape;
        using var t = MacroTree(logH);
        AssertOk(t, t.Carve(log: false));
    }

    [Fact]
    public void MacroInAnotherTargetsHeader_DoesNotHideTheRealCall()
    {
        // other.h (never included by main.c) makes log_it a macro for ANOTHER target. main.c really calls the function.
        using var t = new TreeCarve()
            .W("other.h", "#define log_it(x) ((void)0)\n")
            .W("main.c", "void log_it(int);\nint main(void){ log_it(1); return 0; }\n")
            .W("log.c", Decoy)
            .W("other.c", "#include \"other.h\"\nvoid other(void){ log_it(2); }\n");
        t.Compile("main.c").Compile("log.c");
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("log.c"), r.Out + r.Err);
    }

    [Fact]
    public void ParenthesisedName_BypassesTheMacro_AndCallsTheFunction()
    {
        // (log_it)(1) is never a macro invocation: it calls the real function.
        using var t = new TreeCarve()
            .W("log.h", "void log_it(int);\n#define log_it(x) ((void)0)\n")
            .W("main.c", "#include \"log.h\"\nint main(void){ (log_it)(1); return 0; }\n")
            .W("log.c", "#include \"log.h\"\nvoid (log_it)(int x){ (void)x; }\n");
        t.Compile("main.c").Compile("log.c");
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("log.c"), r.Out + r.Err);
    }

    // ---- aliases and weak symbols ------------------------------------------------------------------------

    [Theory]
    [InlineData("attr", "void hook_impl(void){}\nvoid hook(void) __attribute__((weak, alias(\"hook_impl\")));\n")]
    [InlineData("attrAliasOnly", "void hook_impl(void){}\nvoid hook(void) __attribute__((alias(\"hook_impl\")));\n")]
    [InlineData("attrBefore", "void hook_impl(void){}\n__attribute__((weak, alias(\"hook_impl\"))) void hook(void);\n")]
    [InlineData("attrMacro", "#define WEAK_ALIAS(f) __attribute__((weak, alias(#f)))\nvoid hook_impl(void){}\nvoid hook(void) WEAK_ALIAS(hook_impl);\n")]
    [InlineData("pragmaWeak", "#pragma weak hook = hook_impl\nvoid hook_impl(void){}\n")]
    [InlineData("pragmaWeakNoSpaces", "#pragma weak hook=hook_impl\nvoid hook_impl(void){}\n")]
    [InlineData("asmSet", "void hook_impl(void){}\n__asm__(\".weak hook\\n.set hook, hook_impl\");\n")]
    [InlineData("asmEquals", "void hook_impl(void){}\n__asm__(\".globl hook\\n\\t.equ hook, hook_impl\");\n")]
    public void AliasDefinesTheName_AndKeepsItsTarget(string shape, string aliasC)
    {
        _ = shape;
        using var t = new TreeCarve()
            .W("main.c", "void hook(void);\nint main(void){ hook(); return 0; }\n")
            .W("alias.c", aliasC)
            .W("decoy.c", "void hook(void){}\n");
        t.Compile("main.c").Compile("alias.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("alias.c"), "alias.c dropped: the alias was not seen as hook's definition\n" + r.Out + r.Err);
        Assert.False(t.Kept("decoy.c"));
    }

    [Fact]
    public void Alias_TraceOnly_VerifyCountsItAsADefinition()
    {
        using var t = new TreeCarve()
            .W("main.c", "void hook(void);\nint main(void){ hook(); return 0; }\n")
            .W("alias.c", "void hook_impl(void){}\nvoid hook(void) __attribute__((weak, alias(\"hook_impl\")));\n")
            .W("decoy.c", "void hook(void){}\n");
        t.TracePaths = new() { t.S("main.c"), t.S("alias.c") };
        var r = t.Carve(log: false);
        AssertOk(t, r);
        Assert.True(t.Kept("alias.c"), r.Out + r.Err);
    }

    [Fact]
    public void WeakDefault_AndStrongOverride_BothKept()
    {
        using var t = new TreeCarve()
            .W("main.c", "void hook(void);\nint main(void){ hook(); return 0; }\n")
            .W("lib.c", "__attribute__((weak)) void hook(void){}\n")
            .W("board.c", "void board_init(void){}\nvoid hook(void){ board_init(); }\n");
        t.Compile("main.c").Compile("lib.c").Compile("board.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("lib.c"), r.Out + r.Err);
        Assert.True(t.Kept("board.c"), r.Out + r.Err);
    }

    [Theory]
    [InlineData("fast.S", ".globl fast_copy\nfast_copy:\n  ret\n")]
    [InlineData("fast.s", "  .global fast_copy\n  .type fast_copy, %function\nfast_copy:\n  bx lr\n")]
    [InlineData("fast.asm", "PUBLIC fast_copy\nfast_copy PROC\n  ret\nfast_copy ENDP\n")]
    public void FunctionDefinedInAssembly_IsKept_AndNotADecoyFailure(string file, string asm)
    {
        using var t = new TreeCarve()
            .W("main.c", "void fast_copy(void);\nint main(void){ fast_copy(); return 0; }\n")
            .W(file, asm)
            .W("decoy.c", "void fast_copy(void){}\n");
        t.Compile("main.c").Compile(file).TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(File.Exists(t.CarvedPath(file)), r.Out + r.Err);
        Assert.False(t.Kept("decoy.c"));
    }

    [Theory]
    [InlineData("fast.S", ".globl fast_copy\nfast_copy:\n  ret\n")]
    [InlineData("fast.s", "  .global fast_copy\nfast_copy:\n  bx lr\n")]
    public void FunctionDefinedInAssembly_TraceOnly_VerifySeesIt(string file, string asm)
    {
        using var t = new TreeCarve()
            .W("main.c", "void fast_copy(void);\nint main(void){ fast_copy(); return 0; }\n")
            .W(file, asm)
            .W("decoy.c", "void fast_copy(void){}\n");
        t.TracePaths = new() { t.S("main.c"), t.S(file) };
        var r = t.Carve(log: false);
        AssertOk(t, r);
        Assert.True(File.Exists(t.CarvedPath(file)), r.Out + r.Err);
    }

    // ---- definition shapes verify and the parser must both see ---------------------------------------------

    public static IEnumerable<object[]> OddDefinitions() => new[]
    {
        new object[] { "digraphs", "int odd_fn(int a) <% int v<:1:> = <% a %>; return v<:0:>; %>\n" },
        new object[] { "digraphsMixed", "int odd_fn(int a) <% return a; }\n" },
        new object[] { "wrappedName", "#define EXPORT(name) name\nint EXPORT(odd_fn)(int a) { return a; }\n" },
        new object[] { "wrappedNameParens", "#define API(n) (n)\nint API(odd_fn)(int a) { return a; }\n" },
        new object[] { "parenthesisedName", "int (odd_fn)(int a) { return a; }\n" },
        new object[] { "parenthesisedAndMacro", "#define odd_fn(a) ((a) + 1)\nint (odd_fn)(int a) { return a; }\n" },
        // Trigraphs (gcc -trigraphs / strict -std): ??< is {, ??> is }, ??( is [, ??= is #.
        new object[] { "trigraphs", "??=define BASE 7\nint odd_fn(int a) ??< int v??(1??) = ??< a ??>; return v??(0??) ??' BASE; ??>\n" },
        // Line splicing: a backslash-newline may split a name anywhere, with LF or CRLF.
        new object[] { "splitNameLf", "int odd_\\\nfn(int a) { return a; }\n" },
        new object[] { "splitNameCrlf", "int odd_\\\r\nfn(int a)\r\n{\r\n    return a;\r\n}\r\n" },
        new object[] { "crlfBomFormFeed", "﻿/* x */\r\n\f\r\nint odd_fn(int a)\r\n{\f\r\n    return a;\r\n}\r\n" },
        // An asm label: the C name is hidden, the symbol is odd_fn.
        new object[] { "asmLabel", "int hidden(int) __asm__(\"odd_fn\");\nint hidden(int a) { return a; }\n" },
        new object[] { "asmLabelPlain", "int hidden(int) asm (\"odd_fn\");\nint hidden(int a) { return a; }\n" },
        // Head and body braces from macros.
        new object[] { "braceMacros", "#define BEGIN_FN(name) int name(int a) {\n#define END_FN }\nBEGIN_FN(odd_fn)\n    return a;\nEND_FN\n" },
        // Storage class after the type.
        new object[] { "specifierSoup", "long unsigned static long int helper(void) { return 1; }\nint static odd_fn(int a);\nint odd_fn(int a) { return a + (int)helper(); }\n" },
        new object[] { "c2xAttributes", "[[gnu::noinline]] [[maybe_unused]] int odd_fn(int a) { return a; }\n" },
    };

    [Theory]
    [MemberData(nameof(OddDefinitions))]
    public void OddDefinitionShape_IsKept(string shape, string oddC)
    {
        _ = shape;
        using var t = new TreeCarve()
            .W("main.c", "int odd_fn(int);\nint main(void){ return (odd_fn)(1); }\n")
            .W("odd.c", oddC);
        t.Compile("main.c").Compile("odd.c");
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("odd.c"), "odd.c dropped: its definition wasn't recognised\n" + r.Out + r.Err);
    }

    [Theory]
    [MemberData(nameof(OddDefinitions))]
    public void OddDefinitionShape_VerifySeesIt(string shape, string oddC)
    {
        // The independent check must recognise the definition too, or a carve that drops it passes silently.
        _ = shape;
        var defs = CodeCarver.Core.Reachability.EmittedLinkCheck.Scan(oddC).Definitions.Select(d => d.Name).ToList();
        Assert.Contains("odd_fn", defs);
    }

    [Theory]
    [InlineData("#if 0\nint fake(void) { { {\n#endif\nint odd_fn(int a) { return a; }\n#if 0\n}}}\n#endif\n")]
    [InlineData("#if 0\n  \" unterminated ' junk ( {\n#else\nint odd_fn(int a) { return a; }\n#endif\n")]
    [InlineData("#if (0)\n{\n#elif 1\nint odd_fn(int a) { return a; }\n#endif\n")]
    public void DefinitionAfterIfZeroJunk_IsKept_AndVerifySeesIt(string oddC)
    {
        Assert.Contains("odd_fn", CodeCarver.Core.Reachability.EmittedLinkCheck.Scan(oddC).Definitions.Select(d => d.Name));
        using var t = new TreeCarve()
            .W("main.c", "int odd_fn(int);\nint main(void){ return odd_fn(1); }\n")
            .W("odd.c", oddC);
        t.Compile("main.c").Compile("odd.c");
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("odd.c"), r.Out + r.Err);
    }

    [Theory]
    [InlineData("_Pragma(\"weak hook = hook_impl\")\n")]
    [InlineData("_Pragma (\"weak hook=hook_impl\")\n")]
    public void PragmaOperatorWeakAlias_DefinesAndKeepsTarget(string pragma)
    {
        using var t = new TreeCarve()
            .W("main.c", "int hook(void);\nint main(void){ return hook(); }\n")
            .W("alias.c", "static int other(void) { return 1; }\nint hook_impl(void) { return other(); }\n" + pragma + "int hook(void);\n")
            .W("decoy.c", "int hook(void){ return 0; }\n");
        t.TracePaths = new() { t.S("main.c"), t.S("alias.c") };
        var r = t.Carve(log: false, extraToml: "[advanced]\n", common: "");
        AssertOk(t, r);
        Assert.True(t.Kept("alias.c"), r.Out + r.Err);
    }

    // ---- the linker renames: --wrap, asm labels ----------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkerWrap_WrapperIsKeptWhenTheWrappedNameIs(bool prune)
    {
        // Linked with -Wl,--wrap=beast: every beast() call goes to __wrap_beast, which nothing names. __real_beast
        // is the original beast.
        using var t = new TreeCarve()
            .W("main.c", "int beast(int);\nint main(void){ return beast(3); }\n")
            .W("beast.c", "int beast(int x) { return x; }\n")
            .W("wrap.c", "int __real_beast(int);\nstatic int extra(void) { return 1; }\nint __wrap_beast(int x) { return __real_beast(x) + extra(); }\n");
        t.Compile("main.c").Compile("beast.c").Compile("wrap.c").TraceCompiled();
        var r = t.Carve(common: prune ? "carveSourceFileContents = true\n" : "");
        AssertOk(t, r);
        Assert.True(t.Kept("wrap.c"), r.Out + r.Err);
        Assert.True(t.Kept("beast.c"), r.Out + r.Err);
        if (prune) Assert.Contains("__wrap_beast", File.ReadAllText(t.CarvedPath("wrap.c")));
        // Verify sees the wrapper as a use of the original and the original as the wrapper's target.
        var uses = CodeCarver.Core.Reachability.EmittedLinkCheck.Scan("int __real_beast(int);\nint __wrap_beast(int x) { return __real_beast(x); }\n").Uses.Select(u => u.Name);
        Assert.Contains("beast", uses);
    }

    [Fact]
    public void AsmLabelDeclaration_CallBindsToTheLabel()
    {
        using var t = new TreeCarve()
            .W("main.c", "extern int vessel(int) __asm__(\"deep_one\");\nint main(void){ return vessel(4); }\n")
            .W("deep.c", "int deep_one(int x) { return x; }\n")
            .W("decoy.c", "int vessel(int x) { return 0; }\n");
        t.Compile("main.c").Compile("deep.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("deep.c"), r.Out + r.Err);
        Assert.False(t.Kept("decoy.c"), r.Out + r.Err);
        Assert.Contains("deep_one", CodeCarver.Core.Reachability.EmittedLinkCheck.Scan("extern int vessel(int) __asm__(\"deep_one\");\nint f(void){ return vessel(4); }\n").Uses.Select(u => u.Name));
    }

    // ---- a header instantiated more than once ----------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemplateHeader_EveryInstantiationIsDefinedByItsIncluder(bool prune)
    {
        const string tmplH = "#define T_CAT2(a, b) a##_##b\n#define T_CAT(a, b) T_CAT2(a, b)\n"
                           + "static int T_CAT(TNAME, helper)(void) { return TVAL; }\n"
                           + "int T_CAT(TNAME, get)(void) { return T_CAT(TNAME, helper)(); }\n#undef T_CAT\n#undef T_CAT2\n";
        using var t = new TreeCarve()
            .W("tmpl.h", tmplH)
            .W("tmpl.c", "#define TNAME red\n#define TVAL 5\n#include \"tmpl.h\"\n#undef TNAME\n#undef TVAL\n"
                       + "#define TNAME blue\n#define TVAL 6\n#include \"tmpl.h\"\n")
            .W("main.c", "int red_get(void); int blue_get(void);\nint main(void){ return red_get() + blue_get(); }\n");
        t.Compile("main.c").Compile("tmpl.c").TraceCompiled(t.S("tmpl.h"));
        var r = t.Carve(common: prune ? "carveSourceFileContents = true\n" : "");
        AssertOk(t, r);
        Assert.True(t.Kept("tmpl.c"), r.Out + r.Err);
        Assert.True(t.Kept("tmpl.h"), r.Out + r.Err);
    }

    [Fact]
    public void TemplateHeader_VerifyFailsWhenTheIncluderIsDropped()
    {
        // The emitted tree has the caller but not the includer: verify must know tmpl.c defined red_get.
        using var t = new TreeCarve()
            .W("tmpl.h", "#define T_CAT2(a, b) a##_##b\n#define T_CAT(a, b) T_CAT2(a, b)\nint T_CAT(TNAME, get)(void) { return 1; }\n")
            .W("tmpl.c", "#define TNAME red\n#include \"tmpl.h\"\n")
            .W("main.c", "int red_get(void);\nint main(void){ return red_get(); }\n");
        var v = CodeCarver.Core.Reachability.EmittedLinkCheck.Run(
            new[] { ("main.c", Path.Combine(t.Src, "main.c")), ("tmpl.h", Path.Combine(t.Src, "tmpl.h")) },
            new[] { ("tmpl.c", Path.Combine(t.Src, "tmpl.c")) });
        Assert.Contains(v.Hard, x => x.Name == "red_get");
    }

    // ---- linker sections walked by __start_/__stop_ ----------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SectionEntries_WalkedByStartStop_AreKept(bool prune)
    {
        using var t = new TreeCarve()
            .W("rites.h", "typedef int (*rite_fn)(void);\n#define R_CAT2(a, b) a##b\n#define R_CAT(a, b) R_CAT2(a, b)\n"
                        + "#define RITE(v) static int R_CAT(rite_, __LINE__)(void) { return v; } \\\n"
                        + "    const rite_fn R_CAT(rite_ptr_, __LINE__) __attribute__((section(\"my_rites\"))) = R_CAT(rite_, __LINE__);\n")
            .W("a.c", "#include \"rites.h\"\nRITE(1)\n")
            .W("b.c", "#include \"rites.h\"\nstatic int named(void) { return 10; }\n"
                    + "const rite_fn named_ptr __attribute__((section(\"my_rites\"))) = named;\n")
            .W("main.c", "#include \"rites.h\"\nextern const rite_fn __start_my_rites[], __stop_my_rites[];\n"
                       + "int main(void){ int s = 0; for (const rite_fn *p = __start_my_rites; p < __stop_my_rites; p++) s += (*p)(); return s; }\n");
        t.Compile("main.c").Compile("a.c").Compile("b.c").TraceCompiled(t.S("rites.h"));
        var r = t.Carve(common: prune ? "carveSourceFileContents = true\n" : "");
        AssertOk(t, r);
        Assert.True(t.Kept("a.c"), r.Out + r.Err);
        Assert.True(t.Kept("b.c"), r.Out + r.Err);
        if (prune) Assert.Contains("static int named(void)", File.ReadAllText(t.CarvedPath("b.c")));
    }

    [Fact]
    public void AliasTarget_IsAUse_SoPruningItFailsVerify()
    {
        // The alias is emitted; its target only appears inside a string. Dropping the target must be caught.
        var aliasC = "int hook(void) __attribute__((alias(\"hook_impl\")));\n";
        var uses = CodeCarver.Core.Reachability.EmittedLinkCheck.Scan(aliasC).Uses.Select(u => u.Name);
        Assert.Contains("hook_impl", uses);
        var pragma = CodeCarver.Core.Reachability.EmittedLinkCheck.Scan("#pragma weak hook = hook_impl\n").Uses.Select(u => u.Name);
        Assert.Contains("hook_impl", pragma);
    }

    [Theory]
    [InlineData("extern int maybe_fn(void) __attribute__((weak));\n")]
    [InlineData("__attribute__((weak)) extern int maybe_fn(void);\n")]
    [InlineData("#pragma weak maybe_fn\nextern int maybe_fn(void);\n")]
    [InlineData("_Pragma(\"weak maybe_fn\")\nextern int maybe_fn(void);\n")]
    public void WeakUndefinedReference_NeedsNoDefinition(string decl)
    {
        using var t = new TreeCarve()
            .W("main.c", decl + "int main(void){ return maybe_fn ? maybe_fn() : 0; }\n")
            .W("decoy.c", "int maybe_fn(void){ return 1; }\n");
        t.TracePaths = new() { t.S("main.c") };
        var r = t.Carve(log: false);
        AssertOk(t, r);
    }

    [Fact]
    public void WeakReference_WithACompiledDefinition_KeepsIt()
    {
        // The weak reference resolves to board.c's definition in the real build: dropping it would silently turn
        // the call into a null check that's false. It must be kept.
        using var t = new TreeCarve()
            .W("main.c", "extern int board_hook(void) __attribute__((weak));\nint main(void){ return board_hook ? board_hook() : 0; }\n")
            .W("board.c", "int board_hook(void){ return 1; }\n");
        t.Compile("main.c").Compile("board.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("board.c"), r.Out + r.Err);
    }

    // ---- trace completeness -------------------------------------------------------------------------------

    [Fact]
    public void DefinitionInAFileTheBuildNeverCompiled_IsANote_WhenTheTraceIsComplete()
    {
        // ext_fn comes from a build we weren't given (another component, a prebuilt library). Its only visible
        // definition is in a file this build never compiled, so dropping that file can't break this build.
        using var t = new TreeCarve()
            .W("main.c", "void ext_fn(void);\nint main(void){ ext_fn(); return 0; }\n")
            .W("elsewhere.c", "void ext_fn(void){}\n");
        t.Compile("main.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.Contains("never compiled", r.Out + r.Err);
        Assert.Contains("verify.notBuiltDefinitions = 1", t.Summary);
    }

    [Fact]
    public void TraceMissingACompiledFile_IsIncomplete_SaysSo_AndReadsEverything()
    {
        // helper.c was compiled (the log says so) but the trace never saw it opened: the trace can't be trusted to
        // say what to skip, so nothing is skipped and the carve is still right.
        using var t = new TreeCarve()
            .W("main.c", "int helper(void);\nint main(void){ return helper(); }\n")
            .W("helper.c", "int helper(void){ return 1; }\n")
            .W("unused.c", "int unused(void){ return 2; }\n");
        t.Compile("main.c").Compile("helper.c");
        t.TracePaths = new() { t.S("main.c") };
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("helper.c"), r.Out + r.Err);
        Assert.Contains("build.traceMissedCompiledFiles = 1", t.Summary);
        Assert.Contains("incomplete", r.Out + r.Err);
        Assert.DoesNotContain("parse.filesNotBuiltSkipped = 1", t.Summary);
    }

    [Fact]
    public void TraceOnly_MissingADefinition_FailsAndSaysToAddTheLog()
    {
        using var t = new TreeCarve()
            .W("main.c", "int helper(void);\nint main(void){ return helper(); }\n")
            .W("helper.c", "int helper(void){ return 1; }\n");
        t.TracePaths = new() { t.S("main.c") };
        var r = t.Carve(log: false);
        Assert.True(r.Code == 3, r.Out + r.Err);
        Assert.Contains("add the build log", r.Out + r.Err);
    }

    // ---- entry points ---------------------------------------------------------------------------------------

    [Fact]
    public void EntryPointOnlyInAFileTheBuildNeverCompiled_SaysSo()
    {
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("other.c", "void other_entry(void){}\n");
        t.Compile("main.c").TraceCompiled();
        var r = t.Carve(entry: "other_entry");
        Assert.Equal(1, r.Code);
        Assert.Contains("never compiled", r.Out + r.Err);
    }

    [Fact]
    public void EntryPointDefinedNowhere_SaysSo()
    {
        using var t = new TreeCarve().W("main.c", "int main(void){ return 0; }\n");
        t.Compile("main.c").TraceCompiled();
        var r = t.Carve(entry: "no_such_fn");
        Assert.Equal(1, r.Code);
        Assert.Contains("not defined anywhere", r.Out + r.Err);
    }

    [Fact]
    public void EntryPointInDeadIfdefBranch_SaysSo()
    {
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n#ifdef OFF\nvoid gated(void){}\n#endif\n");
        t.Compile("main.c");
        var r = t.Carve(entry: "gated");
        Assert.Equal(1, r.Code);
        Assert.Contains("#if", r.Out + r.Err);
    }
}
