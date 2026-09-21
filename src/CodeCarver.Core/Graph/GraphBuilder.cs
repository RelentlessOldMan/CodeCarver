namespace CodeCarver.Core.Graph;

/// <summary>
/// A thin, readable façade over <see cref="CodeGraph"/> for constructing graphs by hand — used by
/// tests and by the CLI demo, and a template for what a real front-end emits. Deliberately terse so
/// a graph reads like the code it models.
/// </summary>
public sealed class GraphBuilder
{
    public CodeGraph Graph { get; } = new();

    public NodeId Func(string name, string? file = null, NodeFlags flags = NodeFlags.None, int line = 0)
        => Graph.GetOrAddNode(NodeKind.Function, name, file, Span(line), flags);

    public NodeId Type(string name, string? file = null, int line = 0)
        => Graph.GetOrAddNode(NodeKind.Type, name, file, Span(line));

    public NodeId Global(string name, string? file = null, int line = 0)
        => Graph.GetOrAddNode(NodeKind.Global, name, file, Span(line));

    public NodeId Macro(string name, string? file = null, int line = 0)
        => Graph.GetOrAddNode(NodeKind.Macro, name, file, Span(line));

    public NodeId FileNode(string path)
        => Graph.GetOrAddNode(NodeKind.File, path);

    public void Calls(NodeId from, NodeId to) => Graph.AddEdge(from, to, EdgeKind.Calls);
    public void Refs(NodeId from, NodeId to) => Graph.AddEdge(from, to, EdgeKind.References);
    public void Expands(NodeId from, NodeId to) => Graph.AddEdge(from, to, EdgeKind.Expands);
    public void AddressTaken(NodeId from, NodeId to) => Graph.AddEdge(from, to, EdgeKind.AddressTaken);
    public void Vtable(NodeId from, NodeId to) => Graph.AddEdge(from, to, EdgeKind.VtableEntry);
    public void Edge(NodeId from, NodeId to, EdgeKind kind) => Graph.AddEdge(from, to, kind);

    private static SourceSpan Span(int line) => line > 0 ? new SourceSpan(line, line) : default;
}
