using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Names built by a macro that pastes its own parameters: <c>#define CAT(a, b) a##b</c> builds nothing a body scan
/// can see, but a wrapper <c>#define DESC(n) CAT(n, _desc)</c> used as <c>DESC(uart)</c> builds <c>uart_desc</c>.
/// Without this, the definition such a name refers to looked unused and a carve inside the file removed it (work
/// eval, 1.0.189: the build said "undeclared").
///
/// <see cref="Build"/> learns, for each function-like macro, what its pastes build in terms of its own parameters,
/// through any depth of wrappers. A use names its target exactly (<see cref="UseFragments"/>): linking the macro to
/// every name ending in <c>_desc</c> instead kept far more than the use needs (1.0.194). Only a use with an argument
/// that is not a plain name (an expression, or an object-like macro that may expand first) falls back to the literal
/// prefix or suffix, and only for that use.
/// </summary>
public static class PasteMacros
{
    /// <summary>One piece of a pasted name: literal text, or the macro's parameter <see cref="Param"/> (-1: unknown).</summary>
    public readonly record struct Piece(string? Text, int Param)
    {
        public bool IsText => Text is not null;
    }

    public enum Kind { Prefix, Suffix, Exact }

    /// <summary>Per macro, the templates its pastes build (each holding a parameter), and the names a macro's own
    /// body builds outright (a pasting macro called with literals inside another macro's body).</summary>
    public sealed class Table
    {
        public readonly Dictionary<string, List<Piece[]>> Templates = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> BodyNames = new(StringComparer.Ordinal);
        public bool IsEmpty => Templates.Count == 0 && BodyNames.Count == 0;
    }

    private static readonly Regex Token = new(@"##|[A-Za-z_]\w*|\S", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);
    const int MaxPerMacro = 32;

    /// <summary>The templates of each macro: a <c>##</c> chain in its body with two or more parameters
    /// (<c>a##b</c>), and its calls of such macros with its own arguments put in. <paramref name="isObjectMacro"/>
    /// names the object-like macros: such an argument may expand before it is pasted, so it is unknown.</summary>
    public static Table Build(IReadOnlyDictionary<string, (bool FnLike, List<string> Bodies)> bodies, Func<string, bool> isObjectMacro)
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

        var table = new Table();
        bool AddT(string m, Piece[] t)
        {
            if (!table.Templates.TryGetValue(m, out var l)) table.Templates[m] = l = new List<Piece[]>();
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
                    if (toks[i + 1] != "(" || toks[i] == name || !table.Templates.TryGetValue(toks[i], out var inner)) continue;
                    var args = SplitArgs(toks, i + 1);
                    foreach (var t in inner.ToList())
                    {
                        var nt = Substitute(t, args, ps, isObjectMacro);
                        if (nt.Any(p => !p.IsText)) { if (AddT(name, nt)) added = true; }
                        else
                        {
                            if (!table.BodyNames.TryGetValue(name, out var names)) table.BodyNames[name] = names = new HashSet<string>(StringComparer.Ordinal);
                            names.Add(string.Concat(nt.Select(p => p.Text)));
                        }
                    }
                }
            if (!added) break;
        }
        return table;
    }

    /// <summary>The names macro <paramref name="name"/>'s own body builds outright.</summary>
    public static IEnumerable<string> BodyNames(Table table, string name)
        => table.BodyNames.TryGetValue(name, out var s) ? s : Enumerable.Empty<string>();

    /// <summary>What one use <c>name(args)</c> builds; <paramref name="argumentText"/> is the text inside the
    /// parentheses. All-literal: the exact name. Otherwise the literal prefix and suffix around the unknown part.</summary>
    public static IEnumerable<(Kind Kind, string Frag)> UseFragments(Table table, string name, string argumentText, Func<string, bool> isObjectMacro)
    {
        if (!table.Templates.TryGetValue(name, out var list)) yield break;
        var toks = new List<string> { "(" };
        toks.AddRange(Token.Matches(argumentText).Select(m => m.Value));
        toks.Add(")");
        var args = SplitArgs(toks, 0);
        foreach (var t in list)
        {
            var s = Substitute(t, args, new List<string>(), isObjectMacro);
            if (s.All(p => p.IsText)) { yield return (Kind.Exact, string.Concat(s.Select(p => p.Text))); continue; }
            if (s[0].IsText && s[0].Text!.Length > 0) yield return (Kind.Prefix, s[0].Text!);
            if (s[^1].IsText && s[^1].Text!.Length > 0) yield return (Kind.Suffix, s[^1].Text!);
        }
    }

    /// <summary>Uses of the macros with templates in <paramref name="code"/> (already <see cref="SourceText.CodeOnly"/>:
    /// directives blanked, so a use inside another macro's body is not one): the 1-based line, the macro, and the
    /// text inside its parentheses.</summary>
    public static IEnumerable<(int Line, string Macro, string Args)> Uses(Table table, Regex use, string code)
    {
        List<int>? lineStarts = null;
        foreach (Match m in use.Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var depth = 0;
            var close = -1;
            for (var k = open; k < code.Length; k++)
            {
                if (code[k] == '(') depth++;
                else if (code[k] == ')' && --depth == 0) { close = k; break; }
            }
            if (close < 0) continue;
            if (lineStarts is null)
            {
                lineStarts = new List<int> { 0 };
                for (var k = 0; k < code.Length; k++) if (code[k] == '\n') lineStarts.Add(k + 1);
            }
            var at = lineStarts.BinarySearch(m.Index);
            yield return ((at >= 0 ? at : ~at - 1) + 1, m.Groups[1].Value, code[(open + 1)..close]);
        }
    }

    /// <summary>A regex finding a use of any macro with templates (<c>NAME (</c>), or null when there are none.</summary>
    public static Regex? UseRegex(Table table)
        => table.Templates.Count == 0 ? null
           : new Regex(@"(?<![\w$])(" + string.Join("|", table.Templates.Keys.Select(Regex.Escape)) + @")\s*\(", RegexOptions.Compiled);

    /// <summary>Template <paramref name="t"/> with each parameter replaced by its argument: a plain name or a paste
    /// chain of names and the caller's parameters <paramref name="ps"/>; anything else is unknown, and so is an
    /// object-like macro outside a paste (it may expand first).</summary>
    static Piece[] Substitute(Piece[] t, List<List<string>> args, List<string> ps, Func<string, bool> isObjectMacro)
    {
        var parts = new List<Piece>();
        foreach (var p in t)
        {
            if (p.IsText) { parts.Add(p); continue; }
            var a = p.Param >= 0 && p.Param < args.Count ? args[p.Param] : null;
            if (a is { Count: > 0 } && ChainAt(a, 0, out var chain, out var end) && end == a.Count - 1
                && !(chain.Count == 1 && ps.IndexOf(chain[0]) < 0 && isObjectMacro(chain[0])))
                parts.AddRange(Pieces(chain, ps));
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
