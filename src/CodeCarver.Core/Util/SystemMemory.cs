using System.Runtime.InteropServices;

namespace CodeCarver.Core.Util;

/// <summary>
/// The machine's memory as the operating system sees it now: physical memory free, and how much more every process
/// together may commit before the machine runs out (RAM plus page file). A run at work ran a 32 GB machine out of
/// memory while its own working set looked small (Windows had paged it out): free memory and commit room say what the
/// process's own numbers don't.
/// </summary>
public static class SystemMemory
{
    /// <summary>Physical total and free, and commit room left (-1 where the system doesn't say), in bytes; null when
    /// it can't be read.</summary>
    public static (long Total, long Free, long CommitFree)? Now()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref s) ? ((long)s.TotalPhys, (long)s.AvailPhys, (long)s.AvailPageFile) : null;
            }
            if (OperatingSystem.IsLinux())
            {
                long total = -1, free = -1, commitLimit = -1, committed = -1;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) total = Kb(line);
                    else if (line.StartsWith("MemAvailable:")) free = Kb(line);
                    else if (line.StartsWith("CommitLimit:")) commitLimit = Kb(line);
                    else if (line.StartsWith("Committed_AS:")) committed = Kb(line);
                }
                if (total < 0 || free < 0) return null;
                return (total, free, commitLimit >= 0 && committed >= 0 ? Math.Max(0, commitLimit - committed) : -1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
                                       or EntryPointNotFoundException or FormatException) { }
        return null;

        static long Kb(string line)
        {
            var digits = new string(line.Where(char.IsDigit).ToArray());
            return long.Parse(digits) * 1024;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
