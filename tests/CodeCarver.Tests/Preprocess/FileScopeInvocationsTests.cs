using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

public class FileScopeInvocationsTests
{
    // Review CPU C18: UseOf finds whole-word occurrences with IndexOf, not a regex built per call. Random bodies over a
    // small alphabet (word chars, $, #, operators, a non-ASCII letter) must get the answer the regex version gave.
    [Fact]
    public void UseOf_MatchesTheRegexVersion()
    {
        const string alphabet = "fnx_$# \t=,(&{!?:+-/|^<>[*;}é";
        var rng = new Random(18);
        foreach (var param in new[] { "fn", "f", "nn", "fnfn" })
            for (var trial = 0; trial < 5000; trial++)
            {
                var body = new string(Enumerable.Range(0, rng.Next(0, 24)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
                if (rng.Next(3) == 0) body = body.Insert(rng.Next(body.Length + 1), "return " + param);
                Assert.True(RegexUseOf(body, param) == FileScopeInvocations.UseOf(body, param), $"'{body}' / {param}");
            }
    }

    // UseOf as it was, with the regex.
    static FileScopeInvocations.ParamUse RegexUseOf(string body, string param)
    {
        if (param.Length == 0) return FileScopeInvocations.ParamUse.None;
        var result = FileScopeInvocations.ParamUse.None;
        foreach (Match m in Regex.Matches(body, @"(?<![\w$])" + Regex.Escape(param) + @"(?![\w$])"))
        {
            var p = m.Index - 1;
            while (p >= 0 && char.IsWhiteSpace(body[p])) p--;
            var n = m.Index + m.Length;
            while (n < body.Length && char.IsWhiteSpace(body[n])) n++;
            if (p >= 0 && body[p] == '#') continue;
            if (n + 1 < body.Length && body[n] == '#' && body[n + 1] == '#') continue;
            if (p < 0 || "=,(&{!?:+-/|^<>[".Contains(body[p])) return FileScopeInvocations.ParamUse.References;
            if (char.IsLetterOrDigit(body[p]) || body[p] is '_' or '*' or '$')
            {
                var w = p;
                while (w >= 0 && (char.IsLetterOrDigit(body[w]) || body[w] == '_')) w--;
                if (body[(w + 1)..(p + 1)] == "return") return FileScopeInvocations.ParamUse.References;
                result = FileScopeInvocations.ParamUse.Declares;
            }
            else return FileScopeInvocations.ParamUse.References;
        }
        return result;
    }
}
