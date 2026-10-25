using CodeCarver.Core.Frontend;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>Reading what compiles printed with <c>-E -dU</c>, and whether those files cover the build (docs/USED-MACROS.md).
/// The file texts are shaped like GCC's output; all names are invented.</summary>
public sealed class UsedMacrosTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-um-" + Guid.NewGuid().ToString("N"));
    public UsedMacrosTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static string F(string p) => p.Replace('\\', '/');
    string Src(string rel) => F(Path.Combine(_dir, "src", rel));

    string Put(string name, params string[] lines)
    {
        var p = Path.Combine(_dir, "du", name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, string.Join("\n", lines) + "\n");
        return p;
    }

    string Unit(string name, string source, params string[] macros) =>
        Put(name, new[] { $"# 0 \"{source}\"", "# 0 \"<built-in>\"", "# 0 \"<command-line>\"", $"# 1 \"{source}\"" }
                  .Concat(macros).Append(UsedMacros.OkLine).ToArray());

    CompileCommand Cmd(string rel) => new() { File = Src(rel), Directory = F(Path.Combine(_dir, "src")) };

    [Fact]
    public void Reads_DefinesAndUndefs_FromEveryFileUnderAFolder()
    {
        Unit("a.du", Src("a.c"), "#define KQ_USED 1", "#define KQ_FN(x) ((x) + 1)", "#undef KQ_TESTED_UNDEFINED");
        Unit("sub/b.du", Src("b.c"), "#define KQ_OTHER");
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        Assert.Equal(4, u.Names.Count);
        foreach (var n in new[] { "KQ_FN", "KQ_OTHER", "KQ_TESTED_UNDEFINED", "KQ_USED" }) Assert.True(u.Uses(n), n);
        Assert.Equal(2, u.Files);
        Assert.Equal(0, u.Incomplete + u.NoSource + u.NoMacros + u.Duplicates);
    }

    [Theory]
    [InlineData("#define KQ_A 1", "KQ_A")]
    [InlineData("#  define\tKQ_B(x) x", "KQ_B")]
    [InlineData("#undef KQ_C", "KQ_C")]
    [InlineData("#defined KQ_D", null)]
    [InlineData("#define 1BAD", null)]
    [InlineData("# 12 \"x.c\"", null)]
    public void MacroName_OfOneLine(string line, string? name) => Assert.Equal(name, UsedMacros.MacroName(line));

    [Theory]
    [InlineData("# 12 \"sub/x.c\" 1 3", "sub/x.c")]
    [InlineData("#line 4 \"C:\\\\tree\\\\y.c\"", "C:\\tree\\y.c")]
    [InlineData("# 0 \"<built-in>\"", "<built-in>")]
    [InlineData("#define KQ 1", null)]
    public void Marker_OfOneLine(string line, string? file) => Assert.Equal(file, UsedMacros.Marker(line));

    [Fact]
    public void AFileWithoutTheClosingLine_IsIncomplete_ButItsNamesStillCount()
    {
        Put("cut.du", $"# 0 \"{Src("a.c")}\"", "#define KQ_HALF 1");
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        Assert.Equal(1, u.Incomplete);
        Assert.True(u.Uses("KQ_HALF"));
        Assert.False(u.Check(new[] { Cmd("a.c") }, _dir, p => p, 0).Complete);
    }

    [Fact]
    public void NoMacros_NoSource_AndDuplicates_DoNotCover()
    {
        Unit("clang.du", Src("a.c"));                                   // -dU ignored: line markers only
        Put("p.du", "#define KQ_X 1", UsedMacros.OkLine);               // -P: no marker
        Unit("one.du", Src("b.c"), "#define KQ_Y 1");
        Unit("two.du", Src("b.c"), "#define KQ_Y 1");                   // the same compile written twice
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        Assert.Equal((1, 1, 1), (u.NoMacros, u.NoSource, u.Duplicates));
        var c = u.Check(new[] { Cmd("a.c"), Cmd("b.c"), Cmd("b.c") }, _dir, p => p, 0);
        Assert.Equal((3, 1), (c.Commands, c.Covered));
        Assert.False(c.Complete);
    }

    [Fact]
    public void ASourceCompiledTwice_NeedsTwoFiles()
    {
        Unit("x1.du", Src("a.c"), "#define KQ_CFG_ONE 1");
        Unit("x2.du", Src("a.c"), "#define KQ_CFG_TWO 1");
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        Assert.True(u.Check(new[] { Cmd("a.c"), Cmd("a.c") }, _dir, p => p, 0).Complete);
        Assert.False(u.Check(new[] { Cmd("a.c"), Cmd("a.c"), Cmd("a.c") }, _dir, p => p, 0).Complete);
    }

    [Fact]
    public void ARelativeSource_ResolvesAgainstTheDirectoryTheFileNames()
    {
        Put("rel.du", $"{UsedMacros.CwdPrefix}{F(Path.Combine(_dir, "src"))}", "# 0 \"sub/c.c\"", "#define KQ_Z 1", UsedMacros.OkLine);
        Put("gcc.du", "# 0 \"d.c\"", $"# 1 \"{F(Path.Combine(_dir, "src", "sub"))}//\"", "#define KQ_W 1", UsedMacros.OkLine);
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        var c = u.Check(new[] { Cmd("sub/c.c"), Cmd("sub/d.c") }, _dir, p => p, 0);
        Assert.True(c.Complete, $"{c.Covered} of {c.Commands}");
    }

    [Fact]
    public void ASourceCapturedElsewhere_IsMappedLikeTheLog()
    {
        Unit("far.du", "/build/agent/src/a.c", "#define KQ_FAR 1");
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        string Map(string p) => p.StartsWith("/build/agent/", StringComparison.Ordinal) ? F(Path.Combine(_dir, p["/build/agent/".Length..])) : p;
        Assert.True(u.Check(new[] { Cmd("a.c") }, _dir, Map, 0).Complete);
        Assert.False(u.Check(new[] { Cmd("a.c") }, _dir, p => p, 0).Complete);
    }

    [Fact]
    public void ATranslationUnitInNoCommand_IsNotCovered()
    {
        Unit("a.du", Src("a.c"), "#define KQ_A 1");
        var u = UsedMacros.Read(new[] { Path.Combine(_dir, "du") });
        Assert.False(u.Check(new[] { Cmd("a.c") }, _dir, p => p, unitsWithoutCommand: 1).Complete);
    }
}
