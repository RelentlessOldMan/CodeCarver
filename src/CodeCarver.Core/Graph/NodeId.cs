namespace CodeCarver.Core.Graph;

/// <summary>
/// A stable, interned handle to a <see cref="Node"/> in a <see cref="CodeGraph"/>.
/// Wraps an int so the reachability worklist can use fast, allocation-free sets and
/// so results are deterministic (ids are assigned in insertion order).
/// </summary>
public readonly record struct NodeId(int Value) : IComparable<NodeId>
{
    /// <summary>The absent/invalid id (e.g. the predecessor of a root).</summary>
    public static readonly NodeId None = new(-1);

    public bool IsValid => Value >= 0;

    public int CompareTo(NodeId other) => Value.CompareTo(other.Value);

    public override string ToString() => IsValid ? $"#{Value}" : "#none";
}
