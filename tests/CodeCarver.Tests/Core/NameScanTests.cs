using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Core;

/// <summary>
/// The one-pass name search answers exactly as the regex alternation it replaces, in every shape: the same matches at
/// the same places, the same names, the same replacements, on text full of the edges (a name inside a longer one, after
/// a digit or a <c>$</c>, before a <c>(</c> past odd whitespace, Latin-1 letters).
/// </summary>
public sealed class NameScanTests
{
    static readonly string[] Pieces =
    {
        "FOO", "BAR", "FOO_BAR", "FOOBAR", "BAZ1", "_x", "x", "1FOO", "FOO1", "$", "$FOO", "FOO$", "é", "FOOé", "µFOO",
        "(", ")", " ", "\t", "\v", "\f", "\r\n", "\n", "\u0085", " ", ",", ";", "#", "##", "{", "}", "*", "&", "=", "\"",
    };

    static string RandomText(Random r, int pieces)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pieces; i++) sb.Append(Pieces[r.Next(Pieces.Length)]);
        return sb.ToString();
    }

    public static IEnumerable<object[]> Shapes()
        => Enum.GetValues<NameScan.Shape>().SelectMany(s => new[]
        {
            new object[] { s, new[] { "FOO", "BAR", "FOO_BAR", "x", "FOOé" } },
            new object[] { s, new[] { "FOO", "FOO$" } },              // a name with '$': answered by the regex itself
        });

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Matches_AreTheRegexs(NameScan.Shape shape, string[] names)
    {
        var scan = new NameScan(names, shape);
        var rx = new Regex(NameScan.Pattern(names, shape));
        var r = new Random(7);
        for (var t = 0; t < 3000; t++)
        {
            var text = RandomText(r, r.Next(1, 40));
            var want = rx.Matches(text).Select(m => (m.Index, m.Length, m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value)).ToList();
            var got = scan.Matches(text).Select(m => (m.Index, m.Length, m.Name)).ToList();
            Assert.True(want.SequenceEqual(got), $"{shape} on [{Regex.Escape(text)}]: want {string.Join(" ", want)} got {string.Join(" ", got)}");
            Assert.Equal(rx.IsMatch(text), scan.IsMatch(text));
            Assert.Equal(rx.Replace(text, m => "<" + m.Length + ">"), scan.Replace(text, m => "<" + m.Length + ">"));
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void RangeMatches_AreTheSubstrings(NameScan.Shape shape, string[] names)
    {
        var scan = new NameScan(names, shape);
        var r = new Random(11);
        for (var t = 0; t < 3000; t++)
        {
            var text = RandomText(r, r.Next(1, 40));
            var start = r.Next(text.Length + 1);
            var end = start + r.Next(text.Length - start + 1);
            var part = text[start..end];
            var want = scan.Matches(part).Select(m => (start + m.Index, m.Length, m.Name)).ToList();
            var got = scan.Matches(text, start, end).Select(m => (m.Index, m.Length, m.Name)).ToList();
            Assert.True(want.SequenceEqual(got), $"{shape} on [{Regex.Escape(text)}] {start}..{end}");
            Assert.Equal(scan.IsMatch(part), scan.IsMatch(text, start, end));
        }
    }

    [Fact]
    public void ManyNames_TakeOnePass()
    {
        var names = Enumerable.Range(0, 5000).Select(i => "MODK_REG_" + i).ToList();
        var scan = new NameScan(names, NameScan.Shape.Call);
        var text = string.Concat(Enumerable.Repeat("    MODK_REG_17 (p, MODK_REG_4999(x)); other_fn(MODK_REG_5000(y));\n", 20000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(40000, scan.Matches(text).Count());
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }
}
