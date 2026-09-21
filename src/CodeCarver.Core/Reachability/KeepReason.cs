using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Records why a node survived the carve — captured at first discovery so the plan can explain any
/// kept node ("kept because it is a VectorTable root", or "kept because #42 references it via Calls").
/// This is what turns a carve from a black box into a reviewable report.
/// </summary>
public readonly record struct KeepReason
{
    /// <summary>Set when the node is itself a root; null when it was reached via an edge.</summary>
    public RootKind? AsRoot { get; init; }

    /// <summary>The node that pulled this one in (predecessor), or <see cref="NodeId.None"/> for a root.</summary>
    public NodeId Via { get; init; }

    /// <summary>The edge kind it was reached through, or null for a root.</summary>
    public EdgeKind? ViaEdge { get; init; }

    public bool IsRoot => AsRoot.HasValue;

    public static KeepReason Root(RootKind kind) => new() { AsRoot = kind, Via = NodeId.None };
    public static KeepReason Reached(NodeId via, EdgeKind edge) => new() { Via = via, ViaEdge = edge };
}
