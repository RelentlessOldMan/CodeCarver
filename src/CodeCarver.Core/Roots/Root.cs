using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Roots;

/// <summary>
/// Why a node is a seed of the carve. The whole correctness of a single deterministic pass rests on
/// this set being <em>complete</em>: the closure is exact given the roots, so a missing root kind is
/// the one way a from-<c>main</c> pass silently carves something needed.
/// </summary>
public enum RootKind
{
    /// <summary>The user named this symbol to retain (foo/bar/woot).</summary>
    ExplicitSymbol,

    /// <summary>The user named a whole file to retain.</summary>
    ExplicitFile,

    /// <summary>A program/image entry point (main, Reset_Handler, a target's start symbol).</summary>
    EntryPoint,

    /// <summary>An interrupt/exception vector table entry. Hardware jumps here by address; it is never
    /// "called" in source, so a naïve closure drops the ISR. Must be discovered as a root.</summary>
    VectorTable,

    /// <summary>A <c>.init_array</c> / startup-table entry — runs before main, referenced by address.</summary>
    InitArray,

    /// <summary>A static/global constructor (__attribute__((constructor)), C++ static init).</summary>
    Constructor,

    /// <summary>Force-kept by the linker (KEEP()) or a used/retain attribute.</summary>
    LinkerKeep,

    /// <summary>Externally visible symbol of a library image (its public surface is a root set).</summary>
    Exported,
}

/// <summary>A seed node for the reachability closure, tagged with why it was seeded (for reporting).</summary>
public readonly record struct Root(NodeId Node, RootKind Kind, string? Note = null)
{
    public override string ToString() => $"{Kind}({Node}){(Note is null ? "" : $": {Note}")}";
}
