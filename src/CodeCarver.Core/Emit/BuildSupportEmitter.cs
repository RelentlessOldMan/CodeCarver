using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Emit;

/// <summary>
/// The forceKeepFiles glob helpers shared by the CLI and <see cref="InfrastructureEmitter"/>: root-escape
/// refusal and on-disk glob matching. (This class once also copied linker scripts and startup assembly next to a
/// carved tree; <see cref="InfrastructureEmitter"/> replaced that by passing every non-dropped file through, and
/// the dead copier was removed — review TS9.)
/// </summary>
public static class BuildSupportEmitter
{
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
        var hits = new List<string>();
        IEnumerable<string> files;
        try { files = CodeCarver.Core.Util.SourceWalk.Files(root); }
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
