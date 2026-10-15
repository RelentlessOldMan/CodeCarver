using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The definer scan reads a file's function-like <c>#define</c>s and tests tokens for identifiers by hand, where the
/// regexes it replaced made a match (or a runner) per define and per token. It must find exactly what they found.
/// </summary>
public sealed class DefinerScanTests
{
    static readonly string[] Texts =
    {
        "#define A(x) x\n#define B 1\n  # define C( y ) y + \\\n  more\n#define D(z) \\\r\n z\r\n",
        "#define\tTAB(a)\tb\n#defineX(a) no\n#define 9bad(a) no\n#define _u9(a) ok\\",
        "int x; /* #define NOT(a) */\n\t#  define  SP (a) object\n#define E() \\\\\n#define F(p) after\n",
        "#define G(a) one \\\r continues \\x \\\n two\n#define H(b)",
        "#define ét(a) no\n#define É(a) no\n#define né(a) yes\n#define m١(a) yes",
        "", "#", "#define", "#define ", "#define Q(", "\n\n#define R(a) r\n",
        "x \\\n#define S(a) s\n#define T 1 \\\n#define U(a) inside T\n#define V(a) v",
    };

    [Fact]
    public void FunctionLikeDefines_FindWhatTheRegexFound()
    {
        foreach (var text in Texts)
        {
            var want = DefinerMacros.AnyDefine.Matches(text).Where(m => m.Groups[2].Success)
                .Select(m => (m.Groups[1].Value, m.Groups[3].Value)).ToList();
            Assert.Equal(want, DefinerMacros.FunctionLikeDefines(text).ToList());
        }
    }

    [Fact]
    public void IsIdent_AgreesWithTheRegex()
    {
        var ident = new Regex(@"^[A-Za-z_]\w*$");
        foreach (var s in new[] { "", "a", "_", "a1", "1a", "a b", "a-b", "é", "aé", "a١", "a‍", "##", "(", "abc\n", "_9_" })
            Assert.Equal(ident.IsMatch(s) && !s.EndsWith('\n'), DefinerMacros.IsIdent(s));
    }
}
