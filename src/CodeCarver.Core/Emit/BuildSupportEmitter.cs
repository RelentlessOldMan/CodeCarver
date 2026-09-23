namespace CodeCarver.Core.Emit;

/// <summary>Outcome of copying build-support files alongside a carved tree.</summary>
public readonly record struct SupportEmitResult(int Count, long Bytes);

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

        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
            if (SupportExts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && Keep(p))
                picked.Add(p);
        foreach (var glob in auxGlobs)
            foreach (var p in Directory.EnumerateFiles(sourceRoot, glob, SearchOption.AllDirectories))
                if (Keep(p)) picked.Add(p);

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
        return new SupportEmitResult(count, bytes);
    }
}
