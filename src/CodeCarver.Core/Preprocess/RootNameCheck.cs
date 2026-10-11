namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The entry-point check before the graph is built. A root that resolves to nothing fails the run, but that check needs
/// the graph, the run's most expensive part: a list of guessed names cost a whole-tree carve 5 h 39 min of nothing
/// (work eval, 1.0.200). A name that occurs nowhere in the text the build reads, and that no <c>##</c> paste could
/// assemble from words that do occur, can't resolve, so it fails here in seconds. Anything else waits for the exact
/// check after the graph.
/// </summary>
public sealed class RootNameCheck
{
    readonly Dictionary<string, string> _bareOf = new(StringComparer.Ordinal);   // requested -> the name to look for
    readonly HashSet<string> _pending = new(StringComparer.Ordinal);            // bare names not seen yet
    readonly Dictionary<int, List<string>> _pieces = new();                      // hash -> substrings of pending names
    readonly HashSet<string> _seenPieces = new(StringComparer.Ordinal);
    readonly int _maxLen;
    bool _sawPaste;

    public RootNameCheck(IEnumerable<string> roots)
    {
        foreach (var r in roots)
        {
            var bare = Bare(r);
            if (bare.Length == 0) continue;
            _bareOf[r] = bare;
            if (_pending.Add(bare)) _maxLen = Math.Max(_maxLen, bare.Length);
        }
        foreach (var n in _pending)
            for (var i = 0; i < n.Length; i++)
                for (var len = 1; i + len <= n.Length; len++)
                {
                    var s = n.Substring(i, len);
                    var h = Hash(s.AsSpan());
                    if (!_pieces.TryGetValue(h, out var l)) _pieces[h] = l = new List<string>();
                    if (!l.Contains(s)) l.Add(s);
                }
    }

    /// <summary>The symbol a root names: <c>file.c:fn</c> and <c>ns::fn</c> look for <c>fn</c>.</summary>
    static string Bare(string root)
    {
        var s = root.Trim();
        var colon = s.LastIndexOf(':');
        if (colon >= 0) s = s[(colon + 1)..];
        return s;
    }

    /// <summary>True while some requested name is still unaccounted for.</summary>
    public bool AnyPending { get { lock (_pending) return _pending.Count > 0; } }

    /// <summary>Reads one text (a source file, a build log): its identifiers, and whether it pastes. Safe to call from
    /// several threads at once.</summary>
    public void Add(string text)
    {
        if (!AnyPending) return;
        var paste = text.Contains("##", StringComparison.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var span = text.AsSpan();
        for (var i = 0; i < span.Length;)
        {
            var c = span[i];
            if (!(char.IsAsciiLetter(c) || c == '_' || c == '$' || char.IsAsciiDigit(c))) { i++; continue; }
            var start = i;
            while (i < span.Length && (char.IsAsciiLetterOrDigit(span[i]) || span[i] is '_' or '$')) i++;
            if (char.IsAsciiDigit(c)) continue;
            var word = span[start..i];
            Look(word);
            // A command line's -Dname / -Uname.
            if (start > 0 && span[start - 1] == '-' && word.Length > 1 && word[0] is 'D' or 'U') Look(word[1..]);
        }
        void Look(ReadOnlySpan<char> word)
        {
            if (word.Length > _maxLen || !_pieces.TryGetValue(Hash(word), out var cands)) return;
            foreach (var p in cands)
                if (word.SequenceEqual(p)) { seen.Add(p); break; }
        }
        lock (_pending)
        {
            _sawPaste |= paste;
            foreach (var p in seen) { _seenPieces.Add(p); _pending.Remove(p); }
        }
    }

    /// <summary>The requested names that can't resolve: never seen as a word, and not assembled by a paste from two or
    /// three words that were seen.</summary>
    public IReadOnlyList<string> Missing() =>
        _bareOf.Where(kv => _pending.Contains(kv.Value) && !(_sawPaste && Assembled(kv.Value, 3))).Select(kv => kv.Key).ToList();

    bool Assembled(string name, int parts)
    {
        if (parts == 1) return _seenPieces.Contains(name);
        for (var cut = 1; cut < name.Length; cut++)
            if (_seenPieces.Contains(name[..cut]) && (_seenPieces.Contains(name[cut..]) || Assembled(name[cut..], parts - 1)))
                return true;
        return false;
    }

    static int Hash(ReadOnlySpan<char> s)
    {
        var h = unchecked((int)2166136261);
        foreach (var c in s) h = unchecked((h ^ c) * 16777619);
        return h;
    }
}
