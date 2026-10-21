using Microsoft.Diagnostics.Runtime;

namespace CodeCarver.Cli;

/// <summary>
/// What the heap is made of, by the run's own look at it: the types taking the most memory and what refers to them.
/// A step's live memory says that it kept memory, never what; at work a step kept 9 GB and only its type list could say
/// which table it was. A child process (the run started again with <see cref="ChildVariable"/>) stops the run and walks its
/// heap, so the walk's own memory (buffers it pools, 400 MB on a 440 MB heap when done in-process) is never the run's,
/// and a run near its memory stop can't grow while it is walked. The run's own type names only, never what a string holds.
/// </summary>
static class HeapTypes
{
    /// <summary>The <paramref name="top"/> types by size, as "MB, objects, type, and the types referring to it (share
    /// of the references by size)", largest first; or one line saying why it couldn't look. Stops walking past
    /// <paramref name="budget"/> and says so: what it counted by then.</summary>
    public const string ChildVariable = "CODECARVER_HEAP_WALK_OF";   // "<process id>;<top>;<budget seconds>"

    /// <summary>The <paramref name="top"/> types of this process's heap, walked by a child process (see <see cref="Top"/>).</summary>
    public static List<string> OfThisProcess(int top, TimeSpan budget)
    {
        var lines = new List<string>();
        try
        {
            // This program again: codecarver(.exe), or dotnet codecarver.dll (also when run in a test host).
            var self = Environment.ProcessPath ?? "dotnet";
            var psi = Path.GetFileNameWithoutExtension(self).Equals("codecarver", StringComparison.OrdinalIgnoreCase)
                ? new System.Diagnostics.ProcessStartInfo(self)
                : new System.Diagnostics.ProcessStartInfo(Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet")
                  { ArgumentList = { typeof(HeapTypes).Assembly.Location } };
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.Environment[ChildVariable] = $"{Environment.ProcessId};{top};{(long)budget.TotalSeconds}";
            using var child = System.Diagnostics.Process.Start(psi)!;
            var output = child.StandardOutput.ReadToEndAsync();
            if (!child.WaitForExit(budget + TimeSpan.FromMinutes(1)))
            {
                try { child.Kill(); } catch { }
                lines.Add("(heap walk timed out)");
                return lines;
            }
            lines.AddRange(output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (lines.Count == 0) lines.Add($"(heap walk printed nothing, exit {child.ExitCode})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"(could not start the heap walk: {ex.GetType().Name}: {ex.Message})");
        }
        return lines;
    }

    /// <summary>In the child: walks the heap <see cref="ChildVariable"/> names and prints the lines; false when it
    /// names none (a normal run).</summary>
    public static bool RunAsChild()
    {
        var spec = Environment.GetEnvironmentVariable(ChildVariable)?.Split(';');
        if (spec is not { Length: 3 } || !int.TryParse(spec[0], out var pid) || !int.TryParse(spec[1], out var top)
            || !long.TryParse(spec[2], out var seconds)) return false;
        foreach (var line in Top(pid, top, TimeSpan.FromSeconds(seconds))) Console.Out.WriteLine(line);
        return true;
    }

    public static List<string> Top(int pid, int top, TimeSpan budget)
    {
        var lines = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var target = DataTarget.AttachToProcess(pid, suspend: true);   // still while it is walked
            using var runtime = target.ClrVersions[0].CreateRuntime();
            var heap = runtime.Heap;
            // Pass 1: size and count by type, the heap's regions walked in parallel (one at a time a 6M-object heap took 7 s).
            var segments = heap.Segments.ToArray();
            var names = new System.Collections.Concurrent.ConcurrentDictionary<ulong, string>();
            var parts = new List<Dictionary<ulong, (long Bytes, long Count)>>();   // not a ConcurrentBag: its per-thread queues outlive the walk
            long counted = 0;
            var whole = true;
            Parallel.ForEach(segments, () => new Dictionary<ulong, (long Bytes, long Count)>(), (seg, _, mine) =>
            {
                long n = 0;
                foreach (var o in seg.EnumerateObjects())
                {
                    if ((++n & 0xFFFF) == 0 && sw.Elapsed > budget) { whole = false; break; }
                    if (!o.IsValid || o.Type is not { } t) continue;
                    var e = mine.GetValueOrDefault(t.MethodTable);
                    mine[t.MethodTable] = (e.Bytes + (long)o.Size, e.Count + 1);
                    if (e.Count == 0) names.TryAdd(t.MethodTable, t.Name ?? "?");
                }
                Interlocked.Add(ref counted, n);
                return mine;
            }, mine => { lock (parts) parts.Add(mine); });
            var byType = new Dictionary<ulong, (string Name, long Bytes, long Count)>();
            foreach (var part in parts)
                foreach (var (mt, (b, c)) in part)
                    byType[mt] = byType.TryGetValue(mt, out var e) ? (e.Name, e.Bytes + b, e.Count + c) : (names.GetValueOrDefault(mt, "?"), b, c);
            var typesMs = sw.ElapsedMilliseconds;
            var biggest = byType.OrderByDescending(kv => kv.Value.Bytes).Take(top).ToList();
            var wanted = biggest.Select(kv => kv.Key).ToHashSet();
            // Pass 2: who refers to the biggest types (by the referred object's size). A sample of the referring objects
            // (about 2M in all) gives the shares; every one of them took 5 times pass 1.
            var stride = Math.Max(1L, counted / 2_000_000);
            var complete = whole;
            var refParts = new List<Dictionary<(ulong Child, ulong Parent), long>>();
            if (whole)
                Parallel.ForEach(segments, () => new Dictionary<(ulong Child, ulong Parent), long>(), (seg, _, mine) =>
                {
                    long n = 0;
                    foreach (var o in seg.EnumerateObjects())
                    {
                        if ((++n & 0xFFFF) == 0 && sw.Elapsed > budget) { complete = false; break; }
                        if (n % stride != 0 || !o.IsValid || o.Type is not { } pt) continue;
                        foreach (var r in o.EnumerateReferences())
                            if (r.Type is { } ct && wanted.Contains(ct.MethodTable))
                                mine[(ct.MethodTable, pt.MethodTable)] = mine.GetValueOrDefault((ct.MethodTable, pt.MethodTable)) + (long)r.Size;
                    }
                    return mine;
                }, mine => { lock (refParts) refParts.Add(mine); });
            var by = new Dictionary<ulong, Dictionary<ulong, long>>();
            foreach (var part in refParts)
                foreach (var ((child, parent), b) in part)
                {
                    if (!by.TryGetValue(child, out var parents)) by[child] = parents = new();
                    parents[parent] = parents.GetValueOrDefault(parent) + b;
                }
            foreach (var (mt, (name, bytes, count)) in biggest)
            {
                var refs = by.GetValueOrDefault(mt);
                var total = refs?.Values.Sum() ?? 0;
                var from = refs is null || total == 0 ? ""
                    : " <- " + string.Join(", ", refs.OrderByDescending(kv => kv.Value).Take(3)
                        .Select(kv => $"{(byType.TryGetValue(kv.Key, out var p) ? p.Name : "?")} {kv.Value * 100 / total}%"));
                lines.Add($"{bytes >> 20:N0} MB, {count:N0} x {name}{from}");
            }
            lines.Add($"(heap walk {sw.ElapsedMilliseconds:N0} ms ({typesMs:N0} ms for the types), {counted:N0} objects, {byType.Count:N0} types{(whole ? "" : $", PARTIAL: cut short after {counted:N0} objects")}{(whole && !complete ? ", referrers cut short" : "")})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"(could not walk the heap: {ex.GetType().Name}: {ex.Message})");
        }
        return lines;
    }
}
