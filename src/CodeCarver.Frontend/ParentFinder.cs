using TreeSitter;
using TsNode = TreeSitter.Node;

namespace CodeCarver.Frontend;

/// <summary>
/// A node's parent, found the way tree-sitter finds it (down from the root to the child that holds the node) but with
/// a binary search at a node with many children. tree-sitter's own <c>Node.Parent</c> scans the children at each level,
/// and in a file with parse errors that scan is slow: one step up cost ~120 us in a 4 MB file and grew with the file,
/// so a big file's passes, which step up from nearly every capture, took the square of its size.
/// The answer is always the node tree-sitter would return: where the search is not certain (a zero-width node on a
/// boundary) it asks tree-sitter.
/// </summary>
internal sealed class ParentFinder
{
    /// <summary>A node with at least this many children is searched by binary search; below it tree-sitter's scan is cheap.</summary>
    public const int WideChildren = 32;

    private sealed class Kids
    {
        public required TsNode[] Nodes;
        public required int[] Starts;
        public required int[] Ends;
    }

    private readonly TsNode _root;
    private readonly Dictionary<IntPtr, Kids?> _kids = new();

    public ParentFinder(TsNode root) => _root = root;

    public TsNode? ParentOf(TsNode n)
    {
        if (n.Id == _root.Id) return null;
        var cur = _root;
        for (var depth = 0; depth < 4096; depth++)
        {
            var child = ChildHolding(cur, n);
            if (child is null) break;
            if (child.Id == n.Id) return cur;
            cur = child;
        }
        return n.Parent;   // not found by the search (not expected): tree-sitter decides
    }

    private TsNode? ChildHolding(TsNode cur, TsNode n)
    {
        if (!_kids.TryGetValue(cur.Id, out var kids))
        {
            var list = NodeKids.All(cur);
            if (list.Count >= WideChildren)
            {
                var nodes = list.ToArray();
                kids = new Kids
                {
                    Nodes = nodes,
                    Starts = nodes.Select(k => k.StartIndex).ToArray(),
                    Ends = nodes.Select(k => k.EndIndex).ToArray(),
                };
            }
            _kids[cur.Id] = kids;
        }
        if (kids is null) return cur.GetChildWithDescendant(n);

        // The last child starting at or before the node, and any before it that also hold its range: children do not
        // overlap, so there is one, except for a zero-width child at a boundary.
        int start = n.StartIndex, end = n.EndIndex;
        int lo = 0, hi = kids.Starts.Length - 1, at = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (kids.Starts[mid] <= start) { at = mid; lo = mid + 1; } else hi = mid - 1;
        }
        TsNode? only = null;
        for (var i = at; i >= 0 && kids.Ends[i] >= start; i--)
        {
            if (kids.Starts[i] > start || kids.Ends[i] < end) continue;
            if (kids.Nodes[i].Id == n.Id) return kids.Nodes[i];
            if (only is not null) return cur.GetChildWithDescendant(n);   // two hold it: let tree-sitter choose
            only = kids.Nodes[i];
        }
        return only ?? cur.GetChildWithDescendant(n);
    }
}
