namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Cheap content probe: is a file a macro-dense register/header map? Such files — auto-generated chip
/// register headers, a few MB of almost-nothing-but-<c>#define</c>s, transitively <c>#include</c>d — sit
/// UNDER <c>--max-parse-bytes</c> yet drive the parser to tens of GB (one graph node per <c>#define</c>
/// plus retained AST/text; eval #14). Detecting them by content lets the CLI route them to the same
/// keep-whole path as oversized files, with no tuned byte cap.
///
/// The rule: over a bounded prefix, count lines that are a <c>#define</c> directive vs. all substantive
/// (non-blank, non-comment) lines; if the file is overwhelmingly <c>#define</c>s it's a register map.
/// Biased to NOT flag: a normal large <c>.c</c> (mostly code) fails the ratio and is parsed as before.
/// </summary>
public static class MacroDensity
{
    /// <summary>Files smaller than this are never sampled — the explosion only bites on large headers, and
    /// this keeps the probe off the vast majority of ordinary files. The CLI uses it as the size gate.</summary>
    public const long MinBytesToSample = 1_000_000;

    /// <summary>Bytes of a file to read for the density decision — bounded so the probe is negligible even
    /// on a multi-GB header.</summary>
    public const int PrefixChars = 256 * 1024;

    private const int MinNonBlank = 50;    // ignore small files that happen to be all-defines
    private const double MinRatio = 0.60;  // >= 60% of substantive lines are #define -> register map

    /// <summary>
    /// True if the file at <paramref name="path"/> reads as a macro-dense header. Reads only the first
    /// <see cref="PrefixChars"/> characters. Any I/O or decode error yields <c>false</c> (bias to parsing —
    /// the sound default keeps the file whole either way once it's over the size cap).
    /// </summary>
    public static bool IsMacroDenseHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buf = new char[PrefixChars];
            int read = sr.ReadBlock(buf, 0, buf.Length);
            if (read <= 0) return false;
            // If we filled the buffer the file was longer than the prefix, so the last line is (probably)
            // truncated — drop it so a cut-off `#defin` can't tip the count either way.
            return IsDense(buf.AsSpan(0, read), prefixTruncated: read == buf.Length);
        }
        catch { return false; }
    }

    /// <summary>
    /// The density decision over an in-memory prefix. Exposed for direct unit testing of the boundary
    /// conditions (ratio threshold, minimum line floor, comment handling, CRLF, <c>#  define</c> spacing,
    /// truncated final line). <paramref name="prefixTruncated"/> drops the last (partial) line.
    /// </summary>
    public static bool IsDense(ReadOnlySpan<char> text, bool prefixTruncated)
    {
        int end = text.Length;
        if (prefixTruncated)
        {
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline >= 0) end = lastNewline + 1;
        }

        long nonBlank = 0, defines = 0;
        int i = 0;
        while (i < end)
        {
            int lineStart = i;
            while (i < end && text[i] != '\n') i++;
            int lineEnd = i;      // exclusive
            if (i < end) i++;     // step past '\n'

            int j = lineStart;
            while (j < lineEnd && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r')) j++;
            if (j >= lineEnd) continue;   // blank line
            // pure comment lines (`// …`, `/* …`) are not substantive
            if (text[j] == '/' && j + 1 < lineEnd && (text[j + 1] == '/' || text[j + 1] == '*')) continue;
            nonBlank++;
            if (IsDefineDirective(text, j, lineEnd)) defines++;
        }
        if (nonBlank < MinNonBlank) return false;
        return (double)defines / nonBlank >= MinRatio;
    }

    /// <summary>Matches <c>#</c> then optional spaces/tabs then <c>define</c> as a whole word — so
    /// <c>#define</c>, <c>#  define</c> and <c>#\tdefine</c> all count, but <c>#defined</c> does not.</summary>
    private static bool IsDefineDirective(ReadOnlySpan<char> t, int start, int endExcl)
    {
        if (start >= endExcl || t[start] != '#') return false;
        int i = start + 1;
        while (i < endExcl && (t[i] == ' ' || t[i] == '\t')) i++;
        const string word = "define";
        if (i + word.Length > endExcl) return false;
        for (int k = 0; k < word.Length; k++) if (t[i + k] != word[k]) return false;
        int after = i + word.Length;
        // whole word: end of line or a non-identifier char follows (so `#defined` is NOT a define directive)
        return after >= endExcl || !(char.IsAsciiLetterOrDigit(t[after]) || t[after] == '_');
    }
}
