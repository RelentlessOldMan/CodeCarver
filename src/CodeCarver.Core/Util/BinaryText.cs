namespace CodeCarver.Core.Util;

/// <summary>
/// The names a binary file holds: a prebuilt archive or object carries the names it defines and the ones it calls as
/// plain bytes in its symbol and string tables. Read as Latin-1 text in chunks, so a large image never sits in memory
/// whole; a word is the same run of letters, digits, <c>_</c> and <c>$</c> a source identifier is.
/// </summary>
public static class BinaryText
{
    const int ChunkBytes = 1 << 22;

    /// <summary>True if the file's first 8 KB hold a zero byte: an object, an archive, an image, not text.</summary>
    public static bool IsBinary(string path)
    {
        try
        {
            using var read = IoMeter.Start(IoMeter.Kind.Read);
            using var s = File.OpenRead(path);
            var buf = new byte[8192];
            var n = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return buf.AsSpan(0, n).IndexOf((byte)0) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Files that are text, or binaries whose bytes are not symbol tables (documents, archives, pictures: a manual
    // naming an API is not a call into it). Not opened at all, which on a tree of thousands of scripts saves the reads.
    static readonly HashSet<string> NotSymbolFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx", ".inl", ".s", ".asm", ".sx", ".inc", ".def",
        ".cmm", ".txt", ".md", ".rst", ".py", ".pl", ".sh", ".bat", ".cmd", ".ps1", ".mk", ".mak", ".cmake",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".ld", ".lds", ".html", ".htm", ".csv", ".log",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".chm", ".vsd", ".vsdx",
        ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".jar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg",
    };

    /// <summary>True for a binary that may hold symbol names: not a known text, document, archive or picture type,
    /// and <see cref="IsBinary"/>.</summary>
    public static bool MayHoldSymbols(string path) => !NotSymbolFiles.Contains(Path.GetExtension(path)) && IsBinary(path);

    public static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The file's text in chunks that never cut a word up to <paramref name="maxWord"/> long: a word cut by a
    /// chunk's end goes on to the next chunk whole, and a longer one is shortened to stay longer than that, so it can't
    /// match a name. An unreadable file yields nothing.</summary>
    public static IEnumerable<string> Chunks(string path, int maxWord)
    {
        FileStream s;
        try { s = File.OpenRead(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        using (s)
        using (IoMeter.Start(IoMeter.Kind.Read))
        {
            var buf = new byte[ChunkBytes];
            var carry = "";
            while (true)
            {
                int n;
                try { n = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
                if (n == 0) break;
                var text = carry + System.Text.Encoding.Latin1.GetString(buf, 0, n);
                var cut = text.Length;
                while (cut > 0 && IsWordChar(text[cut - 1])) cut--;
                carry = text[cut..];
                if (carry.Length > maxWord + 1) carry = carry[..(maxWord + 1)];
                yield return text[..cut];
            }
            if (carry.Length > 0) yield return carry;
        }
    }

    /// <summary>Which of <paramref name="names"/> the file holds as whole words.</summary>
    public static HashSet<string> NamesIn(string path, IReadOnlySet<string> names, int maxLen)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in Chunks(path, maxLen))
            for (var i = 0; i < text.Length;)
            {
                if (!IsWordChar(text[i])) { i++; continue; }
                var start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                if (i - start > maxLen || char.IsAsciiDigit(text[start])) continue;
                var w = text.Substring(start, i - start);
                if (names.Contains(w)) found.Add(w);
            }
        return found;
    }
}
