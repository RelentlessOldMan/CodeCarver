namespace CodeCarver.Core.Emit;

/// <summary>Outcome of copying build-support files alongside a carved tree.</summary>
public readonly record struct SupportEmitResult(int Count, long Bytes, IReadOnlyList<string> Warnings);

/// <summary>
/// Copies the non-source files an embedded image needs to actually build — the ones the carve never
/// modelled because they aren't C translation units: <b>linker scripts</b> (<c>.ld</c>/<c>.lds</c>/
/// <c>.ldscript</c>) and <b>standalone startup assembly</b> (<c>.s</c>/<c>.S</c>/<c>.asm</c>), plus
/// anything the caller names with extra globs (Makefiles, TI <c>.cmd</c> linker command files, …).
///
/// Without these a carved firmware tree has its C carved perfectly but won't link — no vector table, no
/// memory map. Copying is generic (no toolchain hardcoded) and an over-approximation: every matching
/// file is taken (a repo with several board variants keeps them all), which is sound for building —
/// callers prune variants they don't want with the same exclude list the carve uses.
/// </summary>
public static class BuildSupportEmitter
{
    private static readonly string[] SupportExts = { ".ld", ".lds", ".ldscript", ".s", ".asm" };

    public static SupportEmitResult Copy(string sourceRoot, string outDir,
                                         IReadOnlyCollection<string> alreadyEmittedRel,
                                         IReadOnlyList<string> excludeDirs,
                                         IReadOnlyList<string> auxGlobs)
    {
        bool Keep(string p) => excludeDirs.Count == 0 ||
            !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase));

        var warnings = new List<string>();
        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
            if (SupportExts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && Keep(p))
                picked.Add(p);
        foreach (var glob in auxGlobs)
        {
            // Count actual matches, not the picked-set delta: a glob may match files the built-in support-ext
            // scan already took (e.g. --aux '*.ld' when a .ld was already picked) -- that's a match, not a miss.
            var matched = 0;
            foreach (var p in MatchGlob(sourceRoot, glob))
                if (Keep(p)) { picked.Add(p); matched++; }
            if (matched == 0)
                warnings.Add($"--aux glob '{glob}' matched no files under {sourceRoot} "
                             + "(a bare pattern like '*.inc' already searches all subdirectories)");
        }

        var already = new HashSet<string>(alreadyEmittedRel, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long bytes = 0;
        foreach (var p in picked)
        {
            var rel = Path.GetRelativePath(sourceRoot, p).Replace('\\', '/');
            if (already.Contains(rel)) continue; // already emitted as a graph file — don't double-copy/overwrite
            var dst = Path.Combine(outDir, rel);
            var dd = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);
            File.Copy(p, dst, overwrite: true);
            bytes += new FileInfo(dst).Length;
            count++;
        }
        return new SupportEmitResult(count, bytes, warnings);
    }

    /// <summary>
    /// Robustly resolve an --aux glob to files. <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/>
    /// takes a FILENAME pattern only, so a glob with a path separator (<c>sub/*.inc</c>, <c>/*.inc</c>,
    /// <c>*.inc</c> on Windows) throws and crashed the process. We split at the last separator into a
    /// (relative subdir, filename pattern), strip leading <c>/</c> and <c>./</c>, and enumerate that subdir
    /// recursively. An invalid pattern or missing subdir yields nothing (the caller warns) instead of throwing.
    /// </summary>
    internal static IEnumerable<string> MatchGlob(string root, string glob)
    {
        glob = (glob ?? "").Replace('\\', '/').Trim();
        // Enumeration is ALWAYS recursive (AllDirectories), so `**/` (recurse-any-dir) is redundant and a
        // bare `**` just means "any name" -> `*`. Normalizing these makes the syntax a user reaches for
        // ('**/*.inc', 'sub/**/*.inc') work instead of matching nothing.
        glob = glob.Replace("**/", "").Replace("**", "*");
        while (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob.Substring(2);
        glob = glob.TrimStart('/');
        var baseDir = root;
        var pattern = glob;
        var slash = glob.LastIndexOf('/');
        if (slash >= 0)
        {
            var sub = glob.Substring(0, slash).Replace('/', Path.DirectorySeparatorChar);
            pattern = glob.Substring(slash + 1);
            baseDir = Path.Combine(root, sub);
        }
        if (pattern.Length == 0) pattern = "*";
        if (!Directory.Exists(baseDir)) return Array.Empty<string>();
        try { return Directory.EnumerateFiles(baseDir, pattern, SearchOption.AllDirectories); }
        catch (ArgumentException) { return Array.Empty<string>(); }  // still-invalid pattern -> no crash
    }
}
