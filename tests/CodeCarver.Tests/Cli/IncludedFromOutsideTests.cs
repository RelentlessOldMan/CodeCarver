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
