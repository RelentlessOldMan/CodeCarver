using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The header a computed <c>#include XSTR(IMPL_FILE)</c> names, by expanding its operand the way the preprocessor does:
/// object-like and function-like macros, <c>#</c> stringizing and <c>##</c> pasting (review E3). A macro the tree defines
/// with a value not followed here stands for any text: it comes out as <see cref="Unknown"/>, which
/// <see cref="PathPattern"/> matches against any one path component's worth of characters.
/// </summary>
public static class ComputedInclude
{
    /// <summary>Stands for the text of a macro whose value is not known.</summary>
    public const char Unknown = '\u0001';

    readonly record struct Tok(string Text, bool Space);

    static readonly Regex TokenRe = new(@"(?<s>\s*)(?<t>""(?:[^""\\]|\\.)*""|##|[A-Za-z_]\w*|\d\w*|\S)", RegexOptions.Compiled);
    const int MaxDepth = 32;
    const int MaxTokens = 4096;

    /// <summary>The header name (no quotes or angle brackets) the operand expands to, or null when it doesn't expand to
    /// one. <paramref name="lookup"/> gives a macro's definitions (null params: object-like); <paramref name="unknown"/>
    /// says a name with no definition here is a macro all the same, of unknown value.</summary>
    public static string? Expand(string operand,
                                 Func<string, IReadOnlyList<(IReadOnlyList<string>? Params, string Body)>?> lookup,
                                 Func<string, bool> unknown)
    {
        var budget = MaxTokens;
        var toks = Expand(Tokenize(operand), lookup, unknown, new HashSet<string>(StringComparer.Ordinal), 0, ref budget);
        if (toks is null) return null;
        var text = Join(toks).Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') return text[1..^1];
        if (text.Length >= 2 && text[0] == '<' && text[^1] == '>') return text[1..^1].Trim();
        return null;
    }

    /// <summary>A regex matching a tree-relative path (forward slashes) the header name may be, as a whole path or
    /// its tail after a <c>/</c>.</summary>
    public static Regex PathPattern(string name)
    {
        var sb = new StringBuilder(@"(?:^|/)");
        var pieces = name.Replace('\\', '/').Split(Unknown);
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i > 0) sb.Append(@"[^/]*");
            sb.Append(Regex.Escape(pieces[i]));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static List<Tok> Tokenize(string text)
    {
        var list = new List<Tok>();
        foreach (Match m in TokenRe.Matches(text)) list.Add(new Tok(m.Groups["t"].Value, m.Groups["s"].Length > 0));
        return list;
    }

    static string Join(IEnumerable<Tok> toks)
    {
        var sb = new StringBuilder();
        foreach (var t in toks) { if (t.Space && sb.Length > 0) sb.Append(' '); sb.Append(t.Text); }
        return sb.ToString();
    }

    static List<Tok>? Expand(List<Tok> toks, Func<string, IReadOnlyList<(IReadOnlyList<string>? Params, string Body)>?> lookup,
                             Func<string, bool> unknown, HashSet<string> active, int depth, ref int budget)
    {
        if (depth > MaxDepth || (budget -= toks.Count) < 0) return null;
        var output = new List<Tok>();
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (!IsIdent(t.Text) || active.Contains(t.Text)) { output.Add(t); continue; }
            var defs = lookup(t.Text);
            if (defs is not { Count: > 0 })
            {
                output.Add(unknown(t.Text) ? new Tok(Unknown.ToString(), t.Space) : t);
                continue;
            }
            var def = defs[0];
            List<Tok> body;
            if (def.Params is null) body = Paste(Tokenize(def.Body));
            else
            {
                if (i + 1 >= toks.Count || toks[i + 1].Text != "(") { output.Add(t); continue; }
                var args = Args(toks, i + 1, out var close);
                if (args is null) return null;
                body = Substitute(def.Params, def.Body, args, lookup, unknown, active, depth, ref budget)!;
                if (body is null) return null;
                i = close;
            }
            var inner = new HashSet<string>(active, StringComparer.Ordinal) { t.Text };
            var expanded = Expand(body, lookup, unknown, inner, depth + 1, ref budget);
            if (expanded is null) return null;
            for (var k = 0; k < expanded.Count; k++) output.Add(k == 0 ? expanded[k] with { Space = t.Space } : expanded[k]);
        }
        return output;
    }

    /// <summary>The arguments of the call whose `(` is at <paramref name="open"/>, and the index of its `)`.</summary>
    static List<List<Tok>>? Args(List<Tok> toks, int open, out int close)
    {
        var args = new List<List<Tok>> { new() };
        var depth = 0;
        for (var k = open + 1; k < toks.Count; k++)
        {
            var x = toks[k].Text;
            if (x == "(") depth++;
            else if (x == ")" && depth-- == 0) { close = k; return args; }
            else if (x == "," && depth == 0) { args.Add(new List<Tok>()); continue; }
            args[^1].Add(toks[k]);
        }
        close = -1;
        return null;
    }

    static List<Tok>? Substitute(IReadOnlyList<string> ps, string bodyText, List<List<Tok>> args,
                                 Func<string, IReadOnlyList<(IReadOnlyList<string>? Params, string Body)>?> lookup,
                                 Func<string, bool> unknown, HashSet<string> active, int depth, ref int budget)
    {
        var body = Tokenize(bodyText);
        var result = new List<Tok>();
        int Param(string name) { for (var p = 0; p < ps.Count; p++) if (ps[p] == name) return p < args.Count ? p : -1; return -1; }
        for (var i = 0; i < body.Count; i++)
        {
            var b = body[i];
            if (b.Text == "#" && i + 1 < body.Count && Param(body[i + 1].Text) is var sp && sp >= 0)
            {
                result.Add(new Tok("\"" + Join(args[sp]).Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", b.Space));
                i++;
                continue;
            }
            var p = IsIdent(b.Text) ? Param(b.Text) : -1;
            if (p < 0) { result.Add(b); continue; }
            var pasted = (i > 0 && body[i - 1].Text == "##") || (i + 1 < body.Count && body[i + 1].Text == "##");
            var arg = pasted ? args[p] : Expand(args[p], lookup, unknown, active, depth + 1, ref budget);
            if (arg is null) return null;
            for (var k = 0; k < arg.Count; k++) result.Add(k == 0 ? arg[k] with { Space = b.Space } : arg[k]);
        }
        return Paste(result);
    }

    static List<Tok> Paste(List<Tok> toks)
    {
        for (var i = toks.Count - 2; i > 0; i--)
            if (toks[i].Text == "##")
            {
                toks[i - 1] = toks[i - 1] with { Text = toks[i - 1].Text + toks[i + 1].Text };
                toks.RemoveRange(i, 2);
            }
        return toks;
    }

    static bool IsIdent(string s) => s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_');
}
