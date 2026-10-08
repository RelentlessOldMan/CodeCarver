namespace CodeCarver.Core.Frontend;

/// <summary>One entry of a compile database: a file plus the exact command/flags it compiled with.
/// <see cref="Defines"/> and <see cref="Includes"/> are the extracted essentials — the exact macros
/// and include paths that resolve the preprocessor for this translation unit.</summary>
public sealed record CompileCommand
{
    public required string File { get; init; }
    public string Directory { get; init; } = ".";
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>The compiler driver as the log spelled it (path or name); its arguments are <see cref="Arguments"/>.</summary>
    public string? Driver { get; init; }

    /// <summary>Macro definitions, without the <c>-D</c> (e.g. "LUA_USE_LINUX", "FOO=1").</summary>
    public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();

    /// <summary>Include search paths, without the <c>-I</c>.</summary>
    public IReadOnlyList<string> Includes { get; init; } = Array.Empty<string>();

    /// <summary>Files force-included before the source (<c>-include</c>, <c>-imacros</c>, <c>/FI</c>); the
    /// macros they define are part of this TU's configuration.</summary>
    public IReadOnlyList<string> ForcedIncludes { get; init; } = Array.Empty<string>();

    /// <summary>True when the define set may be incomplete (an unreadable <c>@response</c> file): the file must
    /// not be resolved closed-world from this command.</summary>
    public bool Incomplete { get; init; }

    /// <summary>Other directories the command may have run in: <c>make -j</c> runs sibling sub-makes at once, so
    /// their "Entering directory" lines interleave and the last one entered is only a guess. The caller resolves
    /// <see cref="File"/> against each and keeps the ones where it exists.</summary>
    public IReadOnlyList<string> AlternativeDirectories { get; init; } = Array.Empty<string>();
}
