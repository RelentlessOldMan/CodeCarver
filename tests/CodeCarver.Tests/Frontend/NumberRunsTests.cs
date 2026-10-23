using CodeCarver.Core.Graph;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>A 20 MB data table of 1.5M number literals took 95 s to process, nearly all in the passes walking its
/// nodes. Long runs of number-only lines are blanked for parsing; nothing they hold names anything.</summary>
public class NumberRunsTests
{
    private static string Table(int lines, string row = "\t0x4EA9F936u, 0xC51DA43Eu, -12, 1.5e-3f, 07, 0b101,\n") =>
        string.Concat(Enumerable.Repeat(row, lines));

    private static string Pad(string s) => s + new string('\n', Math.Max(0, 70_000 - s.Length));

    [Fact]
    public void Blank_LongNumberRun_KeepsLinesAndOffsets()
    {
        var text = Pad("unsigned t[] = {\n" + Table(NumberRuns.MinRun) + "\t0u\n};\nint use(void) { return helper(t[0]); }\n");
        var parse = NumberRuns.Blank(text);
        Assert.Equal(text.Length, parse.Length);
        Assert.Equal(text.Count(c => c == '\n'), parse.Count(c => c == '\n'));
        Assert.DoesNotContain("0x4EA9F936u", parse);
        Assert.Contains("unsigned t[] = {", parse);
        Assert.Contains("return helper(t[0]);", parse);
    }

    [Theory]
    [InlineData(NumberRuns.MinRun - 1, "\t1, 2, 3,\n")]            // a short run: ordinary code is left alone
    [InlineData(NumberRuns.MinRun, "\t1, 2, fn_a,\n")]             // a name in every line
    [InlineData(NumberRuns.MinRun, "\t1, 2, 3 \\\n")]              // a macro body's continued lines
    [InlineData(NumberRuns.MinRun, "\t{ 1, 2 },\n")]               // braces: not a number-only line
    public void Blank_LeavesAnythingElse(int lines, string row)
    {
        var text = Pad("int t[] = {\n" + Table(lines, row) + "0 };\n");
        Assert.Same(text, NumberRuns.Blank(text));
    }

    [Fact]
    public void Blank_SmallFile_Untouched()
    {
        var text = "int t[] = {\n" + Table(NumberRuns.MinRun) + "0 };\n";
        Assert.True(text.Length < 64 * 1024);
        Assert.Same(text, NumberRuns.Blank(text));
    }

    [Fact]
    public void BigTable_NamesAroundItStillCount()
    {
        // The names around and inside the table's declaration still count: the file still uses helper and the
        // function after the table is still defined.
        var src = "int helper(int x) { return x; }\nunsigned tbl[] = {\n" + Table(20_000) + "\t0u\n};\nint after(void) { return helper((int)tbl[0]); }\n";
        using var fe = new CFrontEnd();
        var g = fe.BuildGraph(new[] { ("table.c", src) });
        var after = Assert.Single(g.Nodes, n => n.Kind == NodeKind.Function && n.Name == "after");
        Assert.Contains(g.Nodes, n => n.Name == "tbl");
        var uses = g.OutEdges(after.Id).Select(e => g.GetNode(e.To).Name).ToList();
        Assert.Contains("tbl", uses);
        Assert.Contains("helper", uses);
    }
}
