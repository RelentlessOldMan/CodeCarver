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
