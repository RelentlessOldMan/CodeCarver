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
    [Fact]
    public void Overlap_ThroughAJunctionOrSymlink_IsCaught()
    {
        // Review O1: the output directory is a link to an earlier output whose carved/ is now the source.
        var tmp = Path.Combine(Path.GetTempPath(), "codecarver-o1-" + Guid.NewGuid().ToString("N"));
        var earlier = Path.Combine(tmp, "out-old");
        var source = Path.Combine(earlier, "carved");
        var link = Path.Combine(tmp, "out-link");
        Directory.CreateDirectory(source);
        try
        {
            if (!MakeDirectoryLink(link, earlier)) return;   // no way to make a link here
            Assert.True(OutputPath.Overlaps(source, link));
            Assert.False(OutputPath.Overlaps(source, Path.Combine(link, "new")));   // out-old/new: a sibling
            Assert.True(OutputPath.Overlaps(Path.Combine(link, "carved"), earlier));
            Assert.False(OutputPath.Overlaps(source, Path.Combine(tmp, "elsewhere")));
        }
        finally
        {
            try { Directory.Delete(link); } catch (IOException) { }
            Directory.Delete(tmp, true);
        }
    }

    private static bool MakeDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        p!.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }
}
