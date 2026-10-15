using CodeCarver.Core.Util;

namespace CodeCarver.Frontend;

/// <summary>
/// The <c>#define</c> lines of a file, found by hand in one pass. Every file goes through this in the macro pre-pass:
/// the regexes it replaces built a Match and its groups for each define (~600 bytes, gigabytes over a macro-heavy
/// tree, which the GC then kept committed). Each list is exactly what its regex's Matches gave (see the tests).
/// </summary>
internal static class DefineLines
{
    /// <summary>
    /// <para><paramref name="objects"/>: <c>^[ \t]*#[ \t]*define[ \t]+(NAME)[ \t]+((?:\\\r?\n|[^\r\n])*)</c>: an
    /// object-like define and its body, continuation lines included.</para>
    /// <para><paramref name="valueless"/>: <c>^[ \t]*#[ \t]*define[ \t]+(NAME)[ \t]*\r?$</c>.</para>
    /// <para><paramref name="fnLike"/>: <c>^[ \t]*#[ \t]*define[ \t]+(NAME)\(</c>.</para>
    /// <para><paramref name="any"/> (null to skip): <c>^[ \t]*#[ \t]*define[ \t]+(NAME)(\()?((?:[^\n\\]|\\\r?\n|\\.)*)</c>.</para>
    /// NAME is <c>[A-Za-z_]\w*</c>; <c>^</c> and <c>$</c> are line starts and ends, as in RegexOptions.Multiline.
    /// </summary>
    public static void Scan(string text, List<(string Name, string Body)> objects, List<string> valueless, List<string> fnLike,
                            List<(string Name, bool FnLike, string Body)>? any)
    {
        var n = text.Length;
        // A match of the object or any-define regex takes its continuation lines with it: the next match is searched
        // for after it, so a define on one of those lines isn't one.
        int objectsFrom = 0, anyFrom = 0;
        for (var line = 0; line < n; line = NextLine(text, line))
        {
            var i = line;
            while (i < n && text[i] is ' ' or '\t') i++;
            if (i >= n || text[i] != '#') continue;
            i++;
            while (i < n && text[i] is ' ' or '\t') i++;
            if (string.CompareOrdinal(text, i, "define", 0, 6) != 0) continue;
            i += 6;
            var spaces = i;
            while (i < n && text[i] is ' ' or '\t') i++;
            if (i == spaces || i >= n || !(char.IsAsciiLetter(text[i]) || text[i] == '_')) continue;
            var nameStart = i++;
            while (i < n && CodeTokens.IsWordChar(text[i])) i++;
            var name = text.Substring(nameStart, i - nameStart);
            var after = i;

            if (after < n && text[after] == '(') fnLike.Add(name);

            var v = after;
            while (v < n && text[v] is ' ' or '\t') v++;
            if (v < n && text[v] == '\r') v++;
            if (v == n || text[v] == '\n') valueless.Add(name);

            if (line >= objectsFrom && after < n && text[after] is ' ' or '\t')
            {
                var b = after;
                while (b < n && text[b] is ' ' or '\t') b++;
                var e = ObjectBodyEnd(text, b);
                objects.Add((name, Flatten(text, b, e)));
                objectsFrom = e;
            }

            if (any is not null && line >= anyFrom)
            {
                var paren = after < n && text[after] == '(';
                var b = paren ? after + 1 : after;
                var e = AnyBodyEnd(text, b);
                any.Add((name, paren, text.Substring(b, e - b)));
                anyFrom = e;
            }
        }
    }

    private static int NextLine(string text, int from)
    {
        var nl = text.IndexOf('\n', from);
        return nl < 0 ? text.Length : nl + 1;
    }

    // `(?:\\\r?\n|[^\r\n])*`
    private static int ObjectBodyEnd(string text, int i)
    {
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < n && text[i + 1] == '\n') { i += 2; continue; }
            if (c == '\\' && i + 2 < n && text[i + 1] == '\r' && text[i + 2] == '\n') { i += 3; continue; }
            if (c is '\r' or '\n') break;
            i++;
        }
        return i;
    }

    // `(?:[^\n\\]|\\\r?\n|\\.)*`
    private static int AnyBodyEnd(string text, int i)
    {
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '\n') break;
            if (c != '\\') { i++; continue; }
            if (i + 1 < n && text[i + 1] == '\n') { i += 2; continue; }
            if (i + 2 < n && text[i + 1] == '\r' && text[i + 2] == '\n') { i += 3; continue; }
            if (i + 1 < n) { i += 2; continue; }   // `\\.`: '.' is anything but '\n', which was taken above
            break;
        }
        return i;
    }

    // The body on one line: each continuation (`\` and its line break) and every other line break a space, trimmed.
    private static string Flatten(string text, int start, int end)
    {
        var body = text.AsSpan(start, end - start);
        if (body.IndexOfAny('\r', '\n') < 0) return body.Trim().ToString();
        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length && body[i + 1] == '\n') { sb.Append(' '); i++; }
            else if (c == '\\' && i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n') { sb.Append(' '); i += 2; }
            else sb.Append(c is '\r' or '\n' ? ' ' : c);
        }
        return sb.ToString().Trim();
    }
}
