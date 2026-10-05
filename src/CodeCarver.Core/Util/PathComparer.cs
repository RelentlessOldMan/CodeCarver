namespace CodeCarver.Core.Util;

/// <summary>
/// How two paths in one source tree compare (review RB8). Windows and macOS file systems are case-insensitive
/// by default, so <c>Foo.c</c> and <c>foo.c</c> are one file; on Linux they are two, and a set that folds case
/// silently drops one of them (the emitter skips "already written" <c>a.h</c> because <c>A.h</c> was). Use this
/// for any set or map whose collapse would LOSE a file. Sets that only widen a keep decision (observed or forced
/// files matched leniently) may stay case-insensitive: folding there keeps more, which is sound.
/// </summary>
public static class PathComparer
{
    public static StringComparer Default { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
