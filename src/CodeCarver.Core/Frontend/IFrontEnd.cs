using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Turns real source + a build configuration into a <see cref="CodeGraph"/>. This is the seam where
/// language- and toolchain-specific work lives, and where the two hard C/C++ requirements are met:
///
///  1. <b>Exact preprocessing.</b> The implementation runs the real preprocessor with the exact
///     flags for the target image (the <c>-D</c> defines and include paths from a compile database),
///     so every <c>#ifdef</c> collapses deterministically. No config-guessing ever reaches the graph.
///
///  2. <b>Sound conservative edges.</b> Where a target can't be resolved statically (a function
///     pointer, a virtual call), the front-end emits the over-approximating edges
///     (<see cref="EdgeKind.AddressTaken"/>, <see cref="EdgeKind.VtableEntry"/>, …) so the closure
///     stays sound. Correctness of the whole tool is set here, not in the engine.
///
/// Kept deliberately abstract tonight — the first concrete implementation (libclang / compiler-driven)
/// is the next milestone; the engine and tests don't depend on it existing.
/// </summary>
public interface IFrontEnd
{
    /// <summary>Languages/extensions this front-end handles (e.g. ".c", ".cpp", ".h").</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Builds (or extends) the dependency graph for the given build configuration.</summary>
    CodeGraph Build(BuildConfig config);
}

/// <summary>
/// The build configuration a carve is anchored to: which translation units compile, and with which
/// flags. For C/C++ this is normally ingested from a <c>compile_commands.json</c>. Everything about a
/// carve — which preprocessor branches are live, which files even exist — is defined relative to this.
/// </summary>
public sealed record BuildConfig
{
    /// <summary>Absolute path to the repo/source root.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>The translation units in the target image and their exact compile flags.</summary>
    public IReadOnlyList<CompileCommand> Commands { get; init; } = Array.Empty<CompileCommand>();
}

/// <summary>One entry of a compile database: a file plus the exact command/flags it compiled with.
/// <see cref="Defines"/> and <see cref="Includes"/> are the extracted essentials — the exact macros
/// and include paths that resolve the preprocessor for this translation unit.</summary>
public sealed record CompileCommand
{
    public required string File { get; init; }
    public string Directory { get; init; } = ".";
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Macro definitions, without the <c>-D</c> (e.g. "LUA_USE_LINUX", "FOO=1").</summary>
    public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();

    /// <summary>Include search paths, without the <c>-I</c>.</summary>
    public IReadOnlyList<string> Includes { get; init; } = Array.Empty<string>();
}
