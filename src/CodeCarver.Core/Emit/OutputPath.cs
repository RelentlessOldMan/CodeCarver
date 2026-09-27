namespace CodeCarver.Core.Emit;

/// <summary>
/// Guards the relationship between the scanned source tree and the <c>--out</c> directory.
///
/// The carve reads the source tree and writes the carved slice into <c>--out</c>. If those two trees
/// overlap — <c>--out</c> IS the source, is nested inside it, or is a parent of it — the emitter would
/// write on top of the user's own source. With <c>--prune</c> this is catastrophic: EmitPruned does a
/// <c>File.WriteAllText(dst, …)</c> straight onto <c>dst == src</c>, silently replacing the source file
/// with its carved (function-stripped) version. The input tree is sacred; the only safe rule is that the
/// two trees are disjoint. This helper is the single source of truth for that check so the CLI guard and
/// the emitter's belt-and-braces self-copy guard agree.
/// </summary>
public static class OutputPath
{
    /// <summary>
    /// True if <paramref name="sourceRoot"/> and <paramref name="outDir"/> are the same directory, or one
    /// is nested inside the other. Compares normalized full paths with a trailing separator so a sibling
    /// like <c>.../src-carved</c> does NOT match <c>.../src</c>. Path case is compared per-platform
    /// (case-insensitive on Windows/macOS-style filesystems, case-sensitive elsewhere).
    /// </summary>
    public static bool Overlaps(string sourceRoot, string outDir)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(outDir);
        var a = Normalize(sourceRoot);
        var b = Normalize(outDir);
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.Equals(b, cmp) || a.StartsWith(b, cmp) || b.StartsWith(a, cmp);
    }

    /// <summary>Full path, no trailing separator, then exactly one appended — so prefix comparison is a
    /// true path-segment boundary (".../src/" is a prefix of ".../src/x" but not of ".../src-carved/").</summary>
    private static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full + Path.DirectorySeparatorChar;
    }
}
