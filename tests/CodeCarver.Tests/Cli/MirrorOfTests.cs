using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// <c>[advanced] mirrorOf</c>: the carve root is a local copy of a tree (often on a slow share). The run first compares
/// the two by listing (size + last-write time) and stops with exit 2 on any difference, so a copy that drifted is never
/// carved as if it were the real tree.
/// </summary>
public sealed class MirrorOfTests
{
    static TreeCarve Tree() => new TreeCarve()
        .W("main.c", "int helper(void);\nint main(void) { return helper(); }\n")
        .W("lib/helper.c", "int helper(void) { return 0; }\n")
        .Compile("main.c").Compile("lib/helper.c");

    /// <summary>A faithful copy of the source tree, as a copy tool leaves it: same bytes, same last-write times.</summary>
    static string Share(TreeCarve t)
    {
        var share = Path.Combine(t.Root, "share");
        foreach (var f in Directory.EnumerateFiles(t.Src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(share, Path.GetRelativePath(t.Src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(f));
        }
        return share;
    }

    static string Mirror(string share) => $"[advanced]\nmirrorOf = \"{share.Replace('\\', '/')}\"\n";

    [Fact]
    public void AFaithfulCopy_Carves_AndSaysWhatTheCheckCost()
    {
        using var t = Tree();
        var r = t.Carve(extraToml: Mirror(Share(t)));
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("mirror.files = 2", t.Summary);
        Assert.Contains("mirror.differences = 0", t.Summary);
        Assert.Contains("mirror.sourceListMs = ", t.Summary);
    }

    [Fact]
    public void AChangedFile_StopsTheRun_BeforeAnyWork()
    {
        using var t = Tree();
        var share = Share(t);
        File.WriteAllText(Path.Combine(share, "lib", "helper.c"), "int helper(void) { return 1 + 0; }\n");
        var r = t.Carve(extraToml: Mirror(share));
        Assert.Equal(2, r.Code);
        Assert.Contains("1 changed", r.Err);
        Assert.Contains("changed: lib/helper.c", r.Err);
        Assert.False(Directory.Exists(Path.Combine(t.Root, "out", "carved")));
    }

    [Fact]
    public void MissingAndExtraFiles_StopTheRun()
    {
        using var t = Tree();
        var share = Share(t);
        File.WriteAllText(Path.Combine(share, "lib", "new.h"), "#define NEW 1\n");
        File.Delete(Path.Combine(share, "main.c"));
        var r = t.Carve(extraToml: Mirror(share));
        Assert.Equal(2, r.Code);
        Assert.Contains("missing: lib/new.h", r.Err);
        Assert.Contains("extra: main.c", r.Err);
    }

    [Fact]
    public void AMirrorOfThatIsNotThere_IsAnError()
    {
        using var t = Tree();
        var r = t.Carve(extraToml: Mirror(Path.Combine(t.Root, "nowhere")));
        Assert.Equal(2, r.Code);
        Assert.Contains("mirrorOf", r.Err);
    }

    [Fact]
    public void SameSize_LastWriteTime_IsTheTest_WithinTwoSeconds()
    {
        using var t = Tree();
        var share = Share(t);
        var f = Path.Combine(share, "main.c");
        var at = File.GetLastWriteTimeUtc(f);
        File.SetLastWriteTimeUtc(f, at.AddSeconds(1));            // a share that keeps times to 2 s
        Assert.Equal(0, MirrorCheck.Compare(t.Src, share, Array.Empty<string>()).Differences);
        File.SetLastWriteTimeUtc(f, at.AddMinutes(5));            // edited since the copy, same size
        Assert.Equal(new[] { "main.c" }, MirrorCheck.Compare(t.Src, share, Array.Empty<string>()).Changed);
    }

    [Fact]
    public void ExcludedDirectories_AreNotCompared()
    {
        using var t = Tree();
        var share = Share(t);
        Directory.CreateDirectory(Path.Combine(share, "obj", "x"));
        File.WriteAllText(Path.Combine(share, "obj", "x", "a.o"), "o");
        Assert.Equal(1, MirrorCheck.Compare(t.Src, share, Array.Empty<string>()).Differences);
        Assert.Equal(0, MirrorCheck.Compare(t.Src, share, new[] { "/obj/" }).Differences);
    }
}
