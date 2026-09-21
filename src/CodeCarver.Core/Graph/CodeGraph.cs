namespace CodeCarver.Core.Graph;

/// <summary>
/// The dependency graph a carve is computed over. Nodes are interned by (kind, name, file, line) so a
/// definition referenced from many places collapses to one id, while two same-named definitions in one
/// file (e.g. C++ methods Circle::area and Square::area) stay distinct. Ids are handed out in insertion
/// order, which makes every downstream result deterministic.
///
/// This is an in-memory model. The scale story (memory-mapping for 90 GB repos) lives behind the
/// same surface later; the reachability engine only ever touches <see cref="Nodes"/> and
/// <see cref="OutEdges"/>, so the backing store can change without touching the algorithm.
/// </summary>
public sealed class CodeGraph
{
    private readonly List<Node> _nodes = new();
    private readonly List<List<Edge>> _out = new();
    private readonly Dictionary<string, int> _intern = new(StringComparer.Ordinal);

    public int NodeCount => _nodes.Count;
    public IEnumerable<Node> Nodes => _nodes;

    /// <summary>Interns a node by identity (kind+name+file+line). Repeated calls with the same identity
    /// return the same id and merge in any newly-observed flags.</summary>
    public NodeId GetOrAddNode(NodeKind kind, string name, string? file = null,
                               SourceSpan span = default, NodeFlags flags = NodeFlags.None)
    {
        var key = Key(kind, name, file, span.StartLine);
        if (_intern.TryGetValue(key, out var existing))
        {
            var cur = _nodes[existing];
            var mergedFlags = cur.Flags | flags;
            var mergedSpan = cur.Span.IsKnown ? cur.Span : span;
            if (mergedFlags != cur.Flags || !mergedSpan.Equals(cur.Span))
                _nodes[existing] = cur with { Flags = mergedFlags, Span = mergedSpan };
            return new NodeId(existing);
        }

        var id = _nodes.Count;
        _nodes.Add(new Node { Id = new NodeId(id), Kind = kind, Name = name, File = file, Span = span, Flags = flags });
        _out.Add(new List<Edge>());
        _intern[key] = id;
        return new NodeId(id);
    }

    public void AddEdge(NodeId from, NodeId to, EdgeKind kind)
    {
        Validate(from);
        Validate(to);
        _out[from.Value].Add(new Edge(from, to, kind));
    }

    /// <summary>Merge additional flags onto an existing node (e.g. mark a function address-taken once a
    /// front-end discovers its address is used somewhere).</summary>
    public void AddFlag(NodeId id, NodeFlags flags)
    {
        Validate(id);
        var n = _nodes[id.Value];
        if ((n.Flags & flags) != flags)
            _nodes[id.Value] = n with { Flags = n.Flags | flags };
    }

    public Node GetNode(NodeId id)
    {
        Validate(id);
        return _nodes[id.Value];
    }

    public IReadOnlyList<Edge> OutEdges(NodeId id)
    {
        Validate(id);
        return _out[id.Value];
    }

    /// <summary>All distinct file paths any node lives in, sorted — the universe a carve prunes from.</summary>
    public IReadOnlyList<string> AllFiles()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in _nodes)
            if (n.FilePath is { } f)
                set.Add(f);
        return set.ToList();
    }

    private void Validate(NodeId id)
    {
        if (!id.IsValid || id.Value >= _nodes.Count)
            throw new ArgumentOutOfRangeException(nameof(id), $"node {id} is not in this graph");
    }

    private static string Key(NodeKind kind, string name, string? file, int line)
        => $"{(int)kind} {name} {file} {line}";
}
