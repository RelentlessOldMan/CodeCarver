namespace CodeCarver.Core.Graph;

/// <summary>
/// What a graph node represents. Reachability is computed over these uniformly; the kind
/// matters for emitting (how you carve a Function differs from a Macro or a File) and for
/// root discovery (only some kinds can be implicit roots).
/// </summary>
public enum NodeKind
{
    Unknown = 0,

    /// <summary>A function/method definition.</summary>
    Function,

    /// <summary>A struct/class/enum/typedef/union — anything a declaration can depend on.</summary>
    Type,

    /// <summary>A file- or global-scope variable/constant.</summary>
    Global,

    /// <summary>A preprocessor <c>#define</c>. CodeCompass deliberately skips these; CodeCarver needs
    /// them as first-class nodes because a kept function can depend on a macro that expands to code.</summary>
    Macro,

    /// <summary>A source or header file (path is the node <see cref="Node.Name"/>).</summary>
    File,

    /// <summary>A translation unit — a compiled .c/.cpp plus the config (defines/includes) it built with.</summary>
    TranslationUnit,

    /// <summary>A linker section (for map-file cross-checks and link-level carving).</summary>
    Section,

    /// <summary>A named label/alias/anchor referenced by inline asm or the linker script.</summary>
    Label,
}
