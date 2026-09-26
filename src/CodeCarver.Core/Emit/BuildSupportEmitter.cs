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
        // IgnoreInaccessible: an unreadable dir under the root (common on a network share) must be skipped,
        // not throw mid-walk and leave a partial tree.
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var p in Directory.EnumerateFiles(sourceRoot, "*.*", walk))
            if (SupportExts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && Keep(p))
                picked.Add(p);
        foreach (var glob in auxGlobs)
        {
            // Refuse a glob that escapes the source root — it could clobber files outside --out (eval-#7).
            if (GlobEscapesRoot(glob))
            {
                warnings.Add($"--aux glob '{glob}' refused: contains '..' or an absolute path (would write outside --out)");
                continue;
            }
            // Count actual matches, not the picked-set delta: a glob may match files the built-in support-ext
            // scan already took (e.g. --aux '*.ld' when a .ld was already picked) -- that's a match, not a miss.
            var matched = 0;
            foreach (var p in MatchGlob(sourceRoot, glob))
                if (Keep(p)) { picked.Add(p); matched++; }
            if (matched == 0)
                warnings.Add($"--aux glob '{glob}' matched no files under {sourceRoot} "
                             + "(a bare pattern like '*.inc' already searches all subdirectories)");
        }

        var outFull = Path.GetFullPath(outDir);
        var already = new HashSet<string>(alreadyEmittedRel, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long bytes = 0;
        foreach (var p in picked)
        {
            var rel = Path.GetRelativePath(sourceRoot, p).Replace('\\', '/');
            if (already.Contains(rel)) continue; // already emitted as a graph file — don't double-copy/overwrite
            var dst = Path.Combine(outDir, rel);
            // Belt-and-braces: NEVER write outside --out, whatever the rel path resolved to.
            var dstFull = Path.GetFullPath(dst);
            if (!dstFull.StartsWith(outFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dstFull, outFull, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"skipped '{rel}': destination would fall outside --out");
                continue;
            }
            var dd = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);
            File.Copy(p, dst, overwrite: true);
            bytes += new FileInfo(dst).Length;
            count++;
        }
        return new SupportEmitResult(count, bytes, warnings);
    }

    /// <summary>An --aux glob that escapes the source root (a <c>..</c> segment) or is rooted/absolute must
    /// never be honored: the destination is <c>Path.Combine(outDir, relPathFromRoot)</c>, so a <c>../</c>
    /// carries into the output path and could read/copy — and OVERWRITE — files OUTSIDE <c>--out</c>, breaking
    /// the write-only-under-out rule (eval-#7). Callers reject these with a clear message.</summary>
    public static bool GlobEscapesRoot(string glob)
    {
        glob = (glob ?? "").Replace('\\', '/').Trim();
        while (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob.Substring(2);
        glob = glob.TrimStart('/');   // a LEADING '/' means "source root" (root-relative), not an absolute path
        if (Path.IsPathRooted(glob)) return true;                       // drive/UNC-rooted (C:\..., //server) -> escapes
        foreach (var seg in glob.Split('/')) if (seg == "..") return true;
        return false;
    }

    /// <summary>
    /// Resolve an --aux glob to files under <paramref name="root"/>. The glob is compiled to a regex matched
    /// against each file's path RELATIVE TO ROOT, so <c>**</c> works anywhere (leading, mid-path <c>a/**/b</c>,
    /// standalone); a separator-less pattern (<c>*.inc</c>) matches by basename at any depth; a pattern with a
    /// <c>/</c> is root-anchored. Enumeration starts AT ROOT so every returned segment carries its real
    /// on-disk casing (a case-sensitive build host needs <c>sub/</c>, not <c>SUB/</c>) and, being confined to
    /// root, never returns a path outside it. <see cref="EnumerationOptions.IgnoreInaccessible"/> skips
    /// unreadable dirs (common on a network share) instead of throwing LATER inside this lazy loop — where a
    /// try around the call can't catch it — and leaving a partial tree.
    /// </summary>
    public static IEnumerable<string> MatchGlob(string root, string glob)
    {
        glob = (glob ?? "").Replace('\\', '/').Trim();
        while (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob.Substring(2);
        glob = glob.TrimStart('/');
        if (glob.Length == 0 || GlobEscapesRoot(glob)) return Array.Empty<string>();

        var rx = GlobToRegex(glob, matchAnyDepth: !glob.Contains('/'));
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var hits = new List<string>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", opts); }
        catch (Exception) { return hits; }
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
