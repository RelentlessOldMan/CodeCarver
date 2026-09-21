namespace CodeCarver.Core.Graph;

/// <summary>
/// The kind of dependency an edge encodes. The reachability closure follows edges; the kind
/// decides <em>whether</em> an edge is followed under a given <see cref="Reachability.ReachabilityOptions"/>.
///
/// The crucial split is <see cref="EdgeKindExtensions.IsConservative"/>: some edges are exact
/// (a direct <see cref="Calls"/> names its target unambiguously), while others are the
/// over-approximating edges a source-level pass must add to stay <em>sound</em> — the ones that
/// don't appear syntactically at the call site (function pointers, vtables, inline asm). Following
/// those keeps a little extra but guarantees the carve still builds and runs. Not following them
/// yields the strictly-minimal (but unsafe) set, useful only for reporting the gap.
/// </summary>
public enum EdgeKind
{
    /// <summary>Direct call A → B (target statically known).</summary>
    Calls,

    /// <summary>Uses a type, global, or declaration (fields, casts, sizeof, extern refs).</summary>
    References,

    /// <summary>Macro expansion: code depends on a <c>#define</c>.</summary>
    Expands,

    /// <summary>A translation unit or file <c>#include</c>s a header file.</summary>
    Includes,

    /// <summary>A file/TU contains a definition (file-node → symbol-node).</summary>
    Contains,

    /// <summary>A definition depends on its defining file existing (symbol-node → file-node). Lets the
    /// closure pull a kept symbol's file — and, via that file's <see cref="Includes"/> edges, the
    /// headers it needs — into the carve, so a kept .c never loses a header it must compile against.</summary>
    DefinedIn,

    /// <summary>Virtual override relationship (derived::foo overrides base::foo).</summary>
    Overrides,

    /// <summary>A class vtable references a virtual method — conservative indirect edge.</summary>
    VtableEntry,

    /// <summary>A function's address is taken (&amp;fn, assigned to a pointer/table) — conservative.</summary>
    AddressTaken,

    /// <summary>An init/ctor array or startup table references a function (runs before main) — a root-ish edge.</summary>
    InitArrayEntry,

    /// <summary>Inline assembly references a symbol by name — conservative (invisible to the C parser).</summary>
    InlineAsmRef,

    /// <summary>Forced retention: linker <c>KEEP()</c>, used/retain attributes, exported symbol.</summary>
    LinkerKeep,
}

public static class EdgeKindExtensions
{
    /// <summary>
    /// True for edges that over-approximate — targets a source-level pass cannot resolve exactly and
    /// must keep wholesale to remain sound. Toggled by <see cref="Reachability.ReachabilityOptions"/>.
    /// </summary>
    public static bool IsConservative(this EdgeKind kind) => kind switch
    {
        EdgeKind.AddressTaken => true,
        EdgeKind.VtableEntry => true,
        EdgeKind.InlineAsmRef => true,
        _ => false,
    };
}
