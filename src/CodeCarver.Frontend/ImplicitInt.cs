using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Frontend;

/// <summary>
/// Recovers pre-C99 "implicit int" function definitions — <c>helper(int x) { ... }</c> or the K&amp;R form
/// <c>helper(a, b) int a; char *b; { ... }</c>, with no return type — which tree-sitter cannot parse as a
/// definition: the name, the parameters and the body come back as error-recovery fragments, the function never
/// becomes a graph node, and a call to it finds no definition, so its file was dropped (work eval, 1.0.159).
///
/// <see cref="Rewrite"/> inserts <c>int </c> before the name, for PARSING only. Lines are preserved (only the
/// recovered line's columns shift), so spans, the dead-line map and the emitter, which all read the original
/// text by line, are unaffected.
///
/// The match is deliberately narrow, because a false positive is worse than a miss: a macro-headed body
/// (<c>FRAMEWORK_FN(name, "doc") { ... }</c>) misread as a function named after the macro could have its body
/// carved away when the macro name is unreferenced, while a miss leaves today's behaviour (the body's calls are
/// attributed to the file, and the link check reports the gap). So a candidate must:
/// <list type="bullet">
/// <item>start a line at brace depth 0, after a <c>;</c>, a <c>}</c> or the start of the file (a return type on
///   the line above means it is already a normal definition);</item>
/// <item>have a name that is not a keyword, not all upper case, and not a function-like macro of the tree;</item>
/// <item>have a parameter list of plain declarations or bare identifiers (no literals, calls or expressions);</item>
/// <item>be followed by <c>{</c>, or (bare-identifier parameters only) by K&amp;R parameter declarations and then
///   <c>{</c>.</item>
/// </list>
/// </summary>
public static class ImplicitInt
{
    private static readonly Regex Head = new(
        @"\G[ \t]*(?:(?:static|extern|inline|__inline|register)[ \t]+)*(?<name>[A-Za-z_]\w*)[ \t]*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex ParamItem = new(@"^(?:\.\.\.|[A-Za-z_][\w\s\*\[\]]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex BareIdent = new(@"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant);
    private static readonly Regex KrDecl = new(@"^\s*[A-Za-z_][\w\s\*\[\],]*$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "switch", "do", "return", "sizeof", "case", "goto", "typedef", "struct",
        "union", "enum", "void", "int", "char", "short", "long", "float", "double", "signed", "unsigned",
        "const", "volatile", "auto", "_Alignof", "alignof", "_Static_assert", "static_assert", "asm", "__asm__",
        "__attribute__", "__declspec", "defined", "_Pragma", "__extension__",
    };

    /// <summary>The text with <c>int </c> inserted before each recovered definition's name (line count
    /// unchanged), or the same string instance when there is none. <paramref name="isFunctionLikeMacro"/>
    /// excludes names the tree defines as function-like macros.</summary>
    public static string Rewrite(string text, Func<string, bool> isFunctionLikeMacro, out int recovered)
    {
        recovered = 0;
        // Cheap pre-filter: a recovered head is a line starting with an identifier and '(' — almost every file
        // has one, so the real filter is the scan below; this only skips files with no '(' at all.
        if (text.IndexOf('(') < 0) return text;

        var code = CodeOnly(text);
        var inserts = new List<int>();
        var depth = 0;
        var prevSig = '\0';  // last non-space code character before the current position
        var lineStart = true;
        for (var i = 0; i < code.Length; i++)
        {
            if (lineStart && depth == 0 && prevSig is '\0' or ';' or '}')
            {
                var m = Head.Match(code, i);
                if (m.Success && Accept(text, code, m, isFunctionLikeMacro))
                    inserts.Add(m.Groups["name"].Index);
            }
            lineStart = false;
            var c = code[i];
            if (c == '\n') { lineStart = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            if (!char.IsWhiteSpace(c)) prevSig = c;
        }
        if (inserts.Count == 0) return text;

        recovered = inserts.Count;
        var sb = new StringBuilder(text.Length + 4 * inserts.Count);
        var last = 0;
        foreach (var at in inserts)
        {
            sb.Append(text, last, at - last).Append("int ");
            last = at;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    private static bool Accept(string text, string code, Match m, Func<string, bool> isFunctionLikeMacro)
    {
        var name = m.Groups["name"].Value;
        if (NotNames.Contains(name) || isFunctionLikeMacro(name)) return false;
        if (name.ToUpperInvariant() == name) return false;   // FOO(...) { — a macro-headed body, not K&R

        // Parameter list: up to the matching ')'. A nested '(' (function-pointer parameter, call, cast) is
        // outside the narrow shape we accept.
        var open = m.Index + m.Length - 1;
        var close = code.IndexOf(')', open + 1);
        if (close < 0) return false;
        var plist = code.AsSpan(open + 1, close - open - 1);
        if (plist.IndexOf('(') >= 0) return false;
        // CodeOnly blanked literals; a quote in the original parameter text means an argument, not a parameter.
        if (text.AsSpan(open + 1, close - open - 1).IndexOfAny("\"'") >= 0) return false;
        var items = plist.ToString().Split(',');
        var bare = true;
        foreach (var raw in items)
        {
            var item = raw.Trim();
            if (!ParamItem.IsMatch(item)) return false;
            if (!BareIdent.IsMatch(item) || item == "void") bare = false;
        }
        if (items.Length == 1 && items[0].Trim().Length == 0) bare = false;   // helper() — no K&R declarations

        // What follows the ')' up to the body's '{'.
        var brace = code.IndexOf('{', close + 1);
        if (brace < 0) return false;
        var between = code.AsSpan(close + 1, brace - close - 1);
        if (between.IndexOfAny("()=}") >= 0) return false;
        // Bare identifiers straight into a body (`portTASK_FUNCTION(prvIdleTask, pvParameters) {`) are almost always a
        // macro defined outside the tree, not K&R: real K&R code declares its parameters. Only typed (or empty)
        // parameter lists may go straight to the body.
        if (between.Trim().Length == 0) return !bare;
        if (!bare) return false;
        // K&R parameter declarations: one or more `type name, *name;` parts, nothing after the last ';'.
        var parts = between.ToString().Split(';');
        if (parts.Length < 2 || parts[^1].Trim().Length != 0) return false;
        for (var k = 0; k < parts.Length - 1; k++)
            if (!KrDecl.IsMatch(parts[k])) return false;
        return true;
    }

    private static readonly Regex KrHead = new(
        @"\b(?<name>[A-Za-z_]\w*)[ \t]*\((?<params>\s*[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*\s*)\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex KrFirst = new(
        @"^(?<base>.*?[\w\s])(?<decl>[\s\*]*\b(?<id>[A-Za-z_]\w*)(?:\s*\[[^\]]*\])*)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex KrMore = new(
        @"^(?<decl>[\s\*]*\b(?<id>[A-Za-z_]\w*)(?:\s*\[[^\]]*\])*)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// For the C++ grammar only, which (unlike the C grammar) rejects a K&amp;R parameter list: rewrites
    /// <c>f(a, b) int a; char *b; {</c> to <c>f(int a, char *b) {</c> for PARSING. The declaration text is
    /// blanked and any newline inside the old parameter list is kept, so the line count is unchanged. A parameter
    /// with no declaration is <c>int</c>, as in K&amp;R C. Run after <see cref="Rewrite"/> (which supplies an
    /// implicit <c>int</c> return type).
    /// </summary>
    public static string KAndRToPrototype(string text, out int rewritten)
    {
        rewritten = 0;
        if (text.IndexOf(';') < 0 || text.IndexOf('(') < 0) return text;
        var code = CodeOnly(text);
        var depth = new int[code.Length + 1];
        for (int i = 0, d = 0; i < code.Length; i++)
        {
            depth[i] = d;
            if (code[i] == '{') d++;
            else if (code[i] == '}') d = Math.Max(0, d - 1);
        }

        var edits = new List<(int Start, int End, string With)>();
        foreach (Match m in KrHead.Matches(code))
        {
            if (depth[m.Index] != 0 || NotNames.Contains(m.Groups["name"].Value)) continue;
            var close = m.Index + m.Length - 1;
            var brace = code.IndexOf('{', close + 1);
            if (brace < 0) continue;
            var between = code[(close + 1)..brace];
            if (between.AsSpan().IndexOfAny("()=}") >= 0) continue;
            var parts = between.Split(';');
            if (parts.Length < 2 || parts[^1].Trim().Length != 0) continue;

            // name -> its full declaration ("char *b"), from `base decl, decl, ...;` parts.
            var decls = new Dictionary<string, string>(StringComparer.Ordinal);
            var ok = true;
            foreach (var part in parts[..^1])
            {
                var items = part.Split(',');
                var first = KrFirst.Match(items[0]);
                if (!first.Success || first.Groups["base"].Value.Trim().Length == 0) { ok = false; break; }
                var baseType = first.Groups["base"].Value.Trim();
                decls[first.Groups["id"].Value] = baseType + " " + first.Groups["decl"].Value.Trim();
                foreach (var more in items.Skip(1))
                {
                    var mm = KrMore.Match(more);
                    if (!mm.Success) { ok = false; break; }
                    decls[mm.Groups["id"].Value] = baseType + " " + mm.Groups["decl"].Value.Trim();
                }
                if (!ok) break;
            }
            var names = m.Groups["params"].Value.Split(',').Select(p => p.Trim()).ToList();
            if (!ok || decls.Keys.Any(k => !names.Contains(k))) continue;   // declares something that is not a parameter

            var p = m.Groups["params"];
            var newlines = new string('\n', p.Value.Count(c => c == '\n'));
            edits.Add((p.Index, p.Index + p.Length,
                string.Join(", ", names.Select(n => decls.TryGetValue(n, out var d) ? d : "int " + n)) + newlines));
            var blank = new StringBuilder(brace - close - 1);
            foreach (var c in text.AsSpan(close + 1, brace - close - 1)) blank.Append(c == '\n' ? '\n' : ' ');
            edits.Add((close + 1, brace, blank.ToString()));
        }
        if (edits.Count == 0) return text;

        rewritten = edits.Count / 2;
        var sb = new StringBuilder(text.Length + 64);
        var last = 0;
        foreach (var (s, e, with) in edits)
        {
            sb.Append(text, last, s - last).Append(with);
            last = e;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>The text with comments, string/char literals and preprocessor lines blanked to spaces
    /// (newlines kept), so indices map one-to-one onto the original.</summary>
    internal static string CodeOnly(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var atLineStart = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (atLineStart)
            {
                var j = i;
                while (j < text.Length && text[j] is ' ' or '\t') j++;
                if (j < text.Length && text[j] == '#')
                {
                    // A directive, with its backslash continuations.
                    while (i < text.Length)
                    {
                        if (text[i] == '\n')
                        {
                            var k = i - 1;
                            if (k >= 0 && text[k] == '\r') k--;
                            if (k >= 0 && text[k] == '\\') { sb.Append('\n'); i++; continue; }
                            break;
                        }
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }
            }
            atLineStart = false;
            if (c == '\n') { sb.Append('\n'); i++; atLineStart = true; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { sb.Append("  "); i += 2; }
                continue;
            }
            if (c is '"' or '\'')
            {
                sb.Append(' ');
                i++;
                while (i < text.Length && text[i] != c && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1] == '\n' ? " \n" : "  "); i += 2; continue; }
                    sb.Append(' ');
                    i++;
                }
                if (i < text.Length && text[i] == c) { sb.Append(' '); i++; }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
