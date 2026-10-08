using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A file that holds only data (configuration objects, counters, buffers) is kept when kept code uses its data,
/// whatever shape the definition has: a dropped one leaves the carved tree with an undefined reference at link time,
/// which verify (functions only) does not see.
/// </summary>
public sealed class GlobalDataTests
{
    [Theory]
    [InlineData("int modd_counter;", "modd_counter")]
    [InlineData("int modd_level = 3;", "modd_level")]
    [InlineData("struct modd_cfg { int a; };\nconst struct modd_cfg modd_board = { 1 };", "modd_board.a")]
    [InlineData("const char *modd_name = \"x\";", "modd_name[0]")]
    [InlineData("int modd_buf[8];", "modd_buf[0]")]
    [InlineData("int (*modd_hook)(int) = 0;", "(modd_hook != 0)")]
    [InlineData("volatile unsigned int modd_a, modd_b = 2;", "modd_b")]
    [InlineData("#ifdef MODD_X\nint modd_flag = 1;\n#else\nint modd_flag = 0;\n#endif", "modd_flag")]
    public void DataOnlyFile_UsedByKeptCode_IsKept(string definition, string use)
    {
        using var t = new TreeCarve();
        var decl = definition.Contains("struct modd_cfg") ? "struct modd_cfg { int a; };\nextern const struct modd_cfg modd_board;\n" : "";
        t.W("data.c", definition + "\n")
         .W("main.c", decl + "int main(void){ return (int)(" + use + "); }\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("data.c"), e);
    }

    /// <summary>A file-scope static is that file's own: a same-named use elsewhere doesn't keep it.</summary>
    [Fact]
    public void StaticData_IsNotReachedFromAnotherFile()
    {
        using var t = new TreeCarve();
        t.W("other.c", "static int modd_state = 5;\nint other_get(void){ return modd_state; }\n")
         .W("main.c", "static int modd_state;\nint main(void){ return modd_state; }\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.False(t.Kept("other.c"), e);
    }

    /// <summary>verify, independent of the graph, fails when emitted code uses data only a dropped file defines, and
    /// stays quiet for what defines nothing (extern, typedef, prototypes, macro calls, tags) and for locals.</summary>
    [Fact]
    public void Verify_FailsOnDataOnlyADroppedFileDefines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-vdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string W(string name, string text) { var p = Path.Combine(dir, name); File.WriteAllText(p, text); return p; }
            var data = W("data.c", "struct cfg { int a; } modd_board = { 1 };\nint modd_counter, modd_buf[4];\nint (*modd_hook)(int) = 0;\n"
                                 + "static int modd_private;\nextern int modd_ext;\ntypedef int modd_t;\nint modd_proto(void);\n"
                                 + "REGISTER(modd_reg);\nstruct modd_tag;\nint modd_fn(void) { int modd_local = 0; return modd_local; }\n");
            var main = W("main.c", "extern int modd_counter, modd_buf[4], modd_private, modd_ext;\nextern int (*modd_hook)(int);\n"
                                 + "int main(void) { int modd_local = 1; return modd_counter + modd_buf[0] + (modd_hook != 0)\n"
                                 + "  + modd_private + modd_ext + modd_local + sizeof(modd_t) + modd_reg; }\n");
            var r = CodeCarver.Core.Reachability.EmittedLinkCheck.Run(new[] { ("main.c", main) }, new[] { ("data.c", data) });
            var failed = r.Hard.Select(v => v.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "modd_buf", "modd_counter", "modd_hook" }, failed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    const string Pruned = "[stages.p]\ncarveSourceFileContents = true\n";
    static string Stage(TreeCarve t, string rel) => Path.Combine(t.Root, "out", "p", "carved", rel);

    /// <summary>An unused variable whose declaration also defines its type is not cut: kept code still names the type.</summary>
    [Fact]
    public void Prune_KeepsUnusedGlobalThatDefinesItsType()
    {
        using var t = new TreeCarve();
        t.W("main.c", "struct modd_cfg { int a; } modd_dead_cfg = { 1 };\nint main(void){ struct modd_cfg c = { 2 }; return c.a; }\n");
        var (code, o, e) = t.Carve(extraToml: Pruned);
        Assert.True(code == 0, o + e);
        Assert.Contains("struct modd_cfg { int a; }", File.ReadAllText(Stage(t, "main.c")));
    }

    /// <summary>A C++ object of class type can register itself in its constructor: it stays with its file.</summary>
    [Fact]
    public void Prune_KeepsCppObjectsWithConstructors()
    {
        using var t = new TreeCarve();
        t.W("main.cpp", "int modd_count;\nstruct Registrar { Registrar(){ modd_count++; } };\nRegistrar modd_entry;\n"
                      + "int modd_register(void){ return ++modd_count; }\nint modd_slot = modd_register();\nint modd_plain = 4;\n"
                      + "int main(void){ return modd_count; }\n");
        var (code, o, e) = t.Carve(extraToml: Pruned, languages: "\"cpp\"");
        Assert.True(code == 0, o + e);
        var text = File.ReadAllText(Stage(t, "main.cpp"));
        Assert.Contains("Registrar modd_entry;", text);
        Assert.Contains("int modd_slot = modd_register();", text);
        Assert.DoesNotContain("modd_plain", text);
    }

    /// <summary>The EXTERN idiom: one unit defines the header's variables by turning EXTERN off before including it.</summary>
    [Fact]
    public void ExternMacroHeader_KeepsTheUnitThatDefinesIt()
    {
        using var t = new TreeCarve();
        t.W("modd_globals.h", "#ifdef MODD_DEFINE_GLOBALS\n#define MODD_EXTERN\n#else\n#define MODD_EXTERN extern\n#endif\nMODD_EXTERN int modd_ticks;\n")
         .W("globals.c", "#define MODD_DEFINE_GLOBALS\n#include \"modd_globals.h\"\n")
         .W("other.c", "#include \"modd_globals.h\"\nint modd_other(void){ return 1; }\n")
         .W("main.c", "#include \"modd_globals.h\"\nint main(void){ return modd_ticks; }\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("globals.c"), o + e);
        Assert.False(t.Kept("other.c"), o + e);
    }

    /// <summary>A header that defines a variable defines it in the unit including it: that unit is kept.</summary>
    [Fact]
    public void HeaderDefinition_KeepsTheIncludingUnit()
    {
        using var t = new TreeCarve();
        t.W("modd_def.h", "int modd_level = 1;\n")
         .W("x.c", "#include \"modd_def.h\"\n")
         .W("main.c", "extern int modd_level;\nint main(void){ return modd_level; }\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("x.c"), o + e);
    }

    /// <summary>An address or a cast in a file-scope initializer is a use, also for a variable that is no graph node.</summary>
    [Theory]
    [InlineData("static int *modd_p = &modd_counter;\nint main(void){ return *modd_p; }\n")]
    [InlineData("static long modd_p = (long)&modd_counter;\nint main(void){ return (int)modd_p; }\n")]
    public void AddressInStaticInitializer_KeepsTheDefiningFile(string main)
    {
        using var t = new TreeCarve();
        t.W("data.c", "int modd_counter = 3;\n")
         .W("main.c", "extern int modd_counter;\n" + main);
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("data.c"), o + e);
    }

    /// <summary>Data the parse can't read (an attribute after the name) is still defined by its file.</summary>
    [Theory]
    [InlineData("int modd_v __attribute__((aligned(4)));")]
    [InlineData("int modd_v __attribute__((section(\".modd\"))) = 1;")]
    public void AttributedData_KeepsItsFile(string definition)
    {
        using var t = new TreeCarve();
        t.W("data.c", definition + "\n")
         .W("main.c", "extern int modd_v;\nint main(void){ return modd_v; }\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("data.c"), o + e);
    }

    /// <summary>A declaration is not a definition: the header's extern doesn't stand in for the file defining it.</summary>
    [Fact]
    public void ExternDeclarationInAHeader_DoesNotHideTheDefinition()
    {
        using var t = new TreeCarve();
        t.W("modd.h", "extern int modd_total;\n")
         .W("data.c", "#include \"modd.h\"\nint modd_total;\n")
         .W("main.c", "#include \"modd.h\"\nint main(void){ return modd_total; }\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("data.c"), e);
    }
}
