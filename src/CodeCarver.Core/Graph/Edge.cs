namespace CodeCarver.Core.Graph;

/// <summary>A directed dependency edge: <see cref="From"/> needs <see cref="To"/>, via <see cref="Kind"/>.</summary>
public readonly record struct Edge(NodeId From, NodeId To, EdgeKind Kind)
{
    public override string ToString() => $"{From} --{Kind}--> {To}";
}
