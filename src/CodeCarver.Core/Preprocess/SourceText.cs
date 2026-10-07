using System.Text;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Undoes the compiler's first translation phases that the parsers and scanners don't know, for READING only:
/// trigraphs (<c>??&lt;</c> is <c>{</c>, <c>??=</c> is <c>#</c>, ...) and a backslash-newline that splits a NAME
/// (<c>int odd_\</c> newline <c>fn(void)</c>). Both rewrites keep every offset after them and every line break, so line
/// numbers and spans computed on the result are the original's. The emitted text is always the original.
/// </summary>
public static class SourceText
{
    /// <summary>The start of a preprocessor directive in any spelling: <c>#</c>, the digraph <c>%:</c>, the trigraph <c>??=</c>.</summary>
    public const string DirectiveStart = @"(?:#|%:|\?\?=)";

    public static string Normalize(string text) => SplitNames(Trigraphs(text));

    /// <summary>
    /// Trigraphs outside string/character literals and comments, each replaced by its character right-aligned in the
    /// same three columns (<c>??&lt;</c> becomes two spaces and <c>{</c>), so <c>??/</c> at a line end still ends the
    /// line with a backslash. In code that isn't compiled with trigraphs these sequences can't appear outside a literal
    /// or comment, so rewriting them there changes nothing real.
    /// </summary>
    public static string Trigraphs(string text)
    {
        if (!text.Contains("??", StringComparison.Ordinal)) return text;
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i++;
                continue;
            }
            if (c is '"' or '\'')
            {
                i++;
                while (i < text.Length && text[i] != c && text[i] != '\n') { if (text[i] == '\\') i++; i++; }
                continue;
            }
            if (c != '?' || i + 2 >= text.Length || text[i + 1] != '?') continue;
            var to = text[i + 2] switch
            {
                '=' => '#', '(' => '[', ')' => ']', '<' => '{', '>' => '}', '/' => '\\', '\'' => '^', '!' => '|', '-' => '~',
                _ => '\0',
            };
            if (to == '\0') continue;
            sb ??= new StringBuilder(text);
            sb[i] = ' '; sb[i + 1] = ' '; sb[i + 2] = to;
            i += 2;
        }
        return sb?.ToString() ?? text;
    }

    static bool IsName(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c > 0x7F;

    /// <summary>
    /// A backslash-newline between two name characters joins them: <c>odd_\</c> newline <c>fn</c> is <c>odd_fn</c>. The
    /// rest of the name (and any further splices inside it) moves up in front of the backslash-newline(s), which now
    /// follow the whole name: <c>odd_fn\</c> newline. Same length, same line breaks, and a continued #define stays
    /// continued.
    /// </summary>
    public static string SplitNames(string text)
    {
        if (!text.Contains("\\\n", StringComparison.Ordinal) && !text.Contains("\\\r\n", StringComparison.Ordinal)) return text;
        char[]? buf = null;
        var src = text;
        for (var i = 1; i < src.Length; i++)
        {
            var cur = buf is null ? src[i] : buf[i];
            if (cur != '\\' || !IsName(buf is null ? src[i - 1] : buf[i - 1])) continue;
            var nl = SpliceLength(buf, src, i);
            if (nl == 0) continue;
            // Gather the name's continuation: name characters, and further splices directly inside it.
            var name = new StringBuilder();
            var splices = new StringBuilder();
            var j = i;
            while (true)
            {
                var len = SpliceLength(buf, src, j);
                if (len == 0) break;
                var k = j + len;
                var start = k;
                while (k < src.Length && IsName(buf is null ? src[k] : buf[k])) k++;
                if (k == start) break;
                splices.Append(buf is null ? src.AsSpan(j, len) : buf.AsSpan(j, len));
                name.Append(buf is null ? src.AsSpan(start, k - start) : buf.AsSpan(start, k - start));
                j = k;
            }
            if (name.Length == 0) continue;
            buf ??= src.ToCharArray();
            var moved = name.ToString() + splices;
            moved.CopyTo(0, buf, i, moved.Length);
            i += moved.Length - 1;
        }
        return buf is null ? text : new string(buf);
    }

    /// <summary>Length of a backslash-newline at <paramref name="i"/> (2 for <c>\</c>LF, 3 for <c>\</c>CRLF), else 0.</summary>
    static int SpliceLength(char[]? buf, string src, int i)
    {
        char At(int k) => buf is null ? src[k] : buf[k];
        if (i >= src.Length || At(i) != '\\') return 0;
        if (i + 1 < src.Length && At(i + 1) == '\n') return 2;
        if (i + 2 < src.Length && At(i + 1) == '\r' && At(i + 2) == '\n') return 3;
        return 0;
    }

    /// <summary>The text with comments, string/char literals and preprocessor lines blanked to spaces
    /// (newlines kept), so indices map one-to-one onto the original.</summary>
    public static string CodeOnly(string text)
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
                    // A backslash-newline continues the literal, CR LF included.
                    if (text[i] == '\\' && i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n') { sb.Append("  \n"); i += 3; continue; }
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
