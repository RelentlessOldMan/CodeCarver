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
    public static IEnumerable<string> Files(string root, Action<string>? onSkippedLink)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        return new FileSystemEnumerable<string>(root, (ref FileSystemEntry e) => e.ToFullPath(), opts)
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
