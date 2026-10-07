using CodeCarver.Core.Frontend;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Names the compiler, the linker or the loader binds that no C call spells out: <c>--defsym</c> and other link
/// flags, an ifunc resolver, <c>#pragma redefine_extname</c>, the translation unit that emits a C99 inline function,
/// a GNU extern inline's out-of-line definition, a command-line <c>-D</c> rename, and a <c>dlsym</c> by name. Each
/// carve must keep the file that really provides the symbol, and verify must fail when that file is dropped.
/// </summary>
public sealed class LinkTimeNameTests
{
    static void AssertOk(TreeCarve t, (int Code, string Out, string Err) r)
        => Assert.True(r.Code == 0, $"exit {r.Code}\n{r.Out}{r.Err}\n--- verify.txt\n{t.VerifyLog}");

    static (string, string) At(TreeCarve t, string rel) => (rel, Path.Combine(t.Src, rel));

    // ---- link flags --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Defsym_TheRightSideIsKept(bool log)
    {
        // Nothing defines omen_call: the link line says it IS omen_real. Without a build log, the tree's build script says so.
        const string link = "gcc -Wl,--defsym=omen_call=omen_real -o app main.o real.o";
        using var t = new TreeCarve()
            .W("main.c", "int omen_call(int);\nint main(void){ return omen_call(4); }\n")
            .W("real.c", "int omen_real(int x) { return x * 11; }\n");
        if (log) { t.Compile("main.c").Compile("real.c"); t.LogLines.Add(link); }
        else t.W("build.sh", "#!/bin/sh\ngcc -c main.c\ngcc -c real.c\n" + link + "\n");
        var r = t.Carve(log: log);
        AssertOk(t, r);
        Assert.True(t.Kept("real.c"), r.Out + r.Err);
    }

    [Fact]
    public void Defsym_VerifyFailsWhenTheTargetIsDropped()
    {
        using var t = new TreeCarve()
            .W("main.c", "int omen_call(int);\nint main(void){ return omen_call(4); }\n")
            .W("real.c", "int omen_real(int x) { return x * 11; }\n");
        var v = EmittedLinkCheck.Run(new[] { At(t, "main.c") }, new[] { At(t, "real.c") },
                                     linkUses: new[] { ("omen_real", "build.log", 1) });
        Assert.Contains(v.Hard, x => x.Name == "omen_real");
    }

    [Fact]
    public void LinkFlags_EveryFormOfRequiredSymbol()
    {
        static string[] Names(string text, bool script = false) => LinkFlags.RequiredSymbols(text, script).Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "omen_real" }, Names("gcc -Wl,--defsym=omen_call=omen_real -o x"));
        Assert.Equal(new[] { "base", "off" }, Names("ld --defsym omen=base+off"));
        Assert.Equal(new[] { "pull_me" }, Names("gcc -Wl,--gc-sections,-u,pull_me -o x"));
        Assert.Equal(new[] { "pull_me" }, Names("gcc -Wl,--undefined=pull_me"));
        Assert.Equal(new[] { "must_be" }, Names("ld --require-defined=must_be"));
        Assert.Equal(new[] { "start_here" }, Names("gcc -Xlinker -e -Xlinker start_here"));
        Assert.Equal(new[] { "start_here" }, Names("gcc -Wl,--entry=start_here"));
        Assert.Empty(Names("gcc -c -DX=1 -o a.o a.c"));
        var script = Names("ENTRY(reset)\nEXTERN(keep_a keep_b)\nPROVIDE(alias_fn = real_fn);\n_stack = ORIGIN(RAM) + 0x100;\n. = ALIGN(4);\n", script: true);
        Assert.Contains("reset", script);
        Assert.Contains("keep_a", script);
        Assert.Contains("keep_b", script);
        Assert.Contains("real_fn", script);
        Assert.DoesNotContain("ALIGN", script);
        Assert.DoesNotContain("ORIGIN", script);
    }

    // ---- ifunc -------------------------------------------------------------------------------------------------

    const string IfuncC = "int mul7_impl(int x);\nstatic int (*resolve_mul7(void))(int) { return mul7_impl; }\n"
                        + "int mul7(int) __attribute__((ifunc(\"resolve_mul7\")));\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ifunc_TheResolverAndItsImplementationAreKept(bool prune)
    {
        using var t = new TreeCarve()
            .W("main.c", "int mul7(int);\nint main(void){ return mul7(6); }\n")
            .W("ifunc.c", IfuncC)
            .W("impl.c", "int mul7_impl(int x) { return x * 7; }\n");
        t.Compile("main.c").Compile("ifunc.c").Compile("impl.c").TraceCompiled();
        var r = t.Carve(common: prune ? "carveSourceFileContents = true\n" : "");
        AssertOk(t, r);
        Assert.True(t.Kept("ifunc.c"), r.Out + r.Err);
        Assert.True(t.Kept("impl.c"), r.Out + r.Err);
        if (prune) Assert.Contains("resolve_mul7(void)", File.ReadAllText(t.CarvedPath("ifunc.c")));
    }

    [Fact]
    public void Ifunc_VerifyFailsWhenItsFileIsDropped()
    {
        using var t = new TreeCarve()
            .W("main.c", "int mul7(int);\nint main(void){ return mul7(6); }\n")
            .W("ifunc.c", IfuncC);
        var v = EmittedLinkCheck.Run(new[] { At(t, "main.c") }, new[] { At(t, "ifunc.c") });
        Assert.Contains(v.Hard, x => x.Name == "mul7");
    }

    // ---- #pragma redefine_extname ------------------------------------------------------------------------------

    const string RedefC = "#pragma redefine_extname old_name new_name\nint old_name(void);\nint old_name(void) { return 61; }\n";

    [Fact]
    public void RedefineExtname_TheCallBindsToTheRenamedDefinition()
    {
        using var t = new TreeCarve()
            .W("main.c", "int new_name(void);\nint main(void){ return new_name(); }\n")
            .W("redef.c", RedefC);
        t.Compile("main.c").Compile("redef.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("redef.c"), r.Out + r.Err);
        var v = EmittedLinkCheck.Run(new[] { At(t, "main.c") }, new[] { At(t, "redef.c") });
        Assert.Contains(v.Hard, x => x.Name == "new_name");
    }

    // ---- inline definitions that emit nothing ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void C99Inline_TheUnitThatEmitsItIsKept(bool prune)
    {
        // The header's body emits no symbol; emit.c's extern declaration does, and nothing in emit.c is called.
        using var t = new TreeCarve()
            .W("inl.h", "inline int twin(int x) { return x * 3; }\n")
            .W("main.c", "#include \"inl.h\"\nint main(void){ return twin(5); }\n")
            .W("emit.c", "#include \"inl.h\"\nextern inline int twin(int x);\n")
            .W("other.c", "#include \"inl.h\"\ninline int twin(int x);\nint unused(void) { return 0; }\n");
        t.Compile("main.c").Compile("emit.c").Compile("other.c").TraceCompiled(Path.Combine(t.Src, "inl.h"));
        var r = t.Carve(common: prune ? "carveSourceFileContents = true\n" : "");
        AssertOk(t, r);
        Assert.True(t.Kept("emit.c"), r.Out + r.Err);
        Assert.False(t.Kept("other.c"), r.Out + r.Err);   // an inline declaration emits nothing
    }

    [Fact]
    public void C99Inline_VerifyFailsWhenTheEmittingUnitIsDropped()
    {
        using var t = new TreeCarve()
            .W("inl.h", "inline int twin(int x) { return x * 3; }\n")
            .W("main.c", "#include \"inl.h\"\nint main(void){ return twin(5); }\n")
            .W("emit.c", "#include \"inl.h\"\nextern inline int twin(int x);\n");
        var fns = C99Inline.HeaderDefinitions(File.ReadAllText(Path.Combine(t.Src, "inl.h"))).ToHashSet();
        Assert.Equal(new[] { "twin" }, fns);
        var v = EmittedLinkCheck.Run(new[] { At(t, "main.c"), At(t, "inl.h") }, new[] { At(t, "emit.c") }, c99InlineFns: fns);
        Assert.Contains(v.Hard, x => x.Name == "twin");
        // A static inline is a definition in every includer: nothing to emit.
        Assert.Empty(C99Inline.HeaderDefinitions("static inline int s(int x) { return x; }\n"));
    }

    [Fact]
    public void GnuExternInline_TheOutOfLineDefinitionIsKept()
    {
        // The header's body is for inlining only; every call (at -O0) goes to real.c's definition.
        const string h = "extern inline __attribute__((gnu_inline)) int gt(int x) { return x + 1; }\n";
        using var t = new TreeCarve()
            .W("gi.h", h)
            .W("main.c", "#include \"gi.h\"\nint main(void){ return gt(1); }\n")
            .W("real.c", "int gt(int x) { return x + 100; }\n");
        t.Compile("main.c").Compile("real.c").TraceCompiled(Path.Combine(t.Src, "gi.h"));
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("real.c"), r.Out + r.Err);
        var v = EmittedLinkCheck.Run(new[] { At(t, "main.c"), At(t, "gi.h") }, new[] { At(t, "real.c") });
        Assert.Contains(v.Hard, x => x.Name == "gt");
    }

    // ---- command-line -D renames -------------------------------------------------------------------------------

    const string DrenFlags = "-Dsecret_rite=true_rite '-DHIDE(n)=hid_##n'";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommandLineRename_TheRenamedDefinitionIsKept(bool log)
    {
        using var t = new TreeCarve()
            .W("main.c", "int true_rite(void);\nint hid_den(void);\nint main(void){ return true_rite() + hid_den(); }\n")
            .W("dren.c", "int secret_rite(void) { return 37; }\nint HIDE(den)(void) { return 5; }\n");
        if (log) t.Compile("main.c").Compile("dren.c", DrenFlags);
        else t.W("Makefile", "dren.o: dren.c\n\t$(CC) " + DrenFlags + " -c dren.c\n");
        var r = t.Carve(log: log);
        AssertOk(t, r);
        Assert.True(t.Kept("dren.c"), r.Out + r.Err);
    }

    [Fact]
    public void CommandLineRename_VerifyFailsWhenTheFileIsDropped()
    {
        using var t = new TreeCarve()
            .W("main.c", "int true_rite(void);\nint hid_den(void);\nint main(void){ return true_rite() + hid_den(); }\n")
            .W("dren.c", "int secret_rite(void) { return 37; }\nint HIDE(den)(void) { return 5; }\n");
        var macros = CommandLineMacros.FromSpecs(CommandLineMacros.SpecsInScript("gcc " + DrenFlags + " -DLEVEL=2 -DNDEBUG -c dren.c"));
        Assert.Equal(new[] { "HIDE", "secret_rite" }, macros.Keys.OrderBy(k => k, StringComparer.Ordinal));   // numbers and flags rename nothing
        var v = EmittedLinkCheck.Run(new[] { At(t, "main.c") }, new[] { At(t, "dren.c") }, commandLineMacros: macros);
        Assert.Contains(v.Hard, x => x.Name == "true_rite");
        Assert.Contains(v.Hard, x => x.Name == "hid_den");
    }

    // ---- dlsym by name -----------------------------------------------------------------------------------------

    [Fact]
    public void DlsymByName_TheTargetIsKept()
    {
        const string dl = "#include <dlfcn.h>\nint main(void){ int (*f)(void) = (int (*)(void))dlsym(RTLD_DEFAULT, \"dl_target\"); return f ? f() : -1; }\n";
        using var t = new TreeCarve()
            .W("main.c", dl)
            .W("target.c", "int dl_target(void) { return 55; }\n")
            .W("other.c", "int not_looked_up(void) { return 1; }\n");
        t.Compile("main.c").Compile("target.c").Compile("other.c").TraceCompiled();
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("target.c"), r.Out + r.Err);
        Assert.False(t.Kept("other.c"), r.Out + r.Err);
        Assert.Contains("dl_target", EmittedLinkCheck.Scan(dl).Uses.Select(u => u.Name));
        Assert.DoesNotContain("dl_target", EmittedLinkCheck.Scan("const char *s = \"dl_target\";\n").Uses.Select(u => u.Name));
    }
}
