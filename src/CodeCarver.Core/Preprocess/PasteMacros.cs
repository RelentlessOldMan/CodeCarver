using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Names built by a macro that pastes its own parameters: <c>#define CAT(a, b) a##b</c> builds nothing a body scan
/// can see, but a wrapper <c>#define DESC(n) CAT(n, _desc)</c> builds names ending in <c>_desc</c>, and a use
/// <c>CAT(uart, _desc)</c> in code builds <c>uart_desc</c>. Without this, the definition such a name refers to looked
/// unused and a carve inside the file removed it (work eval, 1.0.189: the build said "undeclared").
///
/// <see cref="Build"/> learns, for each function-like macro, what its pastes build in terms of its own parameters,
/// through any depth of wrappers. <see cref="Fragments(IReadOnlyDictionary{string, List{Piece[]}}, string)"/> gives the literal
/// pieces a macro's own text adds; <see cref="Fragments(IReadOnlyDictionary{string, List{Piece[]}}, string, string)"/>
/// those of one use with its arguments.
/// </summary>
public static class PasteMacros
{
    /// <summary>One piece of a pasted name: literal text, or the macro's parameter <see cref="Param"/> (-1: unknown).</summary>
    public readonly record struct Piece(string? Text, int Param)
    {
        public bool IsText => Text is not null;
    }

    public enum Kind { Prefix, Suffix, Exact }

    private static readonly Regex Token = new(@"##|[A-Za-z_]\w*|\S", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);
    const int MaxPerMacro = 32;

    /// <summary>Per macro, the names its pastes build: from a <c>##</c> chain in its body with two or more parameters
    /// (<c>a##b</c>), and from its calls of such macros, with its own arguments put in. Only templates holding a
    /// parameter are kept: an all-literal paste is already handled where the macro is defined.</summary>
    public static Dictionary<string, List<Piece[]>> Build(IReadOnlyDictionary<string, (bool FnLike, List<string> Bodies)> bodies)
    {
        var parsed = new List<(string Name, List<string> Params, List<string> Tokens)>();
        foreach (var (name, (fnLike, list)) in bodies)
        {
            if (!fnLike) continue;
            foreach (var raw in list)
            {
                var flat = Regex.Replace(raw, @"\\\r?\n", " ");
                var close = flat.IndexOf(')');
                if (close < 0) continue;
                var ps = flat[..close].Split(',').Select(p => p.Trim()).ToList();
                var toks = Token.Matches(SourceText.CodeOnly(flat[(close + 1)..])).Select(m => m.Value).ToList();
                if (toks.Count > 0) parsed.Add((name, ps, toks));
            }
        }

        var result = new Dictionary<string, List<Piece[]>>(StringComparer.Ordinal);
        bool AddT(string m, Piece[] t)
        {
            if (!result.TryGetValue(m, out var l)) result[m] = l = new List<Piece[]>();
            if (l.Count >= MaxPerMacro || l.Any(x => x.SequenceEqual(t))) return false;
            l.Add(t);
            return true;
        }

        // Direct: `a ## b` (two or more parameters, any literals between).
        foreach (var (name, ps, toks) in parsed)
            for (var i = 0; i < toks.Count; i++)
            {
                if (!ChainAt(toks, i, out var parts, out var end)) continue;
                if (i > 0 && toks[i - 1] == "#") { i = end; continue; }   // `#a ## b`: a string, not a name
                var t = Pieces(parts, ps);
                if (t.Count(p => !p.IsText) >= 2) AddT(name, t);
                i = end;
            }

        // Through wrappers: a body that calls a pasting macro, with its own parameters or literals as arguments.
        for (var round = 0; round < 8; round++)
        {
            var added = false;
            foreach (var (name, ps, toks) in parsed)
                for (var i = 0; i + 1 < toks.Count; i++)
                {
                    if (toks[i + 1] != "(" || toks[i] == name || !result.TryGetValue(toks[i], out var inner)) continue;
                    var args = SplitArgs(toks, i + 1);
                    foreach (var t in inner.ToList())
                    {
                        var nt = Substitute(t, args, ps);
                        if (nt.Any(p => !p.IsText) && AddT(name, nt)) added = true;
                    }
                }
            if (!added) break;
        }
        return result;
    }

    /// <summary>The literal pieces macro <paramref name="name"/> adds to the names it builds: a leading literal is a
    /// prefix, a trailing one a suffix.</summary>
    public static IEnumerable<(Kind Kind, string Frag)> Fragments(IReadOnlyDictionary<string, List<Piece[]>> templates, string name)
    {
        if (!templates.TryGetValue(name, out var list)) yield break;
        foreach (var t in list)
            foreach (var f in FragmentsOf(t)) yield return f;
    }

    /// <summary>What one use <c>name(args)</c> builds; <paramref name="argumentText"/> is the text inside the
    /// parentheses. An argument that is not a plain name is unknown.</summary>
    public static IEnumerable<(Kind Kind, string Frag)> Fragments(IReadOnlyDictionary<string, List<Piece[]>> templates, string name, string argumentText)
    {
        if (!templates.TryGetValue(name, out var list)) yield break;
        var toks = new List<string> { "(" };
        toks.AddRange(Token.Matches(SourceText.CodeOnly(argumentText)).Select(m => m.Value));
        toks.Add(")");
        var args = SplitArgs(toks, 0);
        foreach (var t in list)
            foreach (var f in FragmentsOf(Substitute(t, args, new List<string>()))) yield return f;
    }

    static IEnumerable<(Kind, string)> FragmentsOf(Piece[] t)
    {
        if (t.Length == 0) yield break;
        if (t.All(p => p.IsText)) { yield return (Kind.Exact, string.Concat(t.Select(p => p.Text))); yield break; }
        if (t[0].IsText && t[0].Text!.Length > 0) yield return (Kind.Prefix, t[0].Text!);
        if (t[^1].IsText && t[^1].Text!.Length > 0) yield return (Kind.Suffix, t[^1].Text!);
    }

    /// <summary>Template <paramref name="t"/> with each parameter replaced by its argument: a plain name or a paste
    /// chain of names and the caller's parameters <paramref name="ps"/>; anything else is unknown.</summary>
    static Piece[] Substitute(Piece[] t, List<List<string>> args, List<string> ps)
    {
        var parts = new List<Piece>();
        foreach (var p in t)
        {
            if (p.IsText) { parts.Add(p); continue; }
            var a = p.Param >= 0 && p.Param < args.Count ? args[p.Param] : null;
            if (a is { Count: > 0 } && ChainAt(a, 0, out var chain, out var end) && end == a.Count - 1) parts.AddRange(Pieces(chain, ps));
            else parts.Add(new Piece(null, -1));
        }
        return Merge(parts);
    }

    static Piece[] Pieces(List<string> parts, List<string> ps)
        => Merge(parts.Select(x => ps.IndexOf(x) is var k && k >= 0 ? new Piece(null, k) : new Piece(x, 0)));

    /// <summary>Adjacent literals joined (<c>dev_ ## x</c> with x = <c>foo</c> reads <c>dev_foo</c>).</summary>
    static Piece[] Merge(IEnumerable<Piece> parts)
    {
        var list = new List<Piece>();
        foreach (var p in parts)
            if (p.IsText && list.Count > 0 && list[^1].IsText) list[^1] = new Piece(list[^1].Text + p.Text, 0);
            else list.Add(p);
        return list.ToArray();
    }

    /// <summary>Names joined by <c>##</c> starting at <paramref name="i"/> (a lone name is a chain of one).</summary>
    static bool ChainAt(List<string> toks, int i, out List<string> parts, out int end)
    {
        parts = new List<string>();
        end = i;
        if (!Ident.IsMatch(toks[i])) return false;
        parts.Add(toks[i]);
        while (end + 2 < toks.Count && toks[end + 1] == "##" && Ident.IsMatch(toks[end + 2])) { parts.Add(toks[end + 2]); end += 2; }
        return true;
    }

    /// <summary>Top-level comma-separated argument token lists of the call whose '(' is at <paramref name="open"/>.</summary>
    static List<List<string>> SplitArgs(List<string> toks, int open)
    {
        var args = new List<List<string>> { new() };
        var depth = 0;
        for (var i = open; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t is "(" or "[" or "{") { if (depth++ > 0) args[^1].Add(t); continue; }
            if (t is ")" or "]" or "}") { if (--depth == 0) return args; args[^1].Add(t); continue; }
            if (t == "," && depth == 1) { args.Add(new List<string>()); continue; }
            args[^1].Add(t);
        }
        return args;
    }
}
