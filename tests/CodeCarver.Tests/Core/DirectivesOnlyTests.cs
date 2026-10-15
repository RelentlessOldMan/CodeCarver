using System.Text;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Core;

/// <summary>
/// The one-pass <see cref="TemplateInstances.DirectivesOnly"/> gives the text the line-splitting version it replaces
/// gave, on text full of the edges (continuations at the end, before an empty line, CRLF, stray '\r's, leading
/// whitespace of every kind), and stays linear on a directive continued over many lines.
/// </summary>
public sealed class DirectivesOnlyTests
{
    // The version it replaces: re-copied a continued directive for each line it took in.
    static string Reference(string text)
    {
        var sb = new StringBuilder();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (!raw.TrimStart().StartsWith('#')) continue;
            while (raw.EndsWith('\\') && i + 1 < lines.Length) raw = raw[..^1] + " " + lines[++i].TrimEnd('\r');
            sb.Append(raw).Append('\n');
        }
        return sb.ToString();
    }

    static readonly string[] Pieces =
    {
        "#", "#define X 1", "# include <a.h>", "x", "int f(void);", " ", "\t", "\v", "\f", "\\", "\\\\", "\r", "\n",
        "\r\n", "\\\n", "\\\r\n", "\\\r\r\n", ((char)0x85).ToString(), ((char)0xA0).ToString(), "/* c */", "a\\ b",
    };

    [Fact]
    public void Matches_TheLineSplittingVersion()
    {
        var r = new Random(5);
        for (var t = 0; t < 20000; t++)
        {
            var sb = new StringBuilder();
            for (var n = r.Next(0, 30); n > 0; n--) sb.Append(Pieces[r.Next(Pieces.Length)]);
            var text = sb.ToString();
            Assert.Equal(Reference(text), TemplateInstances.DirectivesOnly(text));
        }
    }

    [Fact]
    public void LongContinuation_IsLinear()
    {
        var text = "#define BIG \\\n" + string.Concat(Enumerable.Repeat("    REG_FIELD(a, b, c) \\\n", 200_000)) + "    0\nint x;\n";
        var before = GC.GetAllocatedBytesForCurrentThread();
        var got = TemplateInstances.DirectivesOnly(text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.StartsWith("#define BIG  ", got);
        Assert.EndsWith(" 0\n", got);
        Assert.True(allocated < 40L * text.Length, $"{allocated:N0} bytes for {text.Length:N0} chars");
    }
}
