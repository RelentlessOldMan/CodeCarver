using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Core;

/// <summary>
/// <see cref="DefinedNameCache"/>: an entry answers only while its file's size and last-write time are unchanged, only
/// for a run that wants no name the writing run didn't ask about (filtered to what this run wants), and a cache that
/// can't be read is no cache.
/// </summary>
public sealed class DefinedNameCacheTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("cc-dnc-").FullName;
    string PathOf => Path.Combine(_dir, "sub", "names.bin");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    void Write(HashSet<string> wanted, params (string Rel, long Size, long Ticks, string[] Names)[] entries)
    {
        var c = new DefinedNameCache(PathOf, wanted);
        foreach (var e in entries) c.Put(e.Rel, e.Size, e.Ticks, e.Names);
        Assert.True(c.Save(PathOf));
    }

    [Fact]
    public void Unchanged_File_IsAnswered_Changed_IsNot()
    {
        Write(Set("FEAT_A", "FEAT_B"), ("inc/big.h", 100, 7, new[] { "FEAT_A", "FEAT_A" }));
        var c = new DefinedNameCache(PathOf, Set("FEAT_A", "FEAT_B"));
        Assert.Equal(new[] { "FEAT_A" }, c.TryGet("inc/big.h", 100, 7));
        Assert.Null(c.TryGet("inc/big.h", 101, 7));
        Assert.Null(c.TryGet("inc/big.h", 100, 8));
        Assert.Null(c.TryGet("inc/other.h", 100, 7));
    }

    [Fact]
    public void FewerWantedNames_FilterTheAnswer_AMoreWantedName_DropsTheCache()
    {
        Write(Set("FEAT_A", "FEAT_B"), ("big.h", 1, 1, new[] { "FEAT_A", "FEAT_B" }));
        Assert.Equal(new[] { "FEAT_B" }, new DefinedNameCache(PathOf, Set("FEAT_B")).TryGet("big.h", 1, 1));
        Assert.Null(new DefinedNameCache(PathOf, Set("FEAT_A", "FEAT_C")).TryGet("big.h", 1, 1));
    }

    [Fact]
    public void Save_KeepsOnlyWhatThisRunAsked_AndItsAnswerSet()
    {
        Write(Set("FEAT_A", "FEAT_B"), ("one.h", 1, 1, new[] { "FEAT_A", "FEAT_B" }), ("two.h", 2, 2, new[] { "FEAT_B" }));
        // A run that wants only FEAT_A and asks about one.h: the cache it leaves answers for FEAT_A, about one.h only.
        var c = new DefinedNameCache(PathOf, Set("FEAT_A"));
        Assert.Equal(new[] { "FEAT_A" }, c.TryGet("one.h", 1, 1));
        Assert.True(c.Save(PathOf));
        Assert.Null(new DefinedNameCache(PathOf, Set("FEAT_A")).TryGet("two.h", 2, 2));
        Assert.Null(new DefinedNameCache(PathOf, Set("FEAT_A", "FEAT_B")).TryGet("one.h", 1, 1));
        Assert.Equal(new[] { "FEAT_A" }, new DefinedNameCache(PathOf, Set("FEAT_A")).TryGet("one.h", 1, 1));
    }

    [Fact]
    public void Missing_Truncated_OrForeign_Cache_IsEmpty()
    {
        Assert.Null(new DefinedNameCache(PathOf, Set("FEAT_A")).TryGet("big.h", 1, 1));
        Write(Set("FEAT_A"), ("big.h", 1, 1, new[] { "FEAT_A" }));
        var bytes = File.ReadAllBytes(PathOf);
        for (var cut = 0; cut < bytes.Length; cut++)
        {
            File.WriteAllBytes(PathOf, bytes[..cut]);
            Assert.Null(new DefinedNameCache(PathOf, Set("FEAT_A")).TryGet("big.h", 1, 1));
        }
        File.WriteAllText(PathOf, "not a cache");
        Assert.Null(new DefinedNameCache(PathOf, Set("FEAT_A")).TryGet("big.h", 1, 1));
    }
}
