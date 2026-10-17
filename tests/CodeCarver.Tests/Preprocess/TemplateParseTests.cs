using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The template walk's header parse reads directives by hand (the regexes' Match and Groups for every #define came to
/// 25 times a header, and a walk re-parses every header the check could not hold). It must read them as the regexes
/// did: the old parse is kept here and both read the same lines.
/// </summary>
public sealed class TemplateParseTests
{
    static readonly Regex Directive = new(@"^[ \t]*#[ \t]*(define|undef|include)\b[ \t]*(.*)$");
    static readonly Regex Conditional = new(@"^[ \t]*#[ \t]*(if|ifdef|ifndef|endif)\b");
    static readonly Regex DefineHead = new(@"^([A-Za-z_]\w*)(\(([^)]*)\))?[ \t]*(.*)$", RegexOptions.Singleline);

    static string StripComment(string s)
    {
        var c = s.IndexOf("//", StringComparison.Ordinal);
        if (c >= 0) s = s[..c];
        return Regex.Replace(s, @"/\*.*?\*/", " ");
    }

    static string Op(string kind, int first, int last, string? text, (List<string>? Params, string Body)? m)
        => $"{kind} {first}-{last} [{text}] " + (m is { } x ? $"({(x.Params is null ? "-" : string.Join(",", x.Params))}) [{x.Body}]" : "");

    // The regex parse, as it was.
    static List<string> OldParse(string text)
    {
        var lines = text.Split('\n');
        var ops = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var first = i;
            if (!raw.TrimStart().StartsWith('#')) { ops.Add(Op("Code", i, i, raw, null)); continue; }
            if (raw.EndsWith('\\') && i + 1 < lines.Length)
            {
                var sb = new StringBuilder(raw);
                while (sb.Length > 0 && sb[^1] == '\\' && i + 1 < lines.Length)
                {
                    sb.Length--;
                    sb.Append(' ').Append(lines[++i].AsSpan().TrimEnd('\r'));
                }
                raw = sb.ToString();
            }
            var d = Directive.Match(raw);
            if (!d.Success)
            {
                var kind = Conditional.Match(raw) is { Success: true } c ? c.Groups[1].Value == "endif" ? "Close" : "Open" : "Other";
                ops.Add(Op(kind, first, i, null, null));
                continue;
            }
            var rest = StripComment(d.Groups[2].Value).Trim();
            switch (d.Groups[1].Value)
            {
                case "define":
                    var h = DefineHead.Match(rest);
                    ops.Add(h.Success
                        ? Op("Define", first, i, h.Groups[1].Value,
                             (h.Groups[2].Success ? h.Groups[3].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : null,
                              h.Groups[4].Value.Trim()))
                        : Op("Define", first, i, null, null));
                    break;
                case "undef":
                    ops.Add(Op("Undef", first, i, rest.Split(' ', '\t')[0], null));
                    break;
                default:
                    // "name" and <name> (death-lite: a template behind an <> include was never walked), and a macro's name.
                    var close = rest.Length >= 2 && rest[0] is '"' or '<' ? rest.IndexOf(rest[0] == '"' ? '"' : '>', 1) : -1;
                    var computed = close < 0 ? Regex.Match(rest, @"^[A-Za-z_]\w*") : Match.Empty;
                    ops.Add(Op("Include", first, i, close >= 0 ? rest[1..close] : computed.Success ? computed.Value : null, null));
                    break;
            }
        }
        return ops;
    }

    public static IEnumerable<object[]> Texts() => new[]
    {
        "#define A 1\n#define B(x, y) ((x)+(y))\nint B(p, q)(void) { }\n",
        "  #  define\tSPACED   value  // trailing\n#define C /* inner */ 2 /* more */\n#define D(x)/*c*/x\n",
        "#define E(\n#define F() empty\n#define G(a,,b) a b\n#define 9bad 1\n#define\n#define _u_ under\n",
        "#define LONG a \\\n  b \\\r\n  c\r\n#define END \\",
        "#undef  A extra\n#undef\tB\n#undef\n#include \"x/y.h\"\n#include <z.h>\n#include \"open\n#include\"\"\n",
        "#if X\n#ifdef Y\n#ifndef Z\n#ifx\n#elif W\n#else\n#endif // c\n#endifx\n#pragma once\n#\n#definex 1\n",
        "\f#define FF 1\n\t#define TAB 2\r\r\n#define CRS 3\r\n\n",
        "#define å 1\n#define aå 2\n#define URL \"http://x\" y\n#define S /* a // b */ c\n#define Q a /* open\n",
        "",
        "\n\n#",
        "#define LAST\\",
        // The name read in place (no copy) where a comment or continuation sits at or before its end.
        "#define /*x*/LEAD 1\n#define GLUED/*x*/1\n#define SP /*x*/ 1\n#undef U/*c*/\n#undef V /*c*/\n#undef\tW\v/*c*/\n#undef /*c*/ X\n",
        "#define \\\nCONT 1\n#undef \\\nUC\n#define NAME\\\n(x) x\n#define K // only //\n#undef // gone\n",
    }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Texts))]
    public void HandParse_ReadsDirectivesAsTheRegexesDid(string text)
        => Assert.Equal(OldParse(text), TemplateInstances.DescribeParse(text).ToList());
}
