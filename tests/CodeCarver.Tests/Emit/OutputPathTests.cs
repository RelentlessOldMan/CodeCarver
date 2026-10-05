using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// Guards the source/out disjointness rule. If --out overlaps the scanned tree, the emitter would write
/// carved files on top of the user's own source — catastrophic with --prune (an in-place rewrite). These
/// pin the exact overlap semantics the CLI relies on to refuse such runs.
/// </summary>
public sealed class OutputPathTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\work\proj" : "/work/proj";
    private static string J(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    [Fact]
    public void SameDirectory_Overlaps()
        => Assert.True(OutputPath.Overlaps(J("src"), J("src")));

    [Fact]
    public void SameDirectory_TrailingSeparator_StillOverlaps()
        => Assert.True(OutputPath.Overlaps(J("src"), J("src") + Path.DirectorySeparatorChar));

    [Fact]
    public void OutNestedInsideSource_Overlaps()
        => Assert.True(OutputPath.Overlaps(J("src"), J("src", "carved")));

    [Fact]
    public void SourceNestedInsideOut_Overlaps()
        => Assert.True(OutputPath.Overlaps(J("src", "module"), J("src")));

    [Fact]
    public void DisjointSiblings_DoNotOverlap()
        => Assert.False(OutputPath.Overlaps(J("src"), J("out")));

    [Fact]
    public void PrefixSiblingIsNotNesting()
        // ".../src-carved" must NOT count as nested inside ".../src" — pure string prefix, not a path segment.
        => Assert.False(OutputPath.Overlaps(J("src"), J("src-carved")));

    [Fact]
    public void RelativeAndAbsoluteFormsOfSameDir_Overlap()
    {
        var abs = J("src");
        var rel = Path.Combine(J("src", "sub"), "..");   // resolves back to .../src
        Assert.True(OutputPath.Overlaps(abs, rel));
    }

    [Fact]
    public void CaseHandling_MatchesPlatformFilesystem()
    {
        // Review TS3: runs on every OS. Windows/macOS file systems are case-insensitive by default, so differently
        // cased forms of one directory overlap there; elsewhere they are different directories.
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, OutputPath.Overlaps(J("Proj", "SRC"), J("proj", "src")));
        Assert.Equal(expected, OutputPath.Overlaps(J("src"), J("SRC", "carved")));
    }
}
