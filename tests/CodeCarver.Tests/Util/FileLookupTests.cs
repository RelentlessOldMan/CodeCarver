using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

public sealed class FileLookupTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "cc-lookup-" + Guid.NewGuid().ToString("N"));

    public FileLookupTests()
    {
        Directory.CreateDirectory(Path.Combine(work, "a", "sub"));
        Directory.CreateDirectory(Path.Combine(work, "b"));
        File.WriteAllText(Path.Combine(work, "a", "top.h"), "");
        File.WriteAllText(Path.Combine(work, "a", "sub", "deep.h"), "");
        File.WriteAllText(Path.Combine(work, "b", "other.h"), "");
    }

    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    [Fact]
    public void Probe_BareName_SubPath_Parent_AndMisses()
    {
        var f = new FileLookup();
        var a = Path.Combine(work, "a");
        Assert.Equal(Path.Combine(a, "top.h"), f.Probe(a, "top.h"));
        Assert.Equal(Path.Combine(a, "top.h"), f.Probe(a + Path.DirectorySeparatorChar, "top.h"));
        Assert.Equal(Path.GetFullPath(Path.Combine(a, "sub", "deep.h")), f.Probe(a, "sub/deep.h"));
        Assert.Equal(Path.GetFullPath(Path.Combine(work, "b", "other.h")), f.Probe(Path.Combine(a, "sub"), "../../b/other.h"));
        Assert.Null(f.Probe(a, "deep.h"));                       // only in a subdirectory
        Assert.Null(f.Probe(a, "sub"));                          // a directory is not a file
        Assert.Null(f.Probe(Path.Combine(work, "nowhere"), "top.h"));
        Assert.Null(f.Probe(a, "bad\0name.h"));
        Assert.Null(f.Probe(a, "sub/\0.h"));                     // not a path at all
    }

    [Fact]
    public void EachDirectory_IsListedOnce()
    {
        var f = new FileLookup();
        var a = Path.Combine(work, "a");
        for (var i = 0; i < 50; i++) { f.Probe(a, "top.h"); f.Probe(a, $"no{i}.h"); f.Exists(Path.Combine(a, "sub", $"x{i}.h")); }
        Assert.Equal(2, f.DirectoriesListed);
    }

    [Fact]
    public void Exists_MatchesTheFileSystem()
    {
        var f = new FileLookup();
        Assert.True(f.Exists(Path.Combine(work, "b", "other.h")));
        Assert.False(f.Exists(Path.Combine(work, "b", "missing.h")));
        Assert.False(f.Exists(Path.Combine(work, "a", "sub")));
        // Names compare as the file system does: case folds on Windows and macOS only.
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), f.Exists(Path.Combine(work, "b", "OTHER.H")));
    }
}
