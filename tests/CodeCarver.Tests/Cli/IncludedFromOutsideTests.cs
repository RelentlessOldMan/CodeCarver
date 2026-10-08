using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// The carve root is one module of a bigger build. Code outside it (shared glue) #includes the module's public
/// headers, so those headers must survive every stage whole even when nothing the entry points reach includes them.
/// </summary>
public sealed class IncludedFromOutsideTests
{
    static TreeCarve Module()
    {
        var t = new TreeCarve();
        t.W("main.c", "int main(void){ return 0; }\n")
         .W("api/pub/modg_api.h", "#define MODG_LIMIT 4\nstatic inline int modg_twice(int x){ return x * 2; }\nint modg_query(void);\n")
         .W("api/pub/modg_types.h", "typedef struct { int a; } modg_rec;\n")
         .W("private/modg_internal.h", "int modg_hidden(void);\n")
         .WOut("glue/glue.c", "#include \"pub/modg_api.h\"\nint glue(void){ return modg_twice(MODG_LIMIT); }\n");
        return t;
    }

    [Fact]
    public void HeaderAnOutsideCompileIncludes_IsKeptWhole()
    {
        using var t = Module();
        t.Compile("main.c");
        t.LogLines.Add($"cd {t.Outside("glue")} && gcc -I{t.S("api")} -c {t.Outside("glue/glue.c")} -o glue.o");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
        Assert.Contains("modg_twice", File.ReadAllText(t.CarvedPath("api/pub/modg_api.h")));
        Assert.False(t.Kept("api/pub/modg_types.h"));          // nothing outside includes it
        Assert.False(t.Kept("private/modg_internal.h"));
        Assert.Contains("roots.includedFromOutside = 1", t.Summary);
    }

    [Fact]
    public void FollowsIncludesThroughOutsideHeaders()
    {
        using var t = Module();
        t.WOut("glue/glue.c", "#include \"bridge.h\"\nint glue(void){ return 0; }\n")
         .WOut("glue/bridge.h", "#include <pub/modg_types.h>\n");
        t.Compile("main.c");
        t.LogLines.Add($"cd {t.Outside("glue")} && gcc -I{t.S("api")} -c {t.Outside("glue/glue.c")} -o glue.o");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_types.h"), e);
        Assert.False(t.Kept("api/pub/modg_api.h"));
    }

    [Fact]
    public void TraceOnly_UnresolvedInclude_MatchesByPath_AndOnlyHeadersTheBuildOpened()
    {
        using var t = Module();
        t.W("other/pub/modg_api.h", "#define MODG_LIMIT 8\n");     // same tail, but the build never opened it
        t.Compile("main.c");
        t.TraceCompiled(t.Outside("glue/glue.c"), t.S("api/pub/modg_api.h"));
        var (code, o, e) = t.Carve(log: false);
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
        Assert.False(t.Kept("other/pub/modg_api.h"));
    }

    /// <summary>Outside code reaches module functions through the kept header: a function-like macro that expands
    /// to a call, and a prototype it calls directly. Nothing inside the root uses either, yet the definitions must
    /// survive every stage or the outside code no longer links.</summary>
    [Fact]
    public void FunctionsTheHeaderNames_SurviveEveryStage()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void){ return 0; }\n")
         .W("api/pub/modf_api.h", "#define MODF_READ(r) modf_read_reg(r)\n#define MODF_WRITE(r, v) modf_write_reg((r), (v))\nint modf_status(void);\n")
         .W("core/modf_core.c", "int modf_read_reg(int r){ return r; }\nvoid modf_write_reg(int r, int v){ (void)r; (void)v; }\n"
                              + "int modf_status(void){ return 1; }\nint modf_unrelated(void){ return 2; }\n")
         .WOut("glue/glue.c", "#include \"pub/modf_api.h\"\nint glue(void){ MODF_WRITE(1, 2); return MODF_READ(3) + modf_status(); }\n");
        t.Compile("main.c").Compile("core/modf_core.c");
        t.LogLines.Add($"cd {t.Outside("glue")} && gcc -I{t.S("api")} -c {t.Outside("glue/glue.c")} -o glue.o");
        var (code, o, e) = t.Carve(extraToml: "[stages.safe]\ncarveSourceFileContents = false\ncarveHeaderFileContents = false\n"
            + "[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n");
        Assert.True(code == 0, o + e);
        foreach (var stage in new[] { "safe", "max" })
        {
            var p = Path.Combine(t.Root, "out", stage, "carved", "core", "modf_core.c");
            Assert.True(File.Exists(p), stage + e);
            var text = File.ReadAllText(p);
            Assert.DoesNotContain("Placeholder written by CodeCarver", text);
            Assert.Contains("int modf_read_reg(int r)", text);
            Assert.Contains("void modf_write_reg(int r, int v)", text);
            Assert.Contains("int modf_status(void)", text);
        }
        Assert.DoesNotContain("modf_unrelated", File.ReadAllText(Path.Combine(t.Root, "out", "max", "carved", "core", "modf_core.c")));
    }

    /// <summary>The same through a header too big to parse: it has no graph nodes, only text.</summary>
    [Fact]
    public void FunctionsAnUnparsedHeaderNames_SurviveEveryStage()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void){ return 0; }\n")
         .W("api/pub/modf_tbl.h", "#define MODF_TBL_GET(i) modf_tbl_get(i)\n" + string.Concat(Enumerable.Range(0, 400).Select(i => $"#define MODF_REG_{i} 0x{i:X4}\n")))
         .W("core/modf_core.c", "int modf_tbl_get(int i){ return i; }\nint modf_unrelated(void){ return 2; }\n")
         .WOut("glue/glue.c", "#include \"pub/modf_tbl.h\"\nint glue(void){ return MODF_TBL_GET(MODF_REG_3); }\n");
        t.Compile("main.c").Compile("core/modf_core.c");
        t.LogLines.Add($"cd {t.Outside("glue")} && gcc -I{t.S("api")} -c {t.Outside("glue/glue.c")} -o glue.o");
        var (code, o, e) = t.Carve(extraToml: "[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n"
            + "[advanced]\nmaxParseBytes = 2000\n");
        Assert.True(code == 0, o + e);
        var text = File.ReadAllText(Path.Combine(t.Root, "out", "max", "carved", "core", "modf_core.c"));
        Assert.Contains("int modf_tbl_get(int i)", text);
        Assert.DoesNotContain("modf_unrelated", text);
        Assert.Contains("#define MODF_REG_399", File.ReadAllText(Path.Combine(t.Root, "out", "max", "carved", "api", "pub", "modf_tbl.h")));
    }

    static void Glue(TreeCarve t, string flags) =>
        t.LogLines.Add($"cd {t.Outside("glue")} && gcc {flags} -c {t.Outside("glue/glue.c")} -o glue.o");

    /// <summary>Windows and macOS open a header whatever case the #include or the -I dir spells it in.</summary>
    [Theory]
    [InlineData("pub/MODG_API.h", "api")]
    [InlineData("pub/modg_api.h", "API")]
    public void IncludeOrIncludeDirInAnotherCase_StillKeepsTheHeader(string include, string incDir)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;   // a case-sensitive build fails too
        using var t = Module();
        t.WOut("glue/glue.c", $"#include \"{include}\"\nint glue(void){{ return MODG_LIMIT; }}\n");
        t.Compile("main.c");
        Glue(t, $"-I{t.S("").TrimEnd('/')}/{incDir}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
        Assert.Contains("roots.includedFromOutside = 1", t.Summary);
    }

    /// <summary>Data the header declares links from outside as surely as functions do.</summary>
    [Fact]
    public void GlobalsTheHeaderDeclares_AreKept()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void){ return 0; }\n")
         .W("api/pub/modg_api.h", "extern int modg_state;\nextern const int modg_table[4];\n")
         .W("core/modg_data.c", "int modg_state;\nconst int modg_table[4] = { 1, 2, 3, 4 };\n")
         .WOut("glue/glue.c", "#include \"pub/modg_api.h\"\nint glue(void){ return modg_state + modg_table[0]; }\n");
        t.Compile("main.c").Compile("core/modg_data.c");
        Glue(t, $"-I{t.S("api")}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("core/modg_data.c"), e);
        Assert.True(t.Summary.Contains("roots.namedFromOutsideHeaders = 2"), t.Summary);
    }

    /// <summary>The header outside code includes includes another module header: what that one names is used from
    /// outside too, and it is kept whole like its includer.</summary>
    [Fact]
    public void HeadersTheIncludedHeaderIncludes_AreCoveredToo()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void){ return 0; }\n")
         .W("api/pub/modg_api.h", "#include \"modg_ext.h\"\n")
         .W("api/pub/modg_ext.h", "#define MODG_GET() modg_get()\nint modg_put(int v);\n" + string.Concat(Enumerable.Range(0, 400).Select(i => $"#define MODG_REG_{i} {i}\n")))
         .W("core/modg.c", "int modg_get(void){ return 1; }\nint modg_put(int v){ return v; }\n")
         .WOut("glue/glue.c", "#include \"pub/modg_api.h\"\nint glue(void){ return modg_put(MODG_GET()) + MODG_REG_7; }\n");
        t.Compile("main.c").Compile("core/modg.c");
        Glue(t, $"-I{t.S("api")}");
        var (code, o, e) = t.Carve(extraToml: "[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n"
            + "[advanced]\nmaxParseBytes = 2000\n");
        Assert.True(code == 0, o + e);
        var core = File.ReadAllText(Path.Combine(t.Root, "out", "max", "carved", "core", "modg.c"));
        Assert.Contains("int modg_get(void)", core);
        Assert.Contains("int modg_put(int v)", core);
        Assert.Contains("#define MODG_REG_7 ", File.ReadAllText(Path.Combine(t.Root, "out", "max", "carved", "api", "pub", "modg_ext.h")));
        Assert.Contains("roots.includedFromOutside = 2", File.ReadAllText(Path.Combine(t.Root, "out", "max", "codecarver", "summary.txt")));
    }

    /// <summary>Another compile's -I dir holds a header of the same name: the include list is the union of every
    /// command's, so the glue's own copy (in the root) must still count.</summary>
    [Fact]
    public void SameNameInAnotherCommandsIncludeDir_StillFindsTheRootHeader()
    {
        using var t = Module();
        t.WOut("sdk/pub/modg_api.h", "#define SDK_ONLY 1\n");
        t.Compile("main.c", $"-I{t.Outside("sdk")}");
        Glue(t, $"-I{t.S("api")}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
    }

    [Fact]
    public void IncludeNext_ThroughAWrapper_FindsTheRootHeader()
    {
        using var t = Module();
        t.WOut("glue/glue.c", "#include <pub/modg_api.h>\nint glue(void){ return MODG_LIMIT; }\n")
         .WOut("wrap/pub/modg_api.h", "#include_next <pub/modg_api.h>\n");
        t.Compile("main.c");
        Glue(t, $"-I{t.Outside("wrap")} -I{t.S("api")}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
    }

    [Fact]
    public void OutsideHeaderWithoutExtension_IsFollowed()
    {
        using var t = Module();
        t.WOut("glue/glue.c", "#include \"bridge\"\nint glue(void){ return MODG_LIMIT; }\n")
         .WOut("glue/bridge", "#include \"pub/modg_api.h\"\n");
        t.Compile("main.c");
        Glue(t, $"-I{t.S("api")}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
    }

    [Theory]
    [InlineData("-isystem{0}")]
    [InlineData("-iquote{0}")]
    [InlineData("-idirafter{0}")]
    [InlineData("--include-directory={0}")]
    public void JoinedIncludeDirFlags_AreFollowed(string flag)
    {
        using var t = Module();
        t.WOut("glue/glue.c", "#include \"bridge.h\"\nint glue(void){ return MODG_LIMIT; }\n")
         .WOut("brinc/bridge.h", "#include \"pub/modg_api.h\"\n")
         .W("other/pub/modg_api.h", "#define MODG_LIMIT 8\n");                  // a bare path match would keep this too
        t.Compile("main.c");
        Glue(t, string.Format(flag, t.Outside("brinc")) + $" -I{t.S("api")}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
        Assert.False(t.Kept("other/pub/modg_api.h"), e);   // resolved through the -I, not guessed by path
    }

    /// <summary><c>#include MACRO</c>: the header name is a string the file spells somewhere.</summary>
    [Fact]
    public void ComputedInclude_IsFollowed()
    {
        using var t = Module();
        t.WOut("glue/glue.c", "#define MODG_HDR \"pub/modg_api.h\"\n#include MODG_HDR\nint glue(void){ return MODG_LIMIT; }\n");
        t.Compile("main.c");
        Glue(t, $"-I{t.S("api")}");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
    }

    /// <summary>An outside compile force-includes a module header (`-include`): no #include names it, yet the outside
    /// code is compiled against it. Spelled with its path, and by name through the -I list.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForcedIncludeOfAModuleHeader_IsKeptWhole(bool fullPath)
    {
        using var t = Module();
        t.WOut("glue/glue.c", "int glue(void){ return MODG_LIMIT; }\n");
        t.Compile("main.c");
        Glue(t, fullPath ? $"-include {t.S("api/pub/modg_api.h")}" : $"-I{t.S("api/pub")} -include modg_api.h");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("api/pub/modg_api.h"), e);
        Assert.Contains("roots.includedFromOutside = 1", t.Summary);
    }

    /// <summary>A root under a system dir (/usr/src/app, a CI work dir under ProgramData) exempts only itself: the
    /// rest of that system dir is still the system, and a toolchain inside the root is still a toolchain. Before, the
    /// whole system dir stopped counting, and glibc's headers became "outside product code".</summary>
    [Fact]
    public void SystemDirHoldingTheRoot_ExemptsOnlyTheRoot()
    {
        var isSystem = CodeCarver.Cli.CarveCommand.SystemPathTest(new[] { "/usr/", "/opt/", "/usr/src/app/tools/gcc/" }, "/usr/src/app");
        Assert.False(isSystem("/usr/src/app/drv/uart.h"));
        Assert.True(isSystem("/usr/include/stdio.h"));
        Assert.True(isSystem("/usr/src/app2/x.h"));                 // a sibling, not under the root
        Assert.True(isSystem("/opt/sdk/inc/sdk.h"));
        Assert.True(isSystem("/usr/src/app/tools/gcc/include/stddef.h"));
        Assert.False(isSystem("/home/build/glue/glue.c"));
        // A root at a file-system root holds the system dirs, it isn't held by them: they stay the system.
        isSystem = CodeCarver.Cli.CarveCommand.SystemPathTest(new[] { "/usr/" }, "/");
        Assert.True(isSystem("/usr/include/stdio.h"));
        Assert.False(isSystem("/srv/glue/glue.c"));
    }

    [Fact]
    public void NoOutsideCode_ReportsZero()
    {
        using var t = Module();
        t.Compile("main.c");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.Contains("roots.includedFromOutside = 0", t.Summary);
        Assert.Contains("roots.namedFromOutsideHeaders = 0", t.Summary);
    }

    [Fact]
    public void EveryStage_KeepsTheHeaderWhole()
    {
        using var t = Module();
        t.Compile("main.c");
        t.LogLines.Add($"cd {t.Outside("glue")} && gcc -I{t.S("api")} -c {t.Outside("glue/glue.c")} -o glue.o");
        var (code, o, e) = t.Carve(extraToml: "[stages.safe]\ncarveSourceFileContents = false\ncarveHeaderFileContents = false\n"
            + "[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n");
        Assert.True(code == 0, o + e);
        foreach (var stage in new[] { "safe", "max" })
        {
            var p = Path.Combine(t.Root, "out", stage, "carved", "api", "pub", "modg_api.h");
            Assert.True(File.Exists(p), stage + e);
            var text = File.ReadAllText(p);
            Assert.Contains("static inline int modg_twice", text);
            Assert.Contains("#define MODG_LIMIT 4", text);
            Assert.Contains("int modg_query(void);", text);
        }
    }
}
