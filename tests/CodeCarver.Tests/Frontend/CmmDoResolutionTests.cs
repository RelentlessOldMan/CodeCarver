using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Review CM1: a `DO` resolves as a path first (beside the caller, then from the root), and an
/// ambiguous basename binds to every match instead of the first.</summary>
public sealed class CmmDoResolutionTests
{
    static CmmClosureResult Closure(Dictionary<string, string> files, params string[] observed) =>
        CmmTraceClosure.Compute(files.Select(kv => (kv.Key, (long)kv.Value.Length)).ToList(), observed,
            rel => files[rel], 20_000_000, dropUnobserved: true);

    [Fact]
    public void AmbiguousBasename_ResolvesBesideTheCaller()
    {
        var r = Closure(new()
        {
            ["t1/init.cmm"] = "A:\n RETURN\n",
            ["t2/init.cmm"] = "B:\n RETURN\n",
            ["t2/flash.cmm"] = "DO init.cmm\n",
        }, "t2/flash.cmm");
        Assert.Contains("t2/init.cmm", r.Kept);
        Assert.Contains("t1/init.cmm", r.Dropped);
    }

    [Fact]
    public void AmbiguousBasename_NotBesideCaller_BindsAll()
    {
        var r = Closure(new()
        {
            ["t1/init.cmm"] = "A:\n RETURN\n",
            ["t2/init.cmm"] = "B:\n RETURN\n",
            ["app/flash.cmm"] = "DO init\n",
            ["app/other.cmm"] = "O:\n RETURN\n",
        }, "app/flash.cmm");
        Assert.Contains("t1/init.cmm", r.Kept);
        Assert.Contains("t2/init.cmm", r.Kept);
        Assert.Contains("app/other.cmm", r.Dropped);
    }

    [Fact]
    public void RelativeParentPath_AndQuotedPathWithSpace()
    {
        var r = Closure(new()
        {
            ["common/x.cmm"] = "X:\n RETURN\n",
            ["other/x.cmm"] = "Y:\n RETURN\n",
            ["boards/my board.cmm"] = "Z:\n RETURN\n",
            ["app/flash.cmm"] = "DO ../common/x.cmm\nDO \"../boards/my board.cmm\"\n",
        }, "app/flash.cmm");
        Assert.Contains("common/x.cmm", r.Kept);
        Assert.Contains("other/x.cmm", r.Dropped);
        Assert.Contains("boards/my board.cmm", r.Kept);
    }

    [Fact]
    public void UnreadableScript_IsKeptAndWarned()
    {
        var files = new Dictionary<string, string> { ["a.cmm"] = "DO b\n", ["b.cmm"] = "B:\n RETURN\n", ["c.cmm"] = "" };
        var r = CmmTraceClosure.Compute(files.Select(kv => (kv.Key, 10L)).ToList(), new[] { "a.cmm", "c.cmm" },
            rel => rel == "c.cmm" ? throw new IOException("locked") : files[rel], 20_000_000, dropUnobserved: true);
        Assert.Contains(r.Warnings, w => w.Contains("could not be read"));
        Assert.Empty(r.Dropped);
    }
}
