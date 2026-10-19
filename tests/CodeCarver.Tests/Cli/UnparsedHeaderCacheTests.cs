using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A header too big to parse is read for the #if-tested names it defines. With <c>[advanced] cacheEnabled</c> a rerun
/// over an unchanged tree takes those from the last run (<c>world.unparsedHeadersFromCache</c>) and carves the same,
/// whatever output directory it writes; a changed header is read again (its new names decide); each tree has its own
/// folder under <c>cacheDirectory</c>, shared by a run over the tree and one over a copy of it (<c>mirrorOf</c>), and
/// never answering for another tree. Without <c>cacheEnabled</c> every run reads them, and says so.
/// </summary>
public sealed class UnparsedHeaderCacheTests : IDisposable
{
    const string Small = "[advanced]\nmaxParseBytes = 1000\n";
    readonly string _caches = Path.Combine(Path.GetTempPath(), "cc-hc-" + Guid.NewGuid().ToString("N"));
    string On => Small + $"cacheEnabled = true\ncacheDirectory = \"{_caches.Replace('\\', '/')}\"\n";
    public void Dispose() { try { Directory.Delete(_caches, true); } catch { } }

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
        var r = t.Carve(extraToml: On);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersRead = 1", t.Summary);
        Assert.Contains("world.unparsedHeadersFromCache = 0", t.Summary);
        var kept = (t.Kept("fast.c"), t.Kept("slow.c"));

        r = t.Carve(extraToml: On);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 1", t.Summary);
        Assert.Equal(kept, (t.Kept("fast.c"), t.Kept("slow.c")));
        Assert.True(t.Kept("fast.c"));
    }

    [Fact]
    public void NewOutputDirectory_IsWarm_AndTheTreesFolderSaysWhichTree()
    {
        using var t = Tree(withFeature: true);
        Assert.Equal(0, t.Carve(extraToml: On).Code);
        // The last output set aside, as a run for the next version writes a new one.
        Directory.Move(Path.Combine(t.Root, "out"), Path.Combine(t.Root, "out-previous"));
        var r = t.Carve(extraToml: On);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 1", t.Summary);
        var folder = Assert.Single(Directory.GetDirectories(_caches));
        Assert.StartsWith("src-", Path.GetFileName(folder));
        Assert.Equal(Path.GetFullPath(t.Src), File.ReadAllText(Path.Combine(folder, "tree.txt")).Trim());
        Assert.Contains($"header cache for this tree: {folder}", r.Err);
    }

    [Fact]
    public void ChangedHeader_IsReadAgain_AndItsNewNamesDecide()
    {
        using var t = Tree(withFeature: true);
        Assert.Equal(0, t.Carve(extraToml: On).Code);
        Assert.True(t.Kept("fast.c"));

        // OPT_FAST gone (same length: OPT_SLOW), and a later last-write time.
        var regs = Path.Combine(t.Src, "regs.h");
        File.WriteAllText(regs, BigHeader(withFeature: false));
        File.SetLastWriteTimeUtc(regs, File.GetLastWriteTimeUtc(regs).AddMinutes(1));
        var r = t.Carve(extraToml: On);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 0", t.Summary);
        Assert.False(t.Kept("fast.c"));
        Assert.True(t.Kept("slow.c"));
    }

    [Fact]
    public void TwoTrees_OneCacheDirectory_SameHeaderPathSizeAndTime_NeverAnswerForEachOther()
    {
        using var a = Tree(withFeature: true);
        using var b = Tree(withFeature: false);
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(a.Src, "regs.h"), when);
        File.SetLastWriteTimeUtc(Path.Combine(b.Src, "regs.h"), when);   // same length too: OPT_FAST / OPT_SLOW
        Assert.Equal(0, a.Carve(extraToml: On).Code);
        Assert.True(a.Kept("fast.c"));

        var r = b.Carve(extraToml: On);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 0", b.Summary);
        Assert.False(b.Kept("fast.c"));
        Assert.True(b.Kept("slow.c"));
        Assert.Equal(2, Directory.GetDirectories(_caches).Length);
    }

    [Fact]
    public void RunOverACopy_SharesTheTreesCache()
    {
        using var tree = Tree(withFeature: true);
        using var copy = Tree(withFeature: true);
        foreach (var f in Directory.GetFiles(tree.Src))
            File.SetLastWriteTimeUtc(Path.Combine(copy.Src, Path.GetFileName(f)), File.GetLastWriteTimeUtc(f));
        Assert.Equal(0, tree.Carve(extraToml: On).Code);

        var r = copy.Carve(extraToml: On + $"mirrorOf = \"{tree.Src.Replace('\\', '/')}\"\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("world.unparsedHeadersFromCache = 1", copy.Summary);
        Assert.Single(Directory.GetDirectories(_caches));
    }

    [Fact]
    public void NoCacheEnabled_ReadsEveryTime_AndSaysSo()
    {
        using var t = Tree(withFeature: true);
        Assert.Equal(0, t.Carve(extraToml: Small).Code);
        var r = t.Carve(extraToml: Small);
        Assert.Equal(0, r.Code);
        Assert.Contains("world.unparsedHeadersFromCache = 0", t.Summary);
        Assert.Contains("no header cache", r.Err);

        // A directory alone is not enough: the cache is on only when asked for.
        r = t.Carve(extraToml: Small + $"cacheDirectory = \"{_caches.Replace('\\', '/')}\"\n");
        Assert.Equal(0, r.Code);
        Assert.Contains("cacheEnabled is not true", r.Out + r.Err);
        Assert.False(Directory.Exists(_caches));
    }

    [Fact]
    public void CacheEnabled_NeedsAUsableDirectory_OutsideTheTree()
    {
        using var t = Tree(withFeature: true);
        var r = t.Carve(extraToml: Small + "cacheEnabled = true\n");
        Assert.Equal(2, r.Code);
        Assert.Contains("cacheEnabled = true needs cacheDirectory", r.Out + r.Err);

        r = t.Carve(extraToml: Small + $"cacheEnabled = true\ncacheDirectory = \"{t.S("cache")}\"\n");
        Assert.Equal(2, r.Code);
        Assert.Contains("cacheDirectory must be OUTSIDE the source tree", r.Out + r.Err);

        var file = Path.Combine(t.Root, "not-a-dir");
        File.WriteAllText(file, "");
        r = t.Carve(extraToml: Small + $"cacheEnabled = true\ncacheDirectory = \"{file.Replace('\\', '/')}\"\n");
        Assert.Equal(2, r.Code);
        Assert.Contains("is a file, not a directory", r.Out + r.Err);

        r = t.Carve(extraToml: Small + "unparsedHeaderCache = false\n");
        Assert.Equal(2, r.Code);
        Assert.Contains("'unparsedHeaderCache' is gone", r.Out + r.Err);
    }
}
