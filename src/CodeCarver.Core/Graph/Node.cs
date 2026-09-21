namespace CodeCarver.Core.Graph;

/// <summary>An inclusive line range within a file. Default (0,0) means "unknown / whole file".</summary>
public readonly record struct SourceSpan(int StartLine, int EndLine)
{
    public bool IsKnown => StartLine > 0;
    public override string ToString() => IsKnown ? $"{StartLine}-{EndLine}" : "?";
}

/// <summary>Node-level attributes that inform root discovery and conservative reachability rules.</summary>
[Flags]
public enum NodeFlags
{
    None = 0,
    /// <summary>Address is taken somewhere — a candidate indirect-call target.</summary>
    AddressTaken = 1 << 0,
    /// <summary>Externally visible (exported symbol) — an implicit root for a library image.</summary>
    Exported = 1 << 1,
    /// <summary>Weak symbol.</summary>
    Weak = 1 << 2,
    /// <summary>Force-kept (KEEP()/used/retain) — an implicit root.</summary>
    Keep = 1 << 3,
    /// <summary>A virtual method — reachable via vtable, not only direct calls.</summary>
    Virtual = 1 << 4,
    /// <summary>Machine-generated (bindings, tables) — informational for reporting.</summary>
    Generated = 1 << 5,
}

/// <summary>
/// One vertex of the dependency graph: a definition (function, type, global, macro) or a
/// container (file, translation unit, section). Immutable; created/interned via <see cref="CodeGraph"/>.
/// </summary>
public sealed record Node
{
    public required NodeId Id { get; init; }
    public required NodeKind Kind { get; init; }
    public required string Name { get; init; }

    /// <summary>Defining file path, or null for container/synthetic nodes.</summary>
    public string? File { get; init; }

    public SourceSpan Span { get; init; }
    public NodeFlags Flags { get; init; }

    /// <summary>The file this node lives in for carve purposes: a File node <em>is</em> its path.</summary>
    public string? FilePath => Kind == NodeKind.File ? Name : File;

    public override string ToString() =>
        $"{Kind} '{Name}'{(FilePath is null ? "" : $" @ {System.IO.Path.GetFileName(FilePath)}:{Span}")}";
}
