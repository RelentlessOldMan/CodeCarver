using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Decides which C++ constructors to root (review P5). A constructor runs on every instantiation (<c>T x;</c>,
/// <c>T{}</c>, <c>new T</c>, a global instance, a base or member of another class), none of which is a traceable
/// call, so it cannot be reached by following calls. Rooting every constructor in the tree was sound but kept
/// every class alive. Every way of creating a <c>T</c> names <c>T</c> somewhere in the emitted text (the
/// declaration, a function's return or parameter type, a template argument, a typedef, a base list, a macro),
/// so a constructor is rooted only when its type name appears in text that will be emitted. That set grows as
/// the carve keeps more files, so the gate runs inside <see cref="EmitClosure"/>'s fixpoint.
///
/// A mention does not count when it cannot create an instance: the name in its own class head
/// (<c>class T</c>, <c>class T;</c>), a mention directly in <c>T</c>'s own class body (its constructor
/// declarations and copy parameters), a qualifier (<c>T::</c>) or a destructor name (<c>~T</c>). Mentions in a
/// member function body of <c>T</c> DO count (<c>static T&amp; get() { static T inst; ... }</c>).
/// </summary>
public sealed class ConstructorGate
{
    readonly Dictionary<string, List<NodeId>> _ctorsByType = new(StringComparer.Ordinal);
    readonly HashSet<string> _scanned = new(StringComparer.Ordinal);
    readonly HashSet<string> _released = new(StringComparer.Ordinal);

    public ConstructorGate(CodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            if (n.Kind == NodeKind.Type) typeNames.Add(n.Name);
        foreach (var n in graph.Nodes)
            if (n.Kind == NodeKind.Function && typeNames.Contains(n.Name))
            {
                if (!_ctorsByType.TryGetValue(n.Name, out var l)) _ctorsByType[n.Name] = l = new List<NodeId>();
                l.Add(n.Id);
            }
    }

    /// <summary>Number of constructor nodes the gate governs.</summary>
    public int ConstructorCount => _ctorsByType.Values.Sum(l => l.Count);

    /// <summary>Type names whose constructors have been released (rooted) so far.</summary>
    public IReadOnlyCollection<string> ReleasedTypes => _released;

    /// <summary>
    /// The constructors newly released by the emitted text of <paramref name="file"/>. Each file is scanned
    /// once. <paramref name="text"/> null means the file will be emitted but could not be read (too big,
    /// locked): every constructor is released, the sound fallback.
    /// </summary>
    public IEnumerable<NodeId> ReleasedBy(string file, string? text)
    {
        if (_ctorsByType.Count == 0 || !_scanned.Add(file)) return Array.Empty<NodeId>();
        IEnumerable<string> names = text is null ? _ctorsByType.Keys.ToList() : InstantiatingMentions(text, _ctorsByType.Keys.ToHashSet(StringComparer.Ordinal));
        return Release(names);
    }

    /// <summary>
    /// Like <see cref="ReleasedBy(string, string?)"/>, reading <paramref name="fullPath"/> itself. A file over
    /// <paramref name="maxTextBytes"/> (a giant generated header) is not tokenized: it is streamed and every
    /// candidate that occurs in it as a word is released, comments included — a superset, so still sound.
    /// A file that cannot be read releases everything.
    /// </summary>
    public IEnumerable<NodeId> ReleasedByFile(string file, string fullPath, long maxTextBytes)
    {
        if (_ctorsByType.Count == 0 || _scanned.Contains(file)) return Array.Empty<NodeId>();
        try
        {
            var len = new FileInfo(fullPath).Length;
            if (len <= maxTextBytes) return ReleasedBy(file, File.ReadAllText(fullPath, System.Text.Encoding.Latin1));
            _scanned.Add(file);
            var words = new List<string>();
            foreach (var line in File.ReadLines(fullPath, System.Text.Encoding.Latin1))
                foreach (System.Text.RegularExpressions.Match m in Word.Matches(line))
                    if (_ctorsByType.ContainsKey(m.Value) && !_released.Contains(m.Value)) words.Add(m.Value);
            return Release(words);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _scanned.Add(file);
            return Release(_ctorsByType.Keys.ToList());
        }
    }

    static readonly System.Text.RegularExpressions.Regex Word =
        new(@"[A-Za-z_$][A-Za-z0-9_$]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    List<NodeId> Release(IEnumerable<string> names)
    {
        var ids = new List<NodeId>();
        foreach (var name in names)
            if (_released.Add(name)) ids.AddRange(_ctorsByType[name]);
        return ids;
    }

    /// <summary>The names in <paramref name="candidates"/> that <paramref name="text"/> mentions in a way that
    /// can create an instance (see the class summary).</summary>
    public static HashSet<string> InstantiatingMentions(string text, IReadOnlySet<string> candidates)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var toks = EmittedLinkCheck.Tokenize(text);
        // Brace stack: the class whose body this brace opens, or null for any other brace.
        var braces = new Stack<string?>();
        string? pendingClass = null;   // `class NAME` head seen; the next '{' opens its body (a ';' cancels it)
        var templateDepth = 0;         // > 0 inside `template < ... >`
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            var prev = i > 0 ? toks[i - 1] : default;
            var next = i + 1 < toks.Count ? toks[i + 1] : default;
            if (!t.Ident)
            {
                if (t.InDefine) continue;   // braces in a macro body don't nest the file's scopes
                switch (t.Text)
                {
                    case "{": braces.Push(pendingClass); pendingClass = null; break;
                    case "}": if (braces.Count > 0) braces.Pop(); break;
                    case ";": pendingClass = null; break;
                    case "<": if (templateDepth > 0 || prev.Text == "template") templateDepth++; break;
                    case ">": if (templateDepth > 0) templateDepth--; break;
                }
                continue;
            }
            if (prev.Text is "class" or "struct" or "union")
            {
                // `class T {` / `class T :` / `class T final` / `class T;` / `class T<...>` name the class itself,
                // and a template parameter `template<class T>` names nothing. Anything else
                // (`struct T x;`, `struct T *p`) is an elaborated use of the type.
                if (templateDepth > 0 || next.Text is "{" or ":" or ";" or "<" or "final")
                {
                    if (!t.InDefine && next.Text is "{" or ":" or "<" or "final") pendingClass = t.Text;
                    continue;
                }
            }
            if (!candidates.Contains(t.Text)) continue;
            if (next.Text == "::" || prev.Text == "~") continue;                         // qualifier, destructor
            if (prev.Text == "::" && i >= 2 && toks[i - 2].Text == t.Text) continue;    // T::T(...) definition
            if (!t.InDefine && braces.Count > 0 && braces.Peek() == t.Text) continue;   // directly in its own class body
            found.Add(t.Text);
        }
        return found;
    }
}
