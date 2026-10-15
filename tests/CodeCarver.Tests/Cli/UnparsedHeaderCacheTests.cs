using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A header too big to parse is read for the #if-tested names it defines; a rerun over an unchanged tree takes those
/// from the last run (<c>world.unparsedHeadersFromCache</c>) and carves the same, a changed header is read again (its
/// new names decide), and <c>[advanced] unparsedHeaderCache = false</c> reads every time.
/// </summary>
public sealed class UnparsedHeaderCacheTests
{
    const string Small = "[advanced]\nmaxParseBytes = 1000\n";

    static string BigHeader(bool withFeature) =>
        "#ifndef REGS_H\n#define REGS_H\n" + (withFeature ? "#define OPT_FAST 1\n" : "#define OPT_SLOW 1\n")
        + string.Concat(Enumerable.Range(0, 200).Select(i => $"#define REG_{i} {i * 4}\n")) + "#endif\n";

    static TreeCarve Tree(bool withFeature) => new TreeCarve()
        .W("regs.h", BigHeader(withFeature))
        .W("main.c", "#include \"regs.h\"\nint fast(void);\nint slow(void);\n"
            + "#ifdef OPT_FAST\nint main(void) { return fast(); }\n#else\nint main(void) { return slow(); }\n#endif\n")
        .W("fast.c", "int fast(void) { return 1; }\n")
        .W("slow.c", "int slow(void) { return 2; }\n")
        .Compile("main.c").Compile("fast.c").Compile("slow.c");

    [Fact]
    public void Rerun_TakesTheNamesFromTheCache_AndCarvesTheSame()
    {
        using var t = Tree(withFeature: true);
        var r = t.Carve(extraToml: Small);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersRead = 1", t.Summary);
        Assert.Contains("world.unparsedHeadersFromCache = 0", t.Summary);
        var kept = (t.Kept("fast.c"), t.Kept("slow.c"));

        r = t.Carve(extraToml: Small);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 1", t.Summary);
        Assert.Equal(kept, (t.Kept("fast.c"), t.Kept("slow.c")));
        Assert.True(t.Kept("fast.c"));
    }

    [Fact]
    public void ChangedHeader_IsReadAgain_AndItsNewNamesDecide()
    {
        using var t = Tree(withFeature: true);
        Assert.Equal(0, t.Carve(extraToml: Small).Code);
        Assert.True(t.Kept("fast.c"));

        // OPT_FAST gone (same length: OPT_SLOW), and a later last-write time.
        var regs = Path.Combine(t.Src, "regs.h");
        File.WriteAllText(regs, BigHeader(withFeature: false));
        File.SetLastWriteTimeUtc(regs, File.GetLastWriteTimeUtc(regs).AddMinutes(1));
        var r = t.Carve(extraToml: Small);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 0", t.Summary);
        Assert.False(t.Kept("fast.c"));
        Assert.True(t.Kept("slow.c"));
    }

    [Fact]
    public void CacheOff_ReadsEveryTime()
    {
        using var t = Tree(withFeature: true);
        const string off = Small + "unparsedHeaderCache = false\n";
        Assert.Equal(0, t.Carve(extraToml: off).Code);
        Assert.Equal(0, t.Carve(extraToml: off).Code);
        Assert.Contains("world.unparsedHeadersFromCache = 0", t.Summary);
        Assert.False(Directory.Exists(Path.Combine(t.Root, "out", ".codecarver-cache")));
    }
}
