using System.Text;
using System.Text.RegularExpressions;

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
    public static IEnumerable<string> MatchGlob(string root, string glob)
    {
        glob = (glob ?? "").Replace('\\', '/').Trim();
        while (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob.Substring(2);
        glob = glob.TrimStart('/');
        if (glob.Length == 0) return Array.Empty<string>();

        // Match the file's FULL path RELATIVE TO ROOT against the glob as a regex, so `**` works ANYWHERE
        // (leading, mid-path a/**/b, or standalone) rather than being string-stripped -- a naive
        // Replace("**/","") turned a/**/b/*.inc into the literal a/b/*.inc and SILENTLY dropped a/q/b/y.inc
        // (eval-#6 under-match). Enumeration is rooted at the longest wildcard-free leading dir so a huge
        // tree isn't fully walked when the glob is anchored (e.g. sub/**/x -> walk only sub/).
        // A separator-less pattern (e.g. *.inc) matches by BASENAME at any depth (recursive) -- the common
        // --aux case; a pattern WITH a '/' is anchored at the root and uses * / ** for path structure.
        var hasSlash = glob.Contains('/');
        var segs = glob.Split('/');
        var lit = 0;
        while (lit < segs.Length - 1 && segs[lit].IndexOfAny(new[] { '*', '?' }) < 0) lit++;
        var litPrefix = string.Join("/", segs.Take(lit));
        var baseDir = litPrefix.Length == 0 ? root : Path.Combine(root, litPrefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(baseDir)) return Array.Empty<string>();

        var rx = GlobToRegex(glob, matchAnyDepth: !hasSlash);
        var hits = new List<string>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories); }
        catch (Exception) { return Array.Empty<string>(); }
        foreach (var f in files)
            if (rx.IsMatch(Path.GetRelativePath(root, f).Replace('\\', '/')))
                hits.Add(f);
        return hits;
    }

    /// <summary>Glob -> anchored regex over '/'-separated relative paths. `**/` = zero-or-more directory
    /// segments, standalone `**` = any chars, `*` = any run within one segment, `?` = one non-separator.</summary>
    private static Regex GlobToRegex(string glob, bool matchAnyDepth)
    {
        var sb = new StringBuilder("^");
        if (matchAnyDepth) sb.Append("(?:.*/)?");   // separator-less pattern -> match basename at any depth
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;                                       // consumed the second '*'
                if (i + 1 < glob.Length && glob[i + 1] == '/') { sb.Append("(?:.*/)?"); i++; } // `**/` -> zero+ dirs
                else sb.Append(".*");                       // trailing/standalone `**`
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '/') sb.Append('/');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
