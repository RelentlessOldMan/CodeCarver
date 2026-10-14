namespace CodeCarver.Core.Util;

/// <summary>
/// One string per distinct name: a tokenizer hands back the same instance each time a name repeats, where taking a
/// substring per token made a new one every time (a file of 1 MB is some 250,000 tokens, and a few thousand names).
/// Not thread-safe: one pool per tokenizing call.
/// </summary>
public sealed class NamePool
{
    string?[] _slots = new string?[256];
    int _count;

    /// <summary>Single characters as strings, shared by every pool.</summary>
    static readonly string[] s_chars = Enumerable.Range(0, 128).Select(c => ((char)c).ToString()).ToArray();

    /// <summary>The string for one character, made once for ASCII.</summary>
    public static string Char(char c) => c < 128 ? s_chars[c] : c.ToString();

    /// <summary>The pooled string equal to <paramref name="s"/>[<paramref name="start"/>..<paramref name="end"/>].</summary>
    public string Get(string s, int start, int end)
    {
        var span = s.AsSpan(start, end - start);
        if (span.Length == 1) return Char(span[0]);
        var mask = _slots.Length - 1;
        var i = string.GetHashCode(span) & mask;
        while (_slots[i] is { } have)
        {
            if (span.SequenceEqual(have)) return have;
            i = (i + 1) & mask;
        }
        var made = span.ToString();
        _slots[i] = made;
        if (++_count * 2 > _slots.Length) Grow();
        return made;
    }

    void Grow()
    {
        var old = _slots;
        _slots = new string?[old.Length * 2];
        var mask = _slots.Length - 1;
        foreach (var s in old)
        {
            if (s is null) continue;
            var i = string.GetHashCode(s.AsSpan()) & mask;
            while (_slots[i] is not null) i = (i + 1) & mask;
            _slots[i] = s;
        }
    }
}
