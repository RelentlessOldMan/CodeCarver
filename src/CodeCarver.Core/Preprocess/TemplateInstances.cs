using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Functions whose NAME a macro computes from the includer's macros: a "template header" instantiated once per
/// <c>#include</c>, <c>#define TNAME red</c> / <c>#include "tmpl.h"</c> where tmpl.h has
/// <c>int CAT(TNAME, get)(void) { ... }</c>. The symbol <c>red_get</c> is defined by the translation unit that includes
/// the header, and no parser reading either file alone can see the name. <see cref="Find"/> walks a translation unit's
/// directives in order (#define, #undef, quoted #include, recursively) like the preprocessor, and expands every
/// macro-named definition head it meets. Conditionals are not evaluated: every branch is read (more names, never fewer).
/// </summary>
public static class TemplateInstances
{
    /// <summary>A definition head whose name is a macro call: <c>NAME(args)(params) {</c>.</summary>
    static readonly Regex MacroNamedHead = new(@"\b([A-Za-z_]\w*)\s*\(((?:[^()\n]|\([^()\n]*\))*)\)\s*\([^(){};]*\)\s*\{", RegexOptions.Compiled);
    static readonly Regex Directive = new(@"^[ \t]*#[ \t]*(define|undef|include)\b[ \t]*(.*)$", RegexOptions.Compiled);
    static readonly Regex DefineHead = new(@"^([A-Za-z_]\w*)(\(([^)]*)\))?[ \t]*(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Quick gate: could <paramref name="text"/> hold a macro-named definition head at all?</summary>
    public static bool HasMacroNamedHead(string text) => text.Contains(')') && MacroNamedHead.IsMatch(text);

    /// <summary>The names a translation unit defines through macro-named heads, with the 1-based line in the unit
    /// (the #include that instantiated it, or the head itself). <paramref name="readInclude"/> returns a quoted
    /// include's text, or null when it can't be found. <paramref name="initial"/>: macros defined before the unit
    /// (the build's command-line <c>-D</c>), so <c>int HIDE(den)(void) {</c> under <c>-D'HIDE(n)=hid_##n'</c> defines hid_den.</summary>
    public static List<(string Name, int Line)> Find(string unit, Func<string, string?> readInclude,
                                                     IReadOnlyDictionary<string, (List<string>? Params, string Body)>? initial = null)
    {
        var found = new List<(string, int)>();
        if (!unit.Contains('#') && (initial is null || initial.Count == 0)) return found;
        var macros = new Dictionary<string, Macro>(StringComparer.Ordinal);
        if (initial is not null) foreach (var kv in initial) macros[kv.Key] = new Macro(kv.Value.Params, kv.Value.Body);
        Walk(unit, macros, readInclude, found, unitLine: null, depth: 0);
        return found;
    }

    sealed record Macro(List<string>? Params, string Body);

    static void Walk(string text, Dictionary<string, Macro> macros, Func<string, string?> readInclude,
                     List<(string, int)> found, int? unitLine, int depth)
    {
        var lines = text.Split('\n');
        var code = new StringBuilder();   // the non-directive text since the last directive
        var codeStartLine = 1;
        void Flush()
        {
            if (code.Length == 0) return;
            var s = code.ToString();
            code.Clear();
            if (!s.Contains('(')) return;
            foreach (Match m in MacroNamedHead.Matches(s))
            {
                if (!macros.ContainsKey(m.Groups[1].Value)) continue;
                var name = Expand(m.Groups[1].Value + "(" + m.Groups[2].Value + ")", macros).Trim();
                if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) continue;
                var line = unitLine ?? codeStartLine + s[..m.Index].Count(c => c == '\n');
                found.Add((name, line));
            }
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var first = i;
            if (raw.TrimStart().StartsWith('#'))
            {
                while (raw.EndsWith('\\') && i + 1 < lines.Length) raw = raw[..^1] + " " + lines[++i].TrimEnd('\r');
                var d = Directive.Match(raw);
                if (!d.Success) continue;
                Flush();
                codeStartLine = i + 2;
                var rest = StripComment(d.Groups[2].Value).Trim();
                switch (d.Groups[1].Value)
                {
                    case "define":
                        var h = DefineHead.Match(rest);
                        if (h.Success)
                            macros[h.Groups[1].Value] = new Macro(
                                h.Groups[2].Success ? h.Groups[3].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : null,
                                h.Groups[4].Value.Trim());
                        break;
                    case "undef":
                        macros.Remove(rest.Split(' ', '\t')[0]);
                        break;
                    case "include":
                        if (depth >= 8 || rest.Length < 2 || rest[0] != '"') break;
                        var close = rest.IndexOf('"', 1);
                        if (close < 0) break;
                        var inc = readInclude(rest[1..close]);
                        if (inc is not null) Walk(inc, macros, readInclude, found, unitLine ?? first + 1, depth + 1);
                        break;
                }
                continue;
            }
            if (code.Length == 0) codeStartLine = i + 1;
            code.Append(raw).Append('\n');
        }
        Flush();
    }

    static string StripComment(string s)
    {
        var c = s.IndexOf("//", StringComparison.Ordinal);
        if (c >= 0) s = s[..c];
        return Regex.Replace(s, @"/\*.*?\*/", " ");
    }

    static readonly Regex Token = new(@"[A-Za-z_]\w*|\d\w*|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|##|\S", RegexOptions.Compiled);

    /// <summary>Expands macros in <paramref name="s"/> (object-like and function-like, # and ##, rescanning, with a
    /// guard against self-reference). Enough to compute a name; not a full preprocessor.</summary>
    public static string Expand(string s, IReadOnlyDictionary<string, (List<string>? Params, string Body)> macros)
        => Expand(s, macros.ToDictionary(kv => kv.Key, kv => new Macro(kv.Value.Params, kv.Value.Body), StringComparer.Ordinal));

    static string Expand(string s, Dictionary<string, Macro> macros)
        => string.Join(" ", ExpandTokens(Token.Matches(s).Select(m => m.Value).ToList(), macros, new HashSet<string>(StringComparer.Ordinal), 0));

    static List<string> ExpandTokens(List<string> toks, Dictionary<string, Macro> macros, HashSet<string> hide, int depth)
    {
        var output = new List<string>();
        if (depth > 32) { output.AddRange(toks); return output; }
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (hide.Contains(t) || !macros.TryGetValue(t, out var m)) { output.Add(t); continue; }
            if (m.Params is null)
            {
                var inner = new HashSet<string>(hide, StringComparer.Ordinal) { t };
                output.AddRange(ExpandTokens(Substitute(m, new List<List<string>>(), macros, hide, depth), macros, inner, depth + 1));
                continue;
            }
            if (i + 1 >= toks.Count || toks[i + 1] != "(") { output.Add(t); continue; }
            // Collect the arguments.
            var args = new List<List<string>> { new() };
            var level = 0; var j = i + 2;
            for (; j < toks.Count; j++)
            {
                var a = toks[j];
                if (a == "(") level++;
                else if (a == ")") { if (level == 0) break; level--; }
                else if (a == "," && level == 0) { args.Add(new List<string>()); continue; }
                args[^1].Add(a);
            }
            if (j >= toks.Count) { output.Add(t); continue; }
            var hidden = new HashSet<string>(hide, StringComparer.Ordinal) { t };
            output.AddRange(ExpandTokens(Substitute(m, args, macros, hide, depth), macros, hidden, depth + 1));
            i = j;
        }
        return output;
    }

    static List<string> Substitute(Macro m, List<List<string>> args, Dictionary<string, Macro> macros, HashSet<string> hide, int depth)
    {
        var body = Token.Matches(m.Body).Select(x => x.Value).ToList();
        var ps = m.Params ?? new List<string>();
        int Param(string tok)
        {
            var k = ps.IndexOf(tok);
            if (k < 0 && tok == "__VA_ARGS__") k = ps.FindIndex(p => p == "...");
            return k;
        }
        List<string> Arg(int k)
        {
            if (k < 0) return new List<string>();
            if (ps[k] == "..." ) return args.Skip(k).SelectMany((a, n) => n == 0 ? a : a.Prepend(",")).ToList();
            return k < args.Count ? args[k] : new List<string>();
        }
        var outp = new List<string>();
        for (var i = 0; i < body.Count; i++)
        {
            var t = body[i];
            if (t == "#" && i + 1 < body.Count && Param(body[i + 1]) is var sp and >= 0)
            {
                outp.Add("\"" + string.Join(" ", Arg(sp)).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                i++;
                continue;
            }
            var k = Param(t);
            if (k < 0) { outp.Add(t); continue; }
            var pasted = (i > 0 && body[i - 1] == "##") || (i + 1 < body.Count && body[i + 1] == "##");
            outp.AddRange(pasted ? Arg(k) : ExpandTokens(Arg(k), macros, hide, depth + 1));
        }
        // Token pasting.
        for (var i = 0; i < outp.Count; i++)
        {
            if (outp[i] != "##") continue;
            var left = i > 0 ? outp[i - 1] : "";
            var right = i + 1 < outp.Count ? outp[i + 1] : "";
            var joined = left + right;
            var start = Math.Max(0, i - 1);
            var count = Math.Min(outp.Count, i + 2) - start;
            outp.RemoveRange(start, count);
            outp.Insert(start, joined);
            i = start;
        }
        return outp;
    }
}
