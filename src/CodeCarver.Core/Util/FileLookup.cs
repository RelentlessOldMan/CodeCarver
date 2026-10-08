namespace CodeCarver.Core.Util;

/// <summary>
/// Whether files exist, answered from directory listings read once each. Following <c>#include</c>s over a product
/// tree asks about every (directory, name) pair the -I search order visits: millions of <c>File.Exists</c> calls,
/// minutes on Windows and far more on a network drive. One run shares one instance; the tree isn't changing under it.
/// </summary>
public sealed class FileLookup
{
    private readonly Dictionary<string, HashSet<string>> listings = new(PathComparer.Default);
    // Directories that couldn't be listed (a network or permission error): asked about file by file from then on.
    private readonly HashSet<string> unlistable = new(PathComparer.Default);
    private readonly Func<string, IEnumerable<string>> enumerate;

    /// <param name="enumerate">A directory's files (full paths); <see cref="Directory.EnumerateFiles(string)"/> unless a
    /// test stands in for an unreachable share.</param>
    public FileLookup(Func<string, IEnumerable<string>>? enumerate = null) => this.enumerate = enumerate ?? Directory.EnumerateFiles;

    /// <summary>Directories looked up (each once): listed, or found absent (an empty listing).</summary>
    public int DirectoriesListed { get; private set; }

    /// <summary>The directory's file names; null when it couldn't be listed (a network or permission error). That is
    /// remembered as "ask per file", never as "empty": a hiccup must not make every file there "missing", and a dead
    /// share must not cost one more listing attempt (a network timeout) per probe.</summary>
    private HashSet<string>? Listing(string dir)
    {
        if (listings.TryGetValue(dir, out var names)) return names;
        if (unlistable.Contains(dir)) return null;
        names = new HashSet<string>(PathComparer.Default);
        try { foreach (var f in enumerate(dir)) names.Add(Path.GetFileName(f)); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException) { names.Clear(); }   // truly absent
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unlistable.Add(dir); return null; }
        DirectoriesListed++;
        return listings[dir] = names;
    }

    /// <summary>Directories that couldn't be listed; their files are checked one by one.</summary>
    public int DirectoriesUnlistable => unlistable.Count;

    /// <summary>True when <paramref name="full"/> (a full path) is an existing file.</summary>
    public bool Exists(string full)
    {
        if (Path.GetDirectoryName(full) is not { } dir) return false;
        var names = Listing(dir);
        return names is null ? File.Exists(full) : names.Contains(Path.GetFileName(full));
    }

    /// <summary>The full path of <paramref name="raw"/> (an include spelling, '/' or '\') under the full directory
    /// <paramref name="dir"/>, or null when no such file exists.</summary>
    public string? Probe(string dir, string raw)
    {
        dir = Path.TrimEndingDirectorySeparator(dir);
        if (raw.IndexOfAny(Separators) < 0 && raw is not "." and not "..")   // a bare name: nothing to normalize
        {
            var names = Listing(dir);
            if (names is not null) return names.Contains(raw) ? Path.Combine(dir, raw) : null;
        }
        string p;
        try { p = Path.GetFullPath(Path.Combine(dir, raw)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return Exists(p) ? p : null;
    }

    private static readonly char[] Separators = { '/', '\\' };
}
