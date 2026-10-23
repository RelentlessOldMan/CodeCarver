namespace CodeCarver.Frontend;

/// <summary>
/// Long runs of lines that hold only number literals and commas (a generated data table) blanked for parsing, line
/// breaks and offsets kept. A 20 MB table of 1.5M literals took 95 s: tree-sitter parsed it in 3 s, then every pass
/// walked its millions of nodes again. A number names nothing, so no definition or use is lost; the emitter reads the
/// original text. Only runs of <see cref="MinRun"/> lines or more are blanked, and never a line continued from the
/// one before it (a directive's or a macro body's), so ordinary code parses as it did.
/// </summary>
internal static class NumberRuns
{
    public const int MinRun = 32;
    private const int MinChars = 64 * 1024;

    public static string Blank(string text)
    {
        if (text.Length < MinChars) return text;
        char[]? buf = null;
        var runStart = -1; var runLines = 0;
        var pos = 0; var prevContinued = false;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            var bodyEnd = end > pos && text[end - 1] == '\r' ? end - 1 : end;
            var numbers = !prevContinued && OnlyNumbers(text, pos, bodyEnd);
            if (numbers) { if (runLines++ == 0) runStart = pos; }
            else Flush(pos);
            prevContinued = bodyEnd > pos && text[bodyEnd - 1] == '\\';
            pos = nl < 0 ? text.Length : nl + 1;
        }
        Flush(text.Length);
        return buf is null ? text : new string(buf);

        void Flush(int upTo)
        {
            if (runLines >= MinRun)
            {
                buf ??= text.ToCharArray();
                for (var i = runStart; i < upTo; i++) if (buf[i] is not ('\n' or '\r')) buf[i] = ' ';
            }
            runLines = 0;
        }
    }

    /// <summary>The line holds at least one number and nothing but numbers (pp-numbers: a digit, or '.' and a digit,
    /// then digits, letters, '_', '.', '\'' and an exponent's sign), signs, commas and blanks.</summary>
    private static bool OnlyNumbers(string t, int from, int to)
    {
        var any = false;
        var i = from;
        while (i < to)
        {
            var c = t[i];
            if (c is ' ' or '\t' or ',' or '-' or '+') { i++; continue; }   // a sign of a signed table's entries
            if (!(char.IsAsciiDigit(c) || c == '.' && i + 1 < to && char.IsAsciiDigit(t[i + 1]))) return false;
            i++;
            while (i < to)
            {
                c = t[i];
                if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '\'') i++;
                else if (c is '+' or '-' && t[i - 1] is 'e' or 'E' or 'p' or 'P') i++;
                else break;
            }
            any = true;
        }
        return any;
    }
}
