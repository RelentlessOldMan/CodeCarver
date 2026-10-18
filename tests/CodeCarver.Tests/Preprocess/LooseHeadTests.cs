using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The check's gate on whether a file may hold a macro-named head reads the text by hand: the regex it was read a gap
/// again from every name before it, so a header of n #define lines with no head cost n² (8,000 lines: 4 s, a stall on
/// a dropped file of a real tree). The old regex is kept here and both must say the same.
/// </summary>
public sealed class LooseHeadTests
{
    static readonly Regex LooseHead = new(
        @"\b[A-Za-z_]\w*(?>(?:\s+|#(?:[^\\\n]|\\(?:\r?\n|.))*)*)\((?:[^()\n]|\([^()\n]*\))*\)(?>(?:\s+|#(?:[^\\\n]|\\(?:\r?\n|.))*)*)\(");

    static bool Old(string t) => t.Contains(')') && LooseHead.IsMatch(t);

    [Theory]
    [InlineData("DEF(a, b)(int x) {")]
    [InlineData("DEF(a)\n#if X\n  (int x)\n#endif\n{")]
    [InlineData("DEF(a) \\\n (int x)")]
    [InlineData("DEF(f(a), (b))(x)")]
    [InlineData("DEF(f(g(a)))(x)")]
    [InlineData("DEF(a\n)(x)")]
    [InlineData("int f(int x);")]
    [InlineData("#define A(x) (x)\n")]
    [InlineData("#define A B \\\n(x)\n(y)")]
    [InlineData("#define A B \\\r\n(x)\n(y)")]
    [InlineData("F(a)\n# x \\")]
    [InlineData("F(a)\n# x \\\n")]
    [InlineData("F(a) # x \\\\\n(")]
    [InlineData("9F(a)(b)")]
    [InlineData("x9F(a)(b)")]
    [InlineData("éF(a)(b)")]
    [InlineData("_é(a)(b)")]
    [InlineData("F(a)#(b)\n(c)")]
    [InlineData("F(a)\r\n\t(b")]
    [InlineData("")]
    [InlineData(")")]
    public void SaysWhatTheRegexSaid(string text) => Assert.Equal(Old(text), TemplateInstances.MayHaveMacroNamedHead(text));

    [Fact]
    public void SaysWhatTheRegexSaid_OnRandomText()
    {
        var rnd = new Random(7);
        var bits = new[] { "F", "_g1", "é", "9", "(", ")", "(a)", " ", "\t", "\n", "\r\n", "#", "#if X\n", "\\", "\\\n", "x", ";", "{", "/*", "*/" };
        for (var n = 0; n < 20_000; n++)
        {
            var sb = new StringBuilder();
            for (var k = rnd.Next(1, 24); k > 0; k--) sb.Append(bits[rnd.Next(bits.Length)]);
            var t = sb.ToString();
            Assert.True(Old(t) == TemplateInstances.MayHaveMacroNamedHead(t), $"differs on [{t.Replace("\n", "\\n")}]");
        }
    }

    [Theory]
    [InlineData("#define A_{0} B_{0}\n")]
    [InlineData("#define A_{0} B_{0}(x)\n")]
    [InlineData("#define A_{0} \\\n   B_{0}\n")]
    [InlineData("#define A_{0} ((x) + B_{0})\n")]
    public void ALongDefineTableWithNoHead_IsReadInLinearTime(string line)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 40_000; i++) sb.Append(string.Format(line, i));
        var text = sb.ToString();
        var sw = Stopwatch.StartNew();
        Assert.False(TemplateInstances.MayHaveMacroNamedHead(text));
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"{sw.ElapsedMilliseconds} ms"); // the regex: ~100 s
    }
}
