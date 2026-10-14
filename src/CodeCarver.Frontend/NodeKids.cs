using TreeSitter;
using TsNode = TreeSitter.Node;

namespace CodeCarver.Frontend;

/// <summary>
/// A node's children, listed with a tree cursor. The binding's <c>Node.Children</c> and <c>NamedChildren</c> fetch child
/// i by counting from the first, and in a tree with parse errors that count is slow: listing the 21,000 top-level items
/// of an 8 MB file took 6-13 s, against 12 ms with a cursor. Same nodes, same order.
/// </summary>
internal static class NodeKids
{
    public static List<TsNode> All(TsNode n) => Collect(n, named: false);

    /// <summary>The named children, as <c>Node.NamedChildren</c> lists them.</summary>
    public static List<TsNode> Named(TsNode n) => Collect(n, named: true);

    private static List<TsNode> Collect(TsNode n, bool named)
    {
        var list = new List<TsNode>();
        using var cur = new TreeCursor(n);
        if (!cur.GotoFirstChild()) return list;
        do
        {
            var c = cur.CurrentNode;
            if (!named || c.IsNamed) list.Add(c);
        }
        while (cur.GotoNextSibling());
        return list;
    }
}
