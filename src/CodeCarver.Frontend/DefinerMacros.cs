using System.Text.RegularExpressions;

namespace CodeCarver.Frontend;

/// <summary>
/// Function-like macros that DEFINE a symbol named after an argument —
/// <c>#define FW_DECLARE(n, init, fini) const struct fw_desc n##_desc = { init, fini }</c> or
/// <c>#define DEFINE_TASK(name) void name(void)</c> followed by a body. Tree-sitter sees only the macro call, so
/// the symbol it defines had no definition in the graph: a kept file referencing <c>uart_desc</c> did not keep the
/// file holding <c>FW_DECLARE(uart, ...)</c>, which was dropped (work eval, 1.0.159).
///
/// <see cref="Build"/> learns, from every macro body in the tree, which parameter (with which pasted prefix and
/// suffix) lands in a declarator position, including through a wrapper macro that passes its parameter on to
/// another definer. <see cref="Uses"/> then finds each file-scope use and the names it defines.
/// </summary>
public static class DefinerMacros
{
    /// <summary>The use's argument <paramref name="Param"/>, wrapped as Prefix + arg + Suffix, is defined;
    /// <paramref name="Function"/> when it is declared as a function (followed by '(').</summary>
    public readonly record struct Template(int Param, string Prefix, string Suffix, bool Function);

    /// <summary>One file-scope use: the defined names, the identifiers in its arguments, its 1-based line span
    /// (through the body when the macro opens a function head) and whether it has such a body.</summary>
    public readonly record struct Use(string Macro, List<(string Name, bool Function)> Defines, List<string> ArgIdentifiers,
                                      int StartLine, int EndLine, bool HasBody);

    private static readonly Regex Token = new(@"##|[A-Za-z_]\w*|\S", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);
    private static readonly Regex Identifiers = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    // A token before the name that makes it something other than a declared symbol.
    private static readonly HashSet<string> NotDeclaratorPrefix = new(StringComparer.Ordinal)
    {
        "struct", "union", "enum", "class", "return", "case", "goto", "sizeof", "else", "do", "if", "while",
        "for", "switch", "defined", "typedef", "operator", "new", "delete", "throw",
    };
    private static readonly HashSet<string> DeclaratorFollow = new(StringComparer.Ordinal) { "(", "=", "[", ";", "{", ",", ")" };

    /// <summary>The definer macros among <paramref name="bodies"/> (name → function-like flag and the raw
    /// text after the name, which for a function-like macro starts with its parameter list).</summary>
    public static Dictionary<string, List<Template>> Build(IReadOnlyDictionary<string, (bool FnLike, List<string> Bodies)> bodies)
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
                var toks = Token.Matches(flat[(close + 1)..]).Select(m => m.Value).ToList();
                if (toks.Count > 0) parsed.Add((name, ps, toks));
            }
        }

        var result = new Dictionary<string, List<Template>>(StringComparer.Ordinal);
        void AddT(string m, Template t)
        {
            if (!result.TryGetValue(m, out var l)) result[m] = l = new List<Template>();
            if (!l.Contains(t)) l.Add(t);
        }

        // Direct: a parameter (possibly pasted) in a declarator position.
        foreach (var (name, ps, toks) in parsed)
            for (var i = 0; i < toks.Count; i++)
            {
                if (!Chain(toks, i, ps, out var param, out var pre, out var suf, out var end)) continue;
                var prev = i > 0 ? toks[i - 1] : null;
                var next = end + 1 < toks.Count ? toks[end + 1] : null;
                var declPrev = prev == "*" || (prev is not null && Ident.IsMatch(prev) && !NotDeclaratorPrefix.Contains(prev));
                var declNext = next is null || DeclaratorFollow.Contains(next) || Ident.IsMatch(next);
                if (declPrev && declNext) AddT(name, new Template(param, pre, suf, next == "(" && prev != "*"));
                i = end;
            }

        // Through wrappers: a body that calls a definer, passing (a paste of) its own parameter in a defined slot.
        for (var round = 0; round < 8; round++)
        {
            var added = false;
            foreach (var (name, ps, toks) in parsed)
                for (var i = 0; i + 1 < toks.Count; i++)
                {
                    if (toks[i + 1] != "(" || !result.TryGetValue(toks[i], out var inner) || toks[i] == name) continue;
                    var args = SplitArgs(toks, i + 1, out _);
                    foreach (var t in inner.ToList())
                    {
                        if (t.Param >= args.Count) continue;
                        var a = args[t.Param];
                        if (a.Count == 0 || !Chain(a, 0, ps, out var param, out var pre, out var suf, out var end) || end != a.Count - 1) continue;
                        var nt = new Template(param, t.Prefix + pre, suf + t.Suffix, t.Function);
                        if (!result.TryGetValue(name, out var l) || !l.Contains(nt)) { AddT(name, nt); added = true; }
                    }
                }
            if (!added) break;
        }
        return result;
    }

    /// <summary>A paste chain at <paramref name="i"/> — <c>pre ## P ## suf</c> with exactly one parameter P
    /// and no stringizing '#' before it.</summary>
    private static bool Chain(List<string> toks, int i, List<string> ps, out int param, out string pre, out string suf, out int end)
    {
        param = -1; pre = suf = ""; end = i;
        if (!Ident.IsMatch(toks[i]) || (i > 0 && toks[i - 1] == "#")) return false;
        var parts = new List<string> { toks[i] };
        var j = i;
        while (j + 2 < toks.Count && toks[j + 1] == "##" && Ident.IsMatch(toks[j + 2])) { parts.Add(toks[j + 2]); j += 2; }
        end = j;
        var at = -1;
        for (var k = 0; k < parts.Count; k++)
            if (ps.IndexOf(parts[k]) is var pi && pi >= 0)
            {
                if (at >= 0) return false;   // two parameters pasted together: not a name we can predict
                at = k; param = pi;
            }
        if (at < 0) return false;
        pre = string.Concat(parts.Take(at));
        suf = string.Concat(parts.Skip(at + 1));
        return true;
    }

    /// <summary>Top-level comma-separated argument token lists of the call whose '(' is at <paramref name="open"/>.</summary>
    private static List<List<string>> SplitArgs(List<string> toks, int open, out int close)
    {
        var args = new List<List<string>> { new() };
        var depth = 0;
        for (close = open; close < toks.Count; close++)
        {
            var t = toks[close];
            if (t is "(" or "[" or "{") { if (depth++ > 0) args[^1].Add(t); continue; }
            if (t is ")" or "]" or "}") { if (--depth == 0) return args; args[^1].Add(t); continue; }
            if (t == "," && depth == 1) { args.Add(new List<string>()); continue; }
            args[^1].Add(t);
        }
        return args;
    }

    /// <summary>File-scope uses of the definers in <paramref name="text"/>, matched by <paramref name="use"/>
    /// (a regex of the definer names). File scope includes namespace and <c>extern "C"</c> blocks; a use inside
    /// a function, class or initializer is not a definition we could remove whole, so it is skipped.</summary>
    public static IEnumerable<Use> Uses(string text, Regex use, IReadOnlyDictionary<string, List<Template>> definers)
    {
        var code = ImplicitInt.CodeOnly(text);
        var matches = use.Matches(code);
        if (matches.Count == 0) yield break;

        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < code.Length; i++) if (code[i] == '\n') lineStarts.Add(i + 1);
        int LineOf(int index) { var k = lineStarts.BinarySearch(index); return (k >= 0 ? k : ~k - 1) + 1; }

        var opaque = OpaqueDepth(code);
        foreach (Match m in matches)
        {
            if (opaque[m.Index] != 0) continue;
            var macro = m.Groups[1].Value;
            var open = code.IndexOf('(', m.Index + macro.Length);
            var close = Matching(code, open, '(', ')');
            if (close < 0) continue;
            var args = TopLevelArgs(code, open, close);

            var defines = new List<(string, bool)>();
            foreach (var t in definers[macro])
                if (t.Param < args.Count && Ident.IsMatch(args[t.Param]))
                    defines.Add((t.Prefix + args[t.Param] + t.Suffix, t.Function));
            if (defines.Count == 0) continue;

            var end = close;
            var k = close + 1;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
            var hasBody = false;
            if (k < code.Length && code[k] == '{')
            {
                var bodyEnd = Matching(code, k, '{', '}');
                if (bodyEnd < 0) continue;
                end = bodyEnd; hasBody = true;
            }
            var ids = Identifiers.Matches(code.Substring(open + 1, close - open - 1)).Select(x => x.Value).Distinct().ToList();
            yield return new Use(macro, defines.Distinct().ToList(), ids, LineOf(m.Index), LineOf(end), hasBody);
        }
    }

    private static List<string> TopLevelArgs(string code, int open, int close)
    {
        var args = new List<string>();
        var depth = 0; var start = open + 1;
        for (var i = open + 1; i < close; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { args.Add(code[start..i].Trim()); start = i + 1; }
        }
        args.Add(code[start..close].Trim());
        return args;
    }

    private static int Matching(string code, int open, char o, char c)
    {
        if (open < 0 || open >= code.Length || code[open] != o) return -1;
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == o) depth++;
            else if (code[i] == c && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Per index, how many enclosing braces are NOT namespace / extern "C" blocks.</summary>
    private static int[] OpaqueDepth(string code)
    {
        var depth = new int[code.Length + 1];
        var stack = new Stack<bool>();   // true = opaque (function body, class, initializer ...)
        var opaque = 0;
        var stmtStart = 0;               // start of the text since the last ; { }
        for (var i = 0; i < code.Length; i++)
        {
            depth[i] = opaque;
            var c = code[i];
            if (c == '{')
            {
                var head = code.AsSpan(stmtStart, i - stmtStart).Trim();
                var transparent = head.StartsWith("namespace") || head.SequenceEqual("extern") || head.EndsWith("extern");
                stack.Push(!transparent);
                if (!transparent) opaque++;
                stmtStart = i + 1;
            }
            else if (c == '}')
            {
                if (stack.Count > 0 && stack.Pop()) opaque--;
                stmtStart = i + 1;
            }
            else if (c == ';') stmtStart = i + 1;
        }
        depth[code.Length] = opaque;
        return depth;
    }
}
