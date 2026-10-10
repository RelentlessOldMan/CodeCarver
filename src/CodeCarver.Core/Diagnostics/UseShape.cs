using CodeCarver.Core.Preprocess;

namespace CodeCarver.Core.Diagnostics;

/// <summary>
/// Where a use the graph missed sits in the emitted text, as fixed shape names, so a remote eval can say which
/// kind of use the front-end skips without sending a line of code (work eval, 1.0.195: 8 definitions kept by the
/// check, cause <c>prunedFromKeptFile</c> alone, which names what was cut but not what used it).
/// <list type="bullet">
/// <item>Place: <c>inMacroDefinition</c> (a <c>#define</c> body), <c>inFunctionBody</c>, <c>inInitializer</c>
///   (<c>= { ... }</c> or <c>= name</c> at file scope), <c>inOtherBlock</c> (a struct, enum or namespace),
///   <c>atFileScope</c>, or <c>lineNotFound</c>.</item>
/// <item>Form: <c>call</c> (followed by <c>(</c>), <c>reference</c>, or <c>nameNotOnLine</c>.</item>
/// <item><c>inConditional</c> when the line sits inside an <c>#if</c> block other than an include guard.</item>
/// </list>
/// </summary>
public static class UseShape
{
    /// <param name="macroOf">The tree's definitions of a function-like macro (parameters, body), or null when it has
    /// none: says, for a use at file scope inside a macro call, what that macro does with the name.</param>
    public static IReadOnlyList<string> Describe(string text, int line, string name,
        Func<string, IReadOnlyList<(IReadOnlyList<string> Params, string Body)>?>? macroOf = null)
    {
        var lines = text.Split('\n');
        if (line < 1 || line > lines.Length) return new[] { "lineNotFound" };
        var shapes = new List<string>();

        // A #define body, continued with backslashes: the start is the first line not continuing the one above.
        var start = line - 1;
        while (start > 0 && lines[start - 1].TrimEnd('\r').EndsWith('\\')) start--;
        var head = lines[start].TrimStart();
        var inDefine = head.StartsWith('#') && head[1..].TrimStart().StartsWith("define", StringComparison.Ordinal);

        var code = SourceText.CodeOnly(text);
        var lineStart = 0;
        for (var i = 0; i < line - 1; i++) lineStart = code.IndexOf('\n', lineStart) + 1;
        var lineEnd = text.IndexOf('\n', lineStart);
        if (inDefine) shapes.Add("inMacroDefinition");
        else
        {
            // Up to the name itself: `static const int x = sizeof(name);` is an initializer although its line starts at file scope.
            var to = lineStart;
            var rest = code[lineStart..(lineEnd < 0 ? code.Length : lineEnd)];
            for (var k = rest.IndexOf(name, StringComparison.Ordinal); k >= 0; k = rest.IndexOf(name, k + 1, StringComparison.Ordinal))
                if ((k == 0 || !IsWord(rest[k - 1])) && (k + name.Length >= rest.Length || !IsWord(rest[k + name.Length]))) { to = lineStart + k; break; }
            shapes.Add(Place(code, to));
        }

        // The form: the line's own text (raw for a #define, which CodeOnly blanks).
        var own = inDefine ? text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)] : code[lineStart..(lineEnd < 0 ? code.Length : lineEnd)];
        var form = Form(own, name);
        if (form == "nameNotOnLine" && !inDefine && Form(text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)], name) != "nameNotOnLine")
            form = "nameInStringOrComment";   // an alias("name") attribute, file-scope asm, a comment
        shapes.Add(form);
        if (InConditional(lines, line - 1)) shapes.Add("inConditional");
        if (shapes[0] == "atFileScope" && form is "reference" or "call") shapes.AddRange(Statement(code, lineStart, lineEnd < 0 ? code.Length : lineEnd, name, macroOf));
        return shapes;
    }

    /// <summary>A use at file scope: how its statement starts (<c>stmt.macroCall</c>: <c>NAME(...)</c>;
    /// <c>stmt.wordsThenCall</c>: <c>static NAME(...)</c>; <c>stmt.declaration</c>), and for the macro call holding the
    /// name, whether the tree defines it and what its definitions do with that argument.</summary>
    static IEnumerable<string> Statement(string code, int lineStart, int lineEnd, string name,
        Func<string, IReadOnlyList<(IReadOnlyList<string> Params, string Body)>?>? macroOf)
    {
        // The statement: back to the previous ';' or '}' (or the start).
        var start = lineStart;
        while (start > 0 && code[start - 1] is not (';' or '}')) start--;
        var at = -1;
        for (var k = code.IndexOf(name, lineStart, lineEnd - lineStart, StringComparison.Ordinal); k >= 0;
             k = k + 1 < lineEnd ? code.IndexOf(name, k + 1, lineEnd - k - 1, StringComparison.Ordinal) : -1)
            if ((k == 0 || !IsWord(code[k - 1])) && (k + name.Length >= code.Length || !IsWord(code[k + name.Length]))) { at = k; break; }
        if (at < 0) yield break;

        // The innermost '(' around the name, and the word before it: the macro called.
        int depth = 0, open = -1, commas = 0;
        for (var k = at - 1; k >= start; k--)
        {
            if (code[k] == ')') depth++;
            else if (code[k] == '(') { if (depth == 0) { open = k; break; } depth--; }
            else if (code[k] == ',' && depth == 0) commas++;
        }
        if (open < 0) { yield return "stmt.declaration"; yield break; }
        var e = open - 1;
        while (e >= start && char.IsWhiteSpace(code[e])) e--;
        var b = e;
        while (b >= start && IsWord(code[b])) b--;
        var macro = code[(b + 1)..(e + 1)];
        if (macro.Length == 0) { yield return "stmt.declaration"; yield break; }
        var before = code[start..(b + 1)].Trim();
        yield return before.Length == 0 ? "stmt.macroCall" : before.All(c => IsWord(c) || char.IsWhiteSpace(c)) ? "stmt.wordsThenCall" : "stmt.declaration";

        if (macroOf is null) yield break;
        var defs = macroOf(macro);
        if (defs is null || defs.Count == 0) { yield return "macro.notInTree"; yield break; }
        if (defs.Count > 1) yield return "macro.severalDefinitions";
        if (defs.Any(d => d.Body.Length == 0)) yield return "macro.emptyDefinition";
        var uses = defs.Select(d => commas < d.Params.Count && d.Params[commas] is var p && p.Length > 0
                                    && System.Text.RegularExpressions.Regex.IsMatch(d.Body, @"(?<![\w#])" + System.Text.RegularExpressions.Regex.Escape(p) + @"(?!\w)"))
                       .ToList();
        if (uses.Any(u => u)) yield return "macro.usesArg";
        if (uses.Any(u => !u)) yield return "macro.dropsArg";
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>The block the offset sits in, read from the brace nesting before it: a block opened after <c>)</c>
    /// is a function body, after <c>=</c> an initializer.</summary>
    static string Place(string code, int offset)
    {
        var openers = new Stack<char>();
        var afterEquals = false;   // a file-scope `= ...;` still open
        for (var i = 0; i < offset; i++)
        {
            var c = code[i];
            if (c == '{')
            {
                var k = i - 1;
                while (k >= 0 && char.IsWhiteSpace(code[k])) k--;
                openers.Push(k >= 0 ? code[k] : ' ');
            }
            else if (c == '}') { if (openers.Count > 0) openers.Pop(); }
            else if (openers.Count == 0 && c == '=') afterEquals = true;
            else if (openers.Count == 0 && c == ';') afterEquals = false;
        }
        foreach (var o in openers.Reverse())
        {
            if (o == ')') return "inFunctionBody";
            if (o == '=') return "inInitializer";
        }
        if (openers.Count > 0) return "inOtherBlock";
        return afterEquals ? "inInitializer" : "atFileScope";
    }

    static string Form(string own, string name)
    {
        for (var at = own.IndexOf(name, StringComparison.Ordinal); at >= 0; at = own.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            var end = at + name.Length;
            if (at > 0 && (char.IsLetterOrDigit(own[at - 1]) || own[at - 1] == '_')) continue;
            if (end < own.Length && (char.IsLetterOrDigit(own[end]) || own[end] == '_')) continue;
            while (end < own.Length && char.IsWhiteSpace(own[end])) end++;
            return end < own.Length && own[end] == '(' ? "call" : "reference";
        }
        return "nameNotOnLine";
    }

    /// <summary>Inside an <c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c> block, the include guard (an <c>#ifndef</c> whose next
    /// directive is a <c>#define</c> of the same name) not counted.</summary>
    static bool InConditional(string[] lines, int index)
    {
        var depth = 0;
        string? guard = null;
        var guardDepth = -1;
        for (var i = 0; i < index; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith('#')) continue;
            var d = t[1..].TrimStart();
            if (d.StartsWith("if", StringComparison.Ordinal))
            {
                depth++;
                if (depth == 1 && guard is null && d.StartsWith("ifndef", StringComparison.Ordinal))
                {
                    var g = d[6..].Trim();
                    for (var j = i + 1; j < index && j < lines.Length; j++)
                    {
                        var n = lines[j].Trim();
                        if (n.Length == 0) continue;
                        if (n.StartsWith('#') && n[1..].TrimStart() is var nd && nd.StartsWith("define", StringComparison.Ordinal)
                            && nd[6..].Trim().Split(' ', '\t')[0] == g) { guard = g; guardDepth = 1; }
                        break;
                    }
                }
            }
            else if (d.StartsWith("endif", StringComparison.Ordinal))
            {
                if (depth == guardDepth) guardDepth = -1;
                if (depth > 0) depth--;
            }
        }
        return depth - (guardDepth == 1 ? 1 : 0) > 0;
    }
}
