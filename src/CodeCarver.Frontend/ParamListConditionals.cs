using System.Text;

namespace CodeCarver.Frontend;

/// <summary>
/// A parameter list whose parameters differ by configuration:
/// <code>
/// int setup(int a,
/// #if defined(BIG)
///           long x, long y,
/// #else
///           short x,
/// #endif
///           int b)
/// { ... }
/// </code>
/// tree-sitter cannot parse two alternative parameter sets in one list (a lone <c>#ifdef ... #endif</c> parses), so
/// the function never became a definition and its file was dropped while a call still needed it (work eval,
/// 1.0.162: two prototypes with different parameter counts). <see cref="Blank"/> blanks every <c>#elif</c>/<c>#else</c>
/// branch that sits inside an open parenthesis at file scope, and the conditional's own directive lines (the C++
/// grammar takes none inside a parameter list), for PARSING only and line-preserving. The blanked text is returned
/// so the caller can still count its names as uses.
/// </summary>
public static class ParamListConditionals
{
    public static string Blank(string text, out string removed)
    {
        removed = "";
        if (!MayHaveOne(text)) return text;

        var code = ImplicitInt.CodeOnly(text);
        var lines = new List<(int Start, int End)>();   // [Start, End) excluding the '\n'
        for (int s = 0, i = 0; i <= code.Length; i++)
            if (i == code.Length || code[i] == '\n') { lines.Add((s, i)); s = i + 1; }

        var frames = new Stack<(bool InParams, bool Blanking)>();
        int parens = 0, braces = 0;
        StringBuilder? sb = null;
        var cut = new StringBuilder();
        bool Blanking() => frames.Any(f => f.Blanking);

        void BlankLine(int start, int end)
        {
            sb ??= new StringBuilder(text);
            cut.Append(text, start, end - start).Append('\n');
            for (var k = start; k < end; k++) if (sb[k] != '\r') sb[k] = ' ';
        }

        for (var li = 0; li < lines.Count; li++)
        {
            var (start, end) = lines[li];
            // Directives come from the original text (CodeOnly blanks them); parentheses from the code-only text.
            var trimmed = text.AsSpan(start, end - start).TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '#')
            {
                // The directive and its backslash continuations.
                var last = li;
                while (last + 1 < lines.Count && text.AsSpan(lines[last].Start, lines[last].End - lines[last].Start).TrimEnd().EndsWith("\\"))
                    last++;
                var word = Word(trimmed[1..]);
                var blankThis = false;
                if (word is "if" or "ifdef" or "ifndef")
                {
                    var inParams = parens > 0 && braces == 0;
                    blankThis = inParams || Blanking();   // the C++ grammar takes no directive inside a parameter list
                    frames.Push((inParams, false));
                }
                else if (word is "elif" or "else" && frames.Count > 0)
                {
                    var f = frames.Pop();
                    if (f.InParams) f.Blanking = true;
                    frames.Push(f);
                    blankThis = Blanking();
                }
                else if (word == "endif" && frames.Count > 0)
                {
                    blankThis = frames.Pop().InParams || Blanking();
                }
                else blankThis = Blanking();
                if (blankThis)
                    for (var k = li; k <= last; k++) BlankLine(lines[k].Start, lines[k].End);
                li = last;
                continue;
            }
            if (Blanking()) { BlankLine(start, end); continue; }
            for (var k = start; k < end; k++)
            {
                var c = code[k];
                if (c == '(') parens++;
                else if (c == ')') parens = Math.Max(0, parens - 1);
                else if (c == '{') braces++;
                else if (c == '}') braces = Math.Max(0, braces - 1);
            }
        }
        if (sb is null) return text;
        removed = cut.ToString();
        return sb.ToString();
    }

    /// <summary>Cheap pre-check, run on every file: some <c>#if</c> line follows a line whose code ends in <c>,</c> or
    /// <c>(</c> (a trailing comment, or comment-only lines between, are looked past). Without one there is no
    /// conditional inside a parameter list, and the full pass (a code-only copy of the file) is skipped.</summary>
    public static bool MayHaveOne(string text)
    {
        var at = 0;
        while ((at = text.IndexOf("#if", at, StringComparison.Ordinal)) >= 0)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            var head = text.AsSpan(lineStart, at - lineStart);
            at += 3;
            if (head.Trim().Length != 0 || lineStart == 0) continue;   // not a directive line, or nothing above
            var end = lineStart - 1;                                       // the '\n' ending the line above
            for (var guard = 0; guard < 8 && end > 0; guard++)
            {
                var start = text.LastIndexOf('\n', end - 1) + 1;
                var line = text.AsSpan(start, end - start).Trim();
                var slashes = line.IndexOf("//");
                if (slashes >= 0) line = line[..slashes].TrimEnd();
                if (line.EndsWith("*/"))
                {
                    var open = line.LastIndexOf("/*");
                    line = open >= 0 ? line[..open].TrimEnd() : ReadOnlySpan<char>.Empty;
                }
                if (line.Length > 0)
                {
                    if (line[^1] is ',' or '(') return true;
                    break;
                }
                end = start - 1;                                           // blank or comment-only: look further up
            }
        }
        return false;
    }

    private static string Word(ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        var n = 0;
        while (n < s.Length && char.IsLetter(s[n])) n++;
        return s[..n].ToString();
    }
}
