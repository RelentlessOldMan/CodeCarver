using System.Collections;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Util;

namespace CodeCarver.Frontend;

/// <summary>
/// Pending identifier uses (calls, references, macro-body names) gathered across the whole tree before they are
/// resolved to definitions (review RB2). A plain list took one entry — and one freshly allocated name string —
/// per OCCURRENCE: an identifier used 50 times in a function cost 50 entries. This keeps one entry per distinct
/// (node, name), in first-seen order (so resolution, and therefore node and edge order, stays deterministic),
/// and shares one string instance per distinct name through a pool common to all lists of a build.
/// </summary>
public sealed class UseList : IReadOnlyList<(NodeId From, string Name)>
{
    readonly List<(NodeId From, string Name)> _items = new();
    readonly HashSet<(int, string)> _seen = new();
    readonly NamePool _names;

    public UseList(NamePool namePool) => _names = namePool;

    public void Add((NodeId From, string Name) use) => Add(use.From, use.Name);

    public void Add(NodeId from, string name) => Add(from, name, 0, name.Length);

    /// <summary>A use of the name <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>]: no string
    /// is made for it when the name is already in the pool.</summary>
    public void Add(NodeId from, string text, int start, int end)
    {
        var pooled = _names.Get(text, start, end);
        if (_seen.Add((from.Value, pooled))) _items.Add((from, pooled));
    }

    /// <summary>The pooled name <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>], for a caller
    /// that needs the name before adding it: a string per occurrence was made only to be pooled (review CPU C7).</summary>
    public string Name(string text, int start, int end) => _names.Get(text, start, end);

    public int Count => _items.Count;
    public (NodeId From, string Name) this[int index] => _items[index];
    public IEnumerator<(NodeId From, string Name)> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
