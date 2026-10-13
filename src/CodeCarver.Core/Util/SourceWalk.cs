using System.IO.Enumeration;

namespace CodeCarver.Core.Util;

/// <summary>
/// The single recursive file walk for scanning a source tree. Centralizes two robustness rules every scan
/// needs on a real Windows tree:
///   • <c>IgnoreInaccessible</c> — an unreadable directory (routine on a network share) is skipped, not
///     thrown mid-enumeration leaving a partial run (eval-#7).
///   • Do not recurse into directory <b>reparse points</b> (junctions / directory symlinks). Following them
///     loops forever on a junction to an ancestor and double-scans (and double-emits) on one to a sibling.
///     Crucially this skips only <em>recursion into</em> reparse-point DIRECTORIES — a reparse-point FILE
///     (a symlinked source file) is still returned, because dropping a real source file would be unsound.
/// VCS metadata directories (<c>.git</c>, <c>.svn</c>, <c>.hg</c>, <c>.bzr</c>) are not entered: they are never a
/// build input and can hold hundreds of thousands of objects. HIDDEN files and directories are otherwise walked
/// like any other — on Linux every dot-file reports as Hidden, so skipping Hidden lost <c>.config</c> (Kconfig),
/// <c>.gitmodules</c>, <c>.clang-format</c> and any <c>.dir/</c> (review RB6). System-attributed entries are skipped.
/// </summary>
public static class SourceWalk
{
    /// <summary>Enumerate full paths of every file under <paramref name="root"/>, applying the rules above.
    /// Lazy: safe to filter/stream. Returns empty if the root can't be enumerated at all.</summary>
    public static IEnumerable<string> Files(string root) => Files(root, null);

    /// <summary>As <see cref="Files(string)"/>; <paramref name="onSkippedLink"/> receives the full path of each
    /// directory junction/symlink that was not entered, so a caller can say so instead of skipping silently
    /// (review RB7).</summary>
    public static IEnumerable<string> Files(string root, Action<string>? onSkippedLink) =>
        Walk(root, onSkippedLink, (ref FileSystemEntry e) => e.ToFullPath());

    /// <summary>As <see cref="Files(string, Action{string})"/>, with each file's size, in the same order. The size comes
    /// with the directory listing, so no file is opened or looked up again. And the directories are listed a window at
    /// a time: on a network drive each listing waits a round trip, and one after another a whole-tree walk took six
    /// minutes. The walk order is kept: a directory's files, then its subdirectories' in turn, breadth first, as the
    /// recursive enumeration yields them. A symlinked file's size is the link's, not its target's, so it comes back as
    /// -1 (look it up).</summary>
    public static IEnumerable<(string Path, long Length)> Entries(string root, Action<string>? onSkippedLink)
    {
        ReadAhead.EnsureThreads();
        var gate = new SemaphoreSlim(ReadAhead.Window);
        Task<Listing> List(string d) => Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return ListOne(d); } finally { gate.Release(); }
        });
        // Every directory is listed as soon as its parent's listing names it; they are taken back in walk order.
        var order = new List<Task<Listing>?> { List(root) };
        for (var i = 0; i < order.Count; i++)
        {
            var listing = order[i]!.GetAwaiter().GetResult();
            order[i] = null;
            foreach (var f in listing.Files) yield return f;
            foreach (var (sub, link) in listing.Dirs)
                if (link) onSkippedLink?.Invoke(sub);
                else order.Add(List(sub));
        }
    }

    sealed record Listing(List<(string Path, long Length)> Files, List<(string Path, bool Link)> Dirs);

    static Listing ListOne(string dir)
    {
        var files = new List<(string, long)>();
        var dirs = new List<(string, bool)>();
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        try
        {
            var entries = new FileSystemEnumerable<(bool Dir, bool Link, string Path, long Length)>(dir,
                (ref FileSystemEntry e) => (e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0, e.ToFullPath(),
                                            e.IsDirectory ? 0 : e.Length), opts);
            foreach (var (isDir, link, path, length) in entries)
                if (!isDir) files.Add((path, link ? -1 : length));
                else if (!IsVcsDir(Path.GetFileName(path.AsSpan()))) dirs.Add((path, link));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return new Listing(files, dirs);
    }

    static IEnumerable<T> Walk<T>(string root, Action<string>? onSkippedLink, FileSystemEnumerable<T>.FindTransform transform)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        return new FileSystemEnumerable<T>(root, transform, opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            // Recurse into a subdirectory only if it is NOT a reparse point (junction/symlink).
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                if (IsVcsDir(e.FileName)) return false;
                if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return true;
                onSkippedLink?.Invoke(e.ToFullPath());
                return false;
            },
        };
    }

    private static bool IsVcsDir(ReadOnlySpan<char> name) =>
        name.SequenceEqual(".git") || name.SequenceEqual(".svn") || name.SequenceEqual(".hg") || name.SequenceEqual(".bzr");
}
