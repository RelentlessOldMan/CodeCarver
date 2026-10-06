using System.Text.RegularExpressions;

namespace CodeCarver.Frontend;

/// <summary>
/// Source-free shape names for a function head, for the verify diagnosis of a definition that never became a graph
/// node (see <see cref="TreeSitterFrontEnd.DiagnoseDefinition"/>). Each name is a fixed word, so the counts can leave
/// a machine whose source must stay there.
/// </summary>
public static class DefinitionHead
{
    private static readonly Regex Tok = new(@"[A-Za-z_]\w*|\d\w*|\S", RegexOptions.CultureInvariant);
    private static readonly Regex CallShape = new(@"\b[A-Za-z_]\w*\s*\(", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> TextShapes(string text, int line, string name)
    {
        var shapes = new List<string>();
        var code = ImplicitInt.CodeOnly(text);
        var lineStart = 0;
        for (var l = 1; l < line && lineStart >= 0; l++) lineStart = code.IndexOf('\n', lineStart) is var n && n >= 0 ? n + 1 : -1;
        if (lineStart < 0) { shapes.Add("nameNotOnLine"); return shapes; }
        var lineEnd = code.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = code.Length;
        var m = new Regex(@"\b" + Regex.Escape(name) + @"\s*\(", RegexOptions.CultureInvariant).Match(code, lineStart, lineEnd - lineStart);
        if (!m.Success) { shapes.Add("nameNotOnLine"); return shapes; }

        var namePos = m.Index;
        var stmtStart = code.LastIndexOfAny(new[] { ';', '}', '{' }, Math.Max(0, namePos - 1)) + 1;
        var open = m.Index + m.Length - 1;
        var close = -1;
        for (int k = open, depth = 0; k < code.Length; k++)
        {
            if (code[k] == '(') depth++;
            else if (code[k] == ')' && --depth == 0) { close = k; break; }
            else if (code[k] is '{' or '}' or ';') break;
        }
        if (close < 0) { shapes.Add("unbalancedParameters"); return shapes; }
        var brace = code.IndexOf('{', close + 1);
        var nextBrace = code.IndexOf('}', close + 1);
        if (brace < 0 || (nextBrace >= 0 && nextBrace < brace)) { shapes.Add("noBodyAfterHead"); brace = -1; }

        // Before the name.
        var prefix = Tok.Matches(code[stmtStart..namePos]).Select(t => t.Value).ToList();
        var words = new List<string>();
        var typed = false;
        for (var k = 0; k < prefix.Count; k++)
        {
            var s = prefix[k];
            if (s == "(") { shapes.Add("macroCallBeforeName"); var d = 1; while (++k < prefix.Count && d > 0) { if (prefix[k] == "(") d++; else if (prefix[k] == ")") d--; } k--; continue; }
            if (!(char.IsLetter(s[0]) || s[0] == '_')) continue;
            if (s is "struct" or "union" or "enum") { typed = true; k++; continue; }
            if (HeadNormalizer.IsPrimitive(s)) { typed = true; continue; }
            if (HeadNormalizer.IsKnownSpecifier(s)) continue;
            words.Add(s);
        }
        if (prefix.Count == 0) shapes.Add("noReturnType");
        if (typed ? words.Count > 0 : words.Count > 1) shapes.Add("extraWordBeforeName");

        // The parameters.
        var parameters = code[(open + 1)..close];
        if (parameters.Contains("(*") || Regex.IsMatch(parameters, @"\(\s*\*")) shapes.Add("functionPointerParameter");
        if (CallShape.Matches(parameters).Count > 0 && !Regex.IsMatch(parameters, @"\)\s*\(")) shapes.Add("macroCallInParameters");

        // Between ')' and '{'.
        if (brace > 0)
        {
            var gap = code[(close + 1)..brace];
            if (gap.Contains(';')) shapes.Add("kAndRDeclarations");
            else if (gap.Trim().Length > 0) shapes.Add("wordsAfterParameters");
        }

        // Directive lines anywhere in the head (from the statement start to the body).
        var end = brace > 0 ? brace : close;
        for (var p = text.LastIndexOf('\n', Math.Max(0, stmtStart - 1)) + 1; p < end && p < text.Length;)
        {
            var e = text.IndexOf('\n', p);
            if (e < 0) e = text.Length;
            if (p >= stmtStart && text.AsSpan(p, e - p).TrimStart().StartsWith("#")) { shapes.Add("directiveInHead"); break; }
            p = e + 1;
        }
        return shapes;
    }
}
