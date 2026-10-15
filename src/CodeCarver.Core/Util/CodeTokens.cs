namespace CodeCarver.Core.Util;

/// <summary>
/// The tokens of a piece of code, scanned by hand. Whole files and every macro body go through these: the regexes they
/// replace allocated hundreds of bytes a token (gigabytes on a big tree, which the GC then kept committed). These
/// allocate the token strings only, and those once per distinct name in a file.
/// </summary>
public static class CodeTokens
{
    /// <summary>
    /// Each token's position and text, in order, as the regex <c>[A-Za-z_]\w*|\d\w*|\S</c> finds them: a name, a
    /// number, or any other single non-space character.
    /// </summary>
    public static List<(int Pos, string S)> Scan(string code)
    {
        var toks = new List<(int Pos, string S)>();
        var pool = new NamePool();
        var n = code.Length;
        for (var i = 0; i < n;)
        {
            var c = code[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i++;
            if (char.IsAsciiLetter(c) || c == '_' || char.IsDigit(c))
                while (i < n && IsWordChar(code[i])) i++;
            toks.Add((start, pool.Get(code, start, i)));
        }
        return toks;
    }

    /// <summary>
    /// A macro body's tokens, as the regex <c>##|[A-Za-z_]\w*|\S</c> finds them: a paste, a name, or any other single
    /// non-space character (a number's digits too, each on its own).
    /// </summary>
    public static List<string> MacroTokens(string code)
    {
        var toks = new List<string>();
        var n = code.Length;
        for (var i = 0; i < n;)
        {
            var c = code[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i++;
            if (c == '#' && i < n && code[i] == '#') { toks.Add("##"); i++; continue; }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < n && IsWordChar(code[i])) i++;
                toks.Add(i - start == 1 ? NamePool.Char(c) : code.Substring(start, i - start));
                continue;
            }
            toks.Add(NamePool.Char(c));
        }
        return toks;
    }

    /// <summary>
    /// Where each name of <c>[A-Za-z_]\w*</c> starts and ends, as the regex finds them: one may start right after a
    /// digit (<c>0x10u</c> holds the name <c>x10u</c>).
    /// </summary>
    public static IEnumerable<(int Start, int End)> Names(string code)
    {
        var n = code.Length;
        for (var i = 0; i < n;)
        {
            var c = code[i];
            if (!(char.IsAsciiLetter(c) || c == '_')) { i++; continue; }
            var start = i++;
            while (i < n && IsWordChar(code[i])) i++;
            yield return (start, i);
        }
    }

    /// <summary>The regex <c>\w</c>: a letter, a non-spacing mark, a decimal digit or a connector (such as '_').</summary>
    public static bool IsWordChar(char c)
    {
        if (c < 128) return char.IsAsciiLetterOrDigit(c) || c == '_';
        switch (char.GetUnicodeCategory(c))
        {
            case System.Globalization.UnicodeCategory.UppercaseLetter:
            case System.Globalization.UnicodeCategory.LowercaseLetter:
            case System.Globalization.UnicodeCategory.TitlecaseLetter:
            case System.Globalization.UnicodeCategory.ModifierLetter:
            case System.Globalization.UnicodeCategory.OtherLetter:
            case System.Globalization.UnicodeCategory.NonSpacingMark:
            case System.Globalization.UnicodeCategory.DecimalDigitNumber:
            case System.Globalization.UnicodeCategory.ConnectorPunctuation:
                return true;
            default:
                return false;
        }
    }
}
