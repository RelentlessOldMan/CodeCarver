using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Controls how the closure traverses the graph. The default is the <see cref="Safe"/> carve: follow
/// every edge, including the conservative over-approximating ones, so the result is guaranteed to
/// build and run (keep-if-unsure). <see cref="MinimalUnsafe"/> drops the conservative edges to expose
/// the strictly-direct set — never emit that, but the delta between the two is exactly the fat that
/// indirection (function pointers, vtables, inline asm) forces us to keep, which is worth reporting.
/// </summary>
public sealed record ReachabilityOptions
{
    /// <summary>Follow <see cref="EdgeKindExtensions.IsConservative"/> edges. Default true = sound.</summary>
    public bool FollowConservativeEdges { get; init; } = true;

    /// <summary>Optional explicit predicate; when set it fully decides edge traversal.</summary>
    public Func<EdgeKind, bool>? EdgeFilter { get; init; }

    public bool Follows(EdgeKind kind)
        => EdgeFilter?.Invoke(kind) ?? (FollowConservativeEdges || !kind.IsConservative());

    /// <summary>The sound, buildable carve. This is the one you ship.</summary>
    public static ReachabilityOptions Safe { get; } = new();

    /// <summary>Strictly-direct edges only — for measuring the indirection tax, never for emitting.</summary>
    public static ReachabilityOptions MinimalUnsafe { get; } = new() { FollowConservativeEdges = false };
}
