using System.Collections.Concurrent;

namespace CodeCarver.Core.Util;

/// <summary>
/// Whether a local copy of a tree (the carve root) still matches the tree it was copied from, often on a network share
/// ([advanced] mirrorOf). Reading a big tree over a share costs a run much of its time; carving a local copy saves that,
/// but only a faithful copy carves the same. Both trees are listed, never read: a file is the same when its size and
/// last-write time are (within <see cref="MtimeSlack"/>), the test the copy tools use to decide what to copy again.
/// </summary>
public static class MirrorCheck
{
    /// <summary>Last-write times this close are the same: FAT and some shares keep them to 2 seconds.</summary>
    public static readonly TimeSpan MtimeSlack = TimeSpan.FromSeconds(2);

    /// <summary>The differences, as paths relative to the roots ('/'-separated), and how long each listing took.</summary>
    public sealed record Result(int Files, List<string> Missing, List<string> Extra, List<string> Changed, long LocalMs, long SourceMs)
    {
        public int Differences => Missing.Count + Extra.Count + Changed.Count;
    }

    /// <summary>Compares <paramref name="local"/> with <paramref name="source"/>. A directory whose path relative to its
    /// root (as "/a/b/") contains one of <paramref name="excludeSegments"/> (as "/name/") is skipped on both sides.</summary>
    public static Result Compare(string local, string source, IReadOnlyList<string> excludeSegments)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var localFiles = List(local, excludeSegments);
        var localMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var sourceFiles = List(source, excludeSegments);
        var sourceMs = clock.ElapsedMilliseconds;

        var missing = new List<string>();
        var changed = new List<string>();
        foreach (var (rel, s) in sourceFiles)
        {
            if (!localFiles.TryGetValue(rel, out var l)) missing.Add(rel);
            else if (l.Size != s.Size || (l.Mtime - s.Mtime).Duration() > MtimeSlack) changed.Add(rel);
        }
        var extra = localFiles.Keys.Where(rel => !sourceFiles.ContainsKey(rel)).ToList();
        missing.Sort(StringComparer.Ordinal); changed.Sort(StringComparer.Ordinal); extra.Sort(StringComparer.Ordinal);
        return new Result(sourceFiles.Count, missing, extra, changed, localMs, sourceMs);
    }

    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Every file under <paramref name="root"/>: relative path to size and last-write time (UTC), from the
    /// directory listings alone. Directories are listed on several threads, a level at a time: on a share each listing
    /// is mostly a wait.</summary>
    static ConcurrentDictionary<string, (long Size, DateTime Mtime)> List(string root, IReadOnlyList<string> excludeSegments)
    {
        var files = new ConcurrentDictionary<string, (long, DateTime)>(PathComparer);
        // Hidden and system files too, as a copy takes them; an unreadable directory is left out on that side, so a
        // copy that could read it shows as a difference.
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var level = new List<(string Full, string Rel)> { (Path.GetFullPath(root), "") };
        // A directory link can loop back on itself; no real tree is this deep.
        for (var depth = 0; level.Count > 0 && depth < 256; depth++)
        {
            var next = new ConcurrentBag<(string, string)>();
            Parallel.ForEach(level, new ParallelOptions { MaxDegreeOfParallelism = 16 }, d =>
            {
                using var op = IoMeter.Start(IoMeter.Kind.List);
                foreach (var e in new DirectoryInfo(d.Full).EnumerateFileSystemInfos("*", options))
                {
                    var rel = d.Rel.Length == 0 ? e.Name : d.Rel + "/" + e.Name;
                    if (e is DirectoryInfo)
                    {
                        var seg = "/" + rel + "/";
                        if (!excludeSegments.Any(x => seg.Contains(x, StringComparison.OrdinalIgnoreCase))) next.Add((e.FullName, rel));
                    }
                    else if (e is FileInfo f) files[rel] = (f.Length, f.LastWriteTimeUtc);
                }
            });
            level = next.ToList();
        }
        return files;
    }
}
