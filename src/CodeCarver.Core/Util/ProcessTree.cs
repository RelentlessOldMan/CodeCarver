using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeCarver.Core.Util;

/// <summary>
/// Which processes each process started, as the operating system lists them now. A compiler driver holds little itself:
/// <c>gcc -E</c> runs <c>cc1</c>, which holds the defines, so a child's memory is its whole tree's. Windows lists every
/// process and its parent in one snapshot, Linux in <c>/proc</c>; elsewhere the list is empty and a child counts alone.
/// </summary>
public sealed class ProcessTree
{
    readonly Dictionary<int, List<int>> _children = new();

    ProcessTree() { }

    /// <summary>Every process's children, now.</summary>
    public static ProcessTree Snapshot()
    {
        var t = new ProcessTree();
        try
        {
            if (OperatingSystem.IsWindows()) t.FromToolhelp();
            else if (OperatingSystem.IsLinux()) t.FromProc();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { t._children.Clear(); }
        return t;
    }

    void Add(int parent, int child)
    {
        if (parent == child) return;
        if (!_children.TryGetValue(parent, out var l)) _children[parent] = l = new List<int>();
        l.Add(child);
    }

    /// <summary>The processes <paramref name="pid"/> started, theirs, and so on (not <paramref name="pid"/> itself).</summary>
    public IEnumerable<int> Descendants(int pid)
    {
        var seen = new HashSet<int> { pid };
        var todo = new Stack<int>();
        todo.Push(pid);
        while (todo.Count > 0)
            if (_children.TryGetValue(todo.Pop(), out var kids))
                foreach (var k in kids)
                    if (seen.Add(k)) { todo.Push(k); yield return k; }
    }

    /// <summary>What <paramref name="root"/> and every process it started hold privately now.</summary>
    public long PrivateBytes(Process root)
    {
        root.Refresh();
        var sum = root.PrivateMemorySize64;
        foreach (var pid in Descendants(root.Id))
        {
            try { using var p = Process.GetProcessById(pid); sum += p.PrivateMemorySize64; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited */ }
        }
        return sum;
    }

    void FromProc()
    {
        foreach (var d in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(d);
            if (!int.TryParse(name, out var pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(d, "stat")); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            // "pid (comm) state ppid ...": comm can hold spaces and parentheses, so read after the last ')'.
            var close = stat.LastIndexOf(')');
            if (close < 0) continue;
            var rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length > 1 && int.TryParse(rest[1], out var ppid)) Add(ppid, pid);
        }
    }

    void FromToolhelp()
    {
        var snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == InvalidHandle) return;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snap, ref e); more; more = Process32NextW(snap, ref e))
                Add((int)e.ParentProcessId, (int)e.ProcessId);
        }
        finally { CloseHandle(snap); }
    }

    const uint SnapProcess = 0x2;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
