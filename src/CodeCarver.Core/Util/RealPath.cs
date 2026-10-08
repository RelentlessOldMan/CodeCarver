using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeCarver.Core.Util;

/// <summary>
/// Where a directory really is: through junctions, symlinks, subst drives and mapped drives (a mapped drive comes
/// back as its <c>\\server\share</c> path). Two spellings of one directory then compare equal — an include directory
/// that reaches the carve root through a link is still the root.
/// </summary>
public static class RealPath
{
    /// <summary>The directory's real full path, or null when it doesn't exist or can't be resolved.</summary>
    public static string? Directory(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            return OperatingSystem.IsWindows() ? WindowsFinal(full) : Walk(full, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or PathTooLongException) { return null; }
    }

    // Each component, following any link it is (a link's target can itself sit under a link: resolved again).
    private static string? Walk(string full, int depth)
    {
        if (depth > 32 || !System.IO.Directory.Exists(full)) return null;
        var root = Path.GetPathRoot(full) ?? "/";
        var cur = root;
        foreach (var seg in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, seg);
            var info = new DirectoryInfo(cur);
            if (info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) return null;
            var resolved = Walk(target.FullName, depth + 1);
            if (resolved is null) return null;
            cur = resolved;
        }
        return cur;
    }

    private static string? WindowsFinal(string full)
    {
        using var h = CreateFileW(full, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                                  FileFlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var buf = new StringBuilder(512);
        var n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Capacity, 0);
        if (n > buf.Capacity) { buf.EnsureCapacity((int)n); n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Capacity, 0); }
        if (n == 0 || n > buf.Capacity) return null;
        var s = buf.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) s = @"\\" + s[8..];
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        return s;
    }

    private const uint FileFlagBackupSemantics = 0x02000000;   // lets CreateFile open a directory

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
                                                     FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, uint size, uint flags);
}
