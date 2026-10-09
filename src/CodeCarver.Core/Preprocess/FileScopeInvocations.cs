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
    private static readonly Regex Head = new(@"\G\s*([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>Each invocation in <paramref name="code"/> (already <see cref="SourceText.CodeOnly"/>): its 1-based line,
    /// the macro name, and the identifiers in its arguments.</summary>
    public static IEnumerable<(int Line, string Macro, List<string> Names)> Find(string code)
    {
        var results = new List<(int, string, List<string>)>();
        var depth = 0;
        var line = 1;
        var statementStart = true;
        for (var i = 0; i < code.Length; i++)
        {
            if (depth == 0 && statementStart && !char.IsWhiteSpace(code[i]))
            {
                statementStart = false;
                var m = Head.Match(code, i);
                if (m.Success && m.Index == i && Close(code, m.Index + m.Length - 1) is var close && close > 0 && Ends(code, close + 1))
                {
                    var args = code[(m.Index + m.Length)..close];
                    var names = Ident.Matches(args).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToList();
                    results.Add((line, m.Groups[1].Value, names));
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
    static bool Ends(string code, int at)
    {
        var k = at;
        while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
        if (k >= code.Length || code[k] == ';') return true;
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
