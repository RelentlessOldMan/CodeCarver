using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A source file the carve could not look inside - over <c>maxParseBytes</c>, or unreadable during the run - is kept
/// whole, never dropped for a placeholder: nothing is known about what it defines, so kept code may need any of it.
/// </summary>
public sealed class UnparsedUnitTests
{
    static TreeCarve Tree() => new TreeCarve()
        .W("main.c", "int big_fn(void);\nint main(void) { return big_fn(); }\n")
        .W("big.c", "int big_fn(void) { return 7; }\n" + string.Concat(Enumerable.Range(0, 60).Select(i => $"int pad_{i}(void) {{ return {i}; }}\n")))
        .W("dead.c", "int dead_fn(void) { return 0; }\n")
        .Compile("main.c").Compile("big.c").Compile("dead.c");

    [Fact]
    public void UnitOverTheParseCap_IsKeptWhole()
    {
        using var t = Tree();
        var r = t.Carve(extraToml: "[advanced]\nmaxParseBytes = 400\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("big.c"), r.Out + r.Err);
        Assert.False(t.Kept("dead.c"));
    }

    [Fact]
    public void UnreadableUnit_IsKeptWhole()
    {
        if (!OperatingSystem.IsWindows()) return;   // a share-none lock blocks readers only on Windows
        using var t = Tree();
        using (new FileStream(Path.Combine(t.Src, "big.c"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = t.Carve();
            Assert.True(r.Code is 0 or 2, r.Out + r.Err);
            if (r.Code == 0) Assert.False(t.Placeholder("big.c"), r.Out + r.Err);
        }
    }
}
