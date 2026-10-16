using System.Text;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>
/// The names a prebuilt binary holds: whole words only, read straight from its bytes, including a word that the
/// file's reads cut in two.
/// </summary>
public sealed class BinaryTextTests
{
    static HashSet<string> NamesIn(byte[] bytes, params string[] names)
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-bintext-" + Guid.NewGuid().ToString("N") + ".o");
        File.WriteAllBytes(path, bytes);
        try { return BinaryText.NamesIn(path, new NameSet(names), names.Max(n => n.Length)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WholeWordsOnly_BetweenAnyNonWordBytes()
    {
        var bytes = Encoding.Latin1.GetBytes("\0\0uart_init\0xuart_send\0uart_sendx 9uart_polléuart_closeÿ$tick\0");
        var found = NamesIn(bytes, "uart_init", "uart_send", "uart_poll", "uart_close", "$tick", "absent");
        Assert.Equal(new[] { "$tick", "uart_close", "uart_init" }, found.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void AWordCutByTheReads_IsReadWhole()
    {
        // Reads are 4 MB: put a name across the boundary, and a longer word holding a name after it.
        const int chunk = 1 << 22;
        var bytes = new byte[chunk + 64];
        var cut = Encoding.ASCII.GetBytes("boot_main");
        Array.Copy(cut, 0, bytes, chunk - 4, cut.Length);
        var longer = Encoding.ASCII.GetBytes("xx_boot_main_and_more");
        Array.Copy(longer, 0, bytes, chunk + 20, longer.Length);
        Assert.Equal(new[] { "boot_main" }, NamesIn(bytes, "boot_main", "boot"));
    }

    [Fact]
    public void AWordLongerThanEveryName_MatchesNone()
    {
        var bytes = Encoding.ASCII.GetBytes("\0abcdefgh\0abc\0");
        Assert.Equal(new[] { "abc" }, NamesIn(bytes, "abc", "abcd"));
    }
}
