using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>The hand tokenizers must find exactly what the regexes they replaced found.</summary>
public sealed class CodeTokensTests
{
    private static readonly Regex Tok = new(@"[A-Za-z_]\w*|\d\w*|\S", RegexOptions.CultureInvariant);
    private static readonly Regex MacroTok = new(@"##|[A-Za-z_]\w*|\S");
    private static readonly Regex Name = new(@"[A-Za-z_]\w*");

    private static void Same(string code)
    {
        Assert.Equal(Tok.Matches(code).Select(m => (m.Index, m.Value)), CodeTokens.Scan(code));
        Assert.Equal(MacroTok.Matches(code).Select(m => m.Value), CodeTokens.MacroTokens(code));
        Assert.Equal(Name.Matches(code).Select(m => (m.Index, m.Index + m.Length)), CodeTokens.Names(code));
    }

    [Fact]
    public void Code_TokensAsTheRegexFindsThem()
    {
        Same("");
        Same("int f0_1(int a) { return a + 0x1Fu; } /* x */ REG_WRITE(BASE_3, (VAL << 3) | MASK\n#define M(x) ((x)+1)\n");
        Same("  \t\r\n 9abc _ __x a$b ##x ### a##b 1.5e+3 'c' \"s\" ->>>= #");
    }

    [Fact]
    public void RandomText_WithUnicode_TokensAsTheRegexFindsThem()
    {
        // Every kind of character the regexes treat specially: ASCII, Unicode letters/digits/marks/connectors,
        // Unicode and control spaces, joiners, surrogate halves.
        var pool = "aZ_09 \t\r\n(){};,.##$@`\\\"'+-*/<>=!&|^~?:[]%"
            + string.Concat(new[] { 0xB, 0xC, 0x1C, 0x85, 0xE9, 0xDF, 0x414, 0x436, 0x4E2D, 0x663, 0x966, 0x301, 0x903, 0x203F, 0x2040, 0xA0, 0x2003, 0x3000, 0x2028, 0x200B, 0x200C, 0x200D, 0xD83D, 0xDE00, 0xB2, 0x2160, 0xAA }.Select(c => (char)c));
        var rng = new Random(1234);
        for (var round = 0; round < 3000; round++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 60);
            for (var i = 0; i < len; i++) sb.Append(pool[rng.Next(pool.Length)]);
            Same(sb.ToString());
        }
    }

    [Fact]
    public void Scan_RepeatedName_OneString()
    {
        var toks = CodeTokens.Scan("abc + abc");
        Assert.Same(toks[0].S, toks[2].S);
    }

    [Fact]
    public void NameSet_FindsItsOwnNamesOnly()
    {
        var names = Enumerable.Range(0, 1000).Select(i => "N" + i).Append("x").Append("").ToList();
        var set = new NameSet(names);
        foreach (var n in names) Assert.Same(n, set.Find(("<" + n + ">").AsSpan(1, n.Length)));
        Assert.Null(set.Find("N1000"));
        Assert.Null(set.Find("y"));
        Assert.Null(new NameSet(Array.Empty<string>()).Find("x"));
    }
}
