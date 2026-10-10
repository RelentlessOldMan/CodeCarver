using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Bare macro invocations at file scope: a statement that is only <c>NAME(args);</c>, no type in front. C has no such
/// declaration (it would be an implicit-int prototype), so it is a macro, and in practice a registration:
/// <c>REGISTER_INIT(on_start);</c> puts <c>on_start</c> in a table. With the macro visible its body says so; when it is
/// defined where the carve can't read (an SDK header outside the root), the parser reads a prototype whose parameter
/// types are the registered names, and the names went unused: a static function was cut from its kept file and the
/// check had to put it back (work eval, 1.0.196: <c>definitionFileLocal</c> used <c>atFileScope</c> as a
/// <c>reference</c>). Its arguments' names are uses of the file.
///
/// A K&amp;R definition head <c>name(a, b)</c> followed by <c>int a;</c> parameter declarations and the body is not
/// one: an invocation ends at <c>;</c>, or without it is followed by a statement holding <c>(</c> before its end
/// (the next prototype or definition).
/// </summary>
public static class FileScopeInvocations
{
    // Optional words first (`static REGISTER(fn);`, `MODULE_ATTR REGISTER(fn);`), then the name and its '('.
    private static readonly Regex Head = new(@"\G\s*(?<words>(?:[A-Za-z_]\w*\s+){0,4}?)(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);
    // A word that makes the statement a declaration (its return type), not a registration.
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "void", "int", "char", "short", "long", "float", "double", "signed", "unsigned", "struct", "union", "enum",
        "_Bool", "bool", "typedef", "return", "sizeof",
    };

    /// <summary>Each invocation in <paramref name="code"/> (already <see cref="SourceText.CodeOnly"/>): its 1-based line,
    /// the macro name, the identifiers in each of its arguments, and whether words came first (<c>static REGISTER(fn);</c>:
    /// shaped like a prototype too, so the caller decides by whether the name is a function or macro anywhere).</summary>
    public static IEnumerable<(int Line, string Macro, List<List<string>> Args, bool AfterWords)> Find(string code)
    {
        var results = new List<(int, string, List<List<string>>, bool)>();
        var depth = 0;
        var line = 1;
        var statementStart = true;
        for (var i = 0; i < code.Length; i++)
        {
            if (depth == 0 && statementStart && !char.IsWhiteSpace(code[i]))
            {
                statementStart = false;
                var m = Head.Match(code, i);
                var words = m.Success ? Ident.Matches(m.Groups["words"].Value).Select(x => x.Value).ToList() : new List<string>();
                if (m.Success && m.Index == i && !words.Any(TypeWords.Contains) && !TypeWords.Contains(m.Groups["name"].Value)
                    && Close(code, m.Index + m.Length - 1) is var close && close > 0 && Ends(code, close + 1, words.Count > 0))
                {
                    var args = code[(m.Index + m.Length)..close];
                    var perArg = SplitTop(args).Select(a => Ident.Matches(a).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToList()).ToList();
                    results.Add((line, m.Groups["name"].Value, perArg, words.Count > 0));
                    for (var k = i; k <= close; k++) if (code[k] == '\n') line++;
                    i = close;
                    statementStart = true;
                    continue;
                }
            }
            var c = code[i];
            if (c == '\n') line++;
            else if (c == '{') depth++;
            else if (c == '}') { if (depth > 0) depth--; if (depth == 0) statementStart = true; }
            else if (c == ';' && depth == 0) statementStart = true;
        }
        return results;
    }

    public enum ParamUse { None, Declares, References }

    /// <summary>What a macro body does with parameter <paramref name="param"/>: names something with it
    /// (<c>= fn</c>, <c>{ #fn, fn }</c>, <c>&amp;fn</c>, <c>OTHER(fn)</c>), only declares it (<c>void fn(void)</c>), or
    /// neither (absent, or only stringized or pasted: <c>#fn</c>, <c>entry_##fn</c>).</summary>
    public static ParamUse UseOf(string body, string param)
    {
        if (param.Length == 0) return ParamUse.None;
        var result = ParamUse.None;
        foreach (Match m in Regex.Matches(body, @"(?<![\w$])" + Regex.Escape(param) + @"(?![\w$])"))
        {
            var p = m.Index - 1;
            while (p >= 0 && char.IsWhiteSpace(body[p])) p--;
            var n = m.Index + m.Length;
            while (n < body.Length && char.IsWhiteSpace(body[n])) n++;
            if (p >= 0 && body[p] == '#') continue;                                  // #fn, x##fn
            if (n + 1 < body.Length && body[n] == '#' && body[n + 1] == '#') continue; // fn##x
            if (p < 0 || "=,(&{!?:+-/|^<>[".Contains(body[p])) return ParamUse.References;
            if (char.IsLetterOrDigit(body[p]) || body[p] is '_' or '*' or '$')
            {
                // A word before: a type (declares) unless it is `return fn`.
                var w = p;
                while (w >= 0 && (char.IsLetterOrDigit(body[w]) || body[w] == '_')) w--;
                if (body[(w + 1)..(p + 1)] == "return") return ParamUse.References;
                result = ParamUse.Declares;
            }
            else return ParamUse.References;
        }
        return result;
    }

    /// <summary>The parameter argument <paramref name="index"/> binds to: a named one, or <c>__VA_ARGS__</c> past the
    /// last named one of a variadic macro; "" when there is none.</summary>
    public static string ParamFor(IReadOnlyList<string> ps, int index)
    {
        var variadic = ps.Count > 0 && ps[^1] == "...";
        if (index < ps.Count - (variadic ? 1 : 0)) return ps[index];
        return variadic ? "__VA_ARGS__" : "";
    }

    /// <summary>Top-level comma-separated parts of an argument list.</summary>
    static IEnumerable<string> SplitTop(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '[' or '{') depth++;
            else if (s[i] is ')' or ']' or '}') depth--;
            else if (s[i] == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    /// <summary>The ')' closing the '(' at <paramref name="open"/>, or -1.</summary>
    static int Close(string code, int open)
    {
        var d = 0;
        for (var k = open; k < code.Length; k++)
        {
            if (code[k] == '(') d++;
            else if (code[k] == ')' && --d == 0) return k;
            else if (code[k] is '{' or '}' or ';') return -1;   // not a plain argument list
        }
        return -1;
    }

    /// <summary>After the ')': a ';', the end, or a following statement with '(' before its ';' or '{' (so not K&amp;R
    /// parameter declarations, and not a body).</summary>
    static bool Ends(string code, int at, bool afterWords)
    {
        var k = at;
        while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
        if (k >= code.Length || code[k] == ';') return true;
        if (afterWords) return false;   // `static int f(a, b) {` and friends: only `words NAME(args);` counts
        if (code[k] == '{') return false;
        var sawNewline = code.AsSpan(at, k - at).Contains('\n');
        if (!sawNewline) return false;
        for (; k < code.Length; k++)
        {
            if (code[k] == '(') return true;
            if (code[k] is ';' or '{' or '}') return false;
        }
        return false;
    }
}
