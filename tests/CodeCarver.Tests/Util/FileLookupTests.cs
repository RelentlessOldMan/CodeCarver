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

    /// <summary>A directory that can't be listed (a dead share, no permission) is tried once: after that its files are
    /// asked about one by one — still answered right, never "all missing", and without another listing attempt (a
    /// network timeout each) per probe.</summary>
    [Fact]
    public void UnlistableDirectory_IsTriedOnce_ThenCheckedPerFile()
    {
        var b = Path.Combine(work, "b");
        var attempts = 0;
        var f = new FileLookup(d =>
        {
            if (string.Equals(d, b, StringComparison.OrdinalIgnoreCase)) { attempts++; throw new IOException("network name no longer available"); }
            return Directory.EnumerateFiles(d);
        });
        for (var i = 0; i < 20; i++)
        {
            Assert.True(f.Exists(Path.Combine(b, "other.h")));
            Assert.Equal(Path.Combine(b, "other.h"), f.Probe(b, "other.h"));
            Assert.Null(f.Probe(b, $"no{i}.h"));
        }
        Assert.Equal(1, attempts);
        Assert.Equal(1, f.DirectoriesUnlistable);
        Assert.Equal(0, f.DirectoriesListed);
        Assert.True(f.Exists(Path.Combine(work, "a", "top.h")));   // other directories are listed as usual
        Assert.Equal(1, f.DirectoriesListed);
    }

    /// <summary>A root directory already ends in its separator: the prefix must not get a second one (a carve root at
    /// a drive or share root matched nothing).</summary>
    [Fact]
    public void DirectoryPrefix_NeverDoublesTheSeparator()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal("/", PathComparer.DirectoryPrefix("/", '/'));
        Assert.Equal("/usr/src/", PathComparer.DirectoryPrefix("/usr/src", '/'));
        Assert.Equal("/usr/src/", PathComparer.DirectoryPrefix("/usr/src/", '/'));
        Assert.Equal("a" + sep + "b" + sep, PathComparer.DirectoryPrefix("a" + sep + "b"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\", PathComparer.DirectoryPrefix(@"C:\"));
            Assert.Equal(@"\\host\share\", PathComparer.DirectoryPrefix(@"\\host\share\"));
            Assert.Equal(@"C:\work\", PathComparer.DirectoryPrefix(@"C:\work"));
        }
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
