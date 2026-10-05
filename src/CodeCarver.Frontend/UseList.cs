using System.Collections;
using CodeCarver.Core.Graph;

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
    readonly Dictionary<string, string> _names;

    public UseList(Dictionary<string, string> namePool) => _names = namePool;

    public void Add((NodeId From, string Name) use) => Add(use.From, use.Name);

    public void Add(NodeId from, string name)
    {
        if (!_names.TryGetValue(name, out var pooled)) _names[name] = pooled = name;
        if (_seen.Add((from.Value, pooled))) _items.Add((from, pooled));
    }

    public int Count => _items.Count;
    public (NodeId From, string Name) this[int index] => _items[index];
    public IEnumerator<(NodeId From, string Name)> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
