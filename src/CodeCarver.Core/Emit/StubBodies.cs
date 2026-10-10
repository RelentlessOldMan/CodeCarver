using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;

namespace CodeCarver.Core.Emit;

/// <summary>
/// Function bodies replaced by a stub (<c>stubUnexecuted</c>): a C function a run trace says never executed keeps its
/// name and signature, so every caller, table and pointer still compiles and links, but its body becomes an endless
/// loop. What only that body used then falls out of the carve. Only as safe as the trace is complete.
///
/// A body is stubbed only when it is found cleanly: the name and its parameter list on the definition's lines, one
/// brace-balanced body ending on its last line, and every <c>#if</c> inside it closed inside it. Anything else is
/// written whole, and the plan follows its uses as usual (the plan and the emitter ask the same question here).
/// Line count is kept, so the line-based pruning that follows still lines up.
/// </summary>
public static class StubBodies
{
    /// <summary>The marker every stub carries.</summary>
    public const string Marker = "/* CodeCarver: not executed in the run trace */";

    public readonly record struct Body(int Open, int Close, IReadOnlyList<string> Params);

    private static readonly Regex FnPointerName = new(@"\(\s*\*\s*(?:const\s+)?([A-Za-z_]\w*)\s*\)", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);
    private static readonly Regex Conditional = new(@"^[ \t]*#[ \t]*(if|ifdef|ifndef|endif)\b", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>One file read for stubbing: its text, the text with comments, strings and directives blanked, and where
    /// each line starts (shared across the file's functions).</summary>
    public sealed class Source
    {
        public readonly string Text, Code;
        readonly List<int> _starts = new() { 0 };
        public Source(string text)
        {
            Text = text;
            Code = SourceText.CodeOnly(text);
            for (var i = 0; i < Code.Length; i++) if (Code[i] == '\n') _starts.Add(i + 1);
        }
        /// <summary>Offset of 1-based <paramref name="line"/>, the text's length past the end, -1 before the start.</summary>
        public int LineStart(int line) => line < 1 ? -1 : line <= _starts.Count ? _starts[line - 1] : Code.Length;
        public int LineOf(int offset) { var i = _starts.BinarySearch(offset); return (i >= 0 ? i : ~i - 1) + 1; }
    }

    /// <summary>The body of function <paramref name="name"/> defined on 1-based lines <paramref name="startLine"/>..
    /// <paramref name="endLine"/>, or null when it can't be found cleanly.</summary>
    public static Body? Find(Source src, int startLine, int endLine, string name)
    {
        if (startLine < 1 || endLine < startLine) return null;
        var text = src.Text;
        var code = src.Code;
        var from = src.LineStart(startLine);
        var to = src.LineStart(endLine + 1);
        if (from >= code.Length) return null;

        // The name, then its parameter list.
        var open = -1;
        for (var at = code.IndexOf(name, from, to - from, StringComparison.Ordinal); at >= 0;
             at = at + 1 < to ? code.IndexOf(name, at + 1, to - at - 1, StringComparison.Ordinal) : -1)
        {
            var end = at + name.Length;
            if (at > 0 && IsWord(code[at - 1]) || end < code.Length && IsWord(code[end])) continue;
            while (end < to && char.IsWhiteSpace(code[end])) end++;
            if (end < to && code[end] == '(') { open = end; break; }
        }
        if (open < 0) return null;
        var close = Match(code, open, '(', ')', to);
        if (close < 0) return null;

        // The body: the first '{' after the parameters (K&R declarations may sit between), balanced, ending on the last line.
        var bo = code.IndexOf('{', close, to - close);
        if (bo < 0) return null;
        var bc = Match(code, bo, '{', '}', to);
        if (bc < 0 || src.LineOf(bc) != endLine) return null;   // the definition's own body ends on its last line

        // Every #if inside the body closes inside it.
        var depth = 0;
        foreach (Match m in Conditional.Matches(text[(bo + 1)..bc]))
        {
            depth += m.Groups[1].Value == "endif" ? -1 : 1;
            if (depth < 0) return null;
        }
        if (depth != 0) return null;

        var knr = code[(close + 1)..bo].Contains(';');
        return new Body(bo, bc, Params(code[(open + 1)..close], knr));
    }

    /// <summary>The text with each body replaced by the stub, newlines kept.</summary>
    public static string Apply(string text, IEnumerable<Body> bodies)
    {
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var b in bodies.OrderBy(b => b.Open))
        {
            if (b.Open < at) continue;   // overlapping: the outer one already went
            sb.Append(text, at, b.Open + 1 - at);
            sb.Append(' ');
            foreach (var p in b.Params) sb.Append("(void)").Append(p).Append("; ");
            sb.Append("for (;;) { } ").Append(Marker);
            for (var k = b.Open + 1; k < b.Close; k++)
                if (text[k] == '\n') sb.Append(k > 0 && text[k - 1] == '\r' ? "\r\n" : "\n");
            sb.Append(' ');
            at = b.Close;
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    /// <summary>The parameter names, to mark them used (<c>(void)p;</c>). An unnamed parameter (<c>int</c>,
    /// <c>size_t</c>) has none; a single word is a name only in a K&amp;R head, whose types follow it.</summary>
    static List<string> Params(string list, bool knr)
    {
        var names = new List<string>();
        foreach (var raw in SplitTop(list))
        {
            var seg = raw.Trim();
            if (seg.Length == 0 || seg == "void" || seg == "...") continue;
            var fp = FnPointerName.Match(seg);
            if (fp.Success) { names.Add(fp.Groups[1].Value); continue; }
            var bracket = seg.IndexOf('[');
            if (bracket >= 0) seg = seg[..bracket];
            var ids = Ident.Matches(seg).Select(m => m.Value).ToList();
            if (ids.Count >= 2 || (knr && ids.Count == 1)) names.Add(ids[^1]);
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    static IEnumerable<string> SplitTop(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '[') depth++;
            else if (s[i] is ')' or ']') depth--;
            else if (s[i] == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    static int Match(string code, int open, char o, char c, int limit)
    {
        var d = 0;
        for (var k = open; k < limit; k++)
        {
            if (code[k] == o) d++;
            else if (code[k] == c && --d == 0) return k;
        }
        return -1;
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
