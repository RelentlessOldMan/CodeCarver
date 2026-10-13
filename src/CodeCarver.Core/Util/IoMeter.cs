using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace CodeCarver.Core.Util;

/// <summary>
/// Counts and times the waits a run makes — directory listings, file reads, copies, compiler runs — and knows which
/// are still waiting and for how long. A run that seems stuck on a slow drive is nearly always waiting on one of
/// these; the watchdog's report says which, how many, and how slow, with no path or name in it.
/// </summary>
public static class IoMeter
{
    public enum Kind { List, Read, Copy, Launch }
    public const int Kinds = 4;

    static readonly string[] Names = { "directory listings", "file reads", "file copies", "compiler runs" };
    static readonly string[] Keys = { "list", "read", "copy", "launch" };

    /// <summary>"directory listings", "file reads", ...</summary>
    public static string Name(Kind k) => Names[(int)k];

    /// <summary>The summary key part: list, read, copy, launch.</summary>
    public static string Key(Kind k) => Keys[(int)k];

    static readonly long[] Count = new long[Kinds], Ticks = new long[Kinds], MaxTicks = new long[Kinds];
    static readonly ConcurrentDictionary<long, (Kind Kind, long Start)> InFlight = new();
    static long _next;

    /// <summary>One wait, timed from here until it is disposed.</summary>
    public readonly struct Op : IDisposable
    {
        readonly long _id, _start;
        readonly Kind _kind;
        internal Op(long id, Kind kind, long start) { _id = id; _kind = kind; _start = start; }

        public void Dispose()
        {
            if (_id == 0) return;
            InFlight.TryRemove(_id, out _);
            var took = Stopwatch.GetTimestamp() - _start;
            var k = (int)_kind;
            Interlocked.Increment(ref Count[k]);
            Interlocked.Add(ref Ticks[k], took);
            long max;
            while (took > (max = Interlocked.Read(ref MaxTicks[k])) && Interlocked.CompareExchange(ref MaxTicks[k], took, max) != max) { }
        }
    }

    public static Op Start(Kind kind)
    {
        var id = Interlocked.Increment(ref _next);
        var start = Stopwatch.GetTimestamp();
        InFlight[id] = (kind, start);
        return new Op(id, kind, start);
    }

    public static string ReadAllText(string path) { using (Start(Kind.Read)) return File.ReadAllText(path); }
    public static string ReadAllText(string path, Encoding encoding) { using (Start(Kind.Read)) return File.ReadAllText(path, encoding); }
    public static byte[] ReadAllBytes(string path) { using (Start(Kind.Read)) return File.ReadAllBytes(path); }
    /// <summary>File.ReadLines, timed as one read from the open until the last line is taken (or the loop stops). The
    /// file opens here, as File.ReadLines opens it, so a missing file throws at the call and not at the loop.</summary>
    public static IEnumerable<string> ReadLines(string path) => ReadLines(path, null);
    public static IEnumerable<string> ReadLines(string path, Encoding? encoding)
    {
        // The open waits like any read, but a caller that never loops must not leave a wait open for the watchdog: the
        // open's wait ends here, and the read is counted once, from the open to the last line, when the loop runs.
        var id = Interlocked.Increment(ref _next);
        var start = Stopwatch.GetTimestamp();
        InFlight[id] = (Kind.Read, start);
        IEnumerable<string> lines;
        try { lines = encoding is null ? File.ReadLines(path) : File.ReadLines(path, encoding); }
        catch { new Op(id, Kind.Read, start).Dispose(); throw; }
        finally { InFlight.TryRemove(id, out _); }
        return Timed(lines, start);
    }

    static IEnumerable<string> Timed(IEnumerable<string> lines, long start)
    {
        var id = Interlocked.Increment(ref _next);
        InFlight[id] = (Kind.Read, start);
        using var op = new Op(id, Kind.Read, start);
        foreach (var line in lines) yield return line;
    }
    public static void Copy(string from, string to, bool overwrite) { using (Start(Kind.Copy)) File.Copy(from, to, overwrite); }

    /// <summary>The totals so far, and what is waiting now.</summary>
    public sealed record Snapshot(long[] Count, long[] Ticks, long[] MaxTicks, int[] Waiting, long[] OldestTicks)
    {
        /// <summary>Waits finished since <paramref name="earlier"/> (all kinds), to tell a run that moves from one that doesn't.</summary>
        public long DoneSince(Snapshot? earlier) => Count.Sum() - (earlier?.Count.Sum() ?? 0);
    }

    public static Snapshot Now()
    {
        var count = new long[Kinds];
        var ticks = new long[Kinds];
        var maxTicks = new long[Kinds];
        for (var k = 0; k < Kinds; k++)
        {
            count[k] = Interlocked.Read(ref Count[k]);
            ticks[k] = Interlocked.Read(ref Ticks[k]);
            maxTicks[k] = Interlocked.Read(ref MaxTicks[k]);
        }
        var waiting = new int[Kinds];
        var oldest = new long[Kinds];
        var now = Stopwatch.GetTimestamp();
        foreach (var (kind, start) in InFlight.Values)
        {
            waiting[(int)kind]++;
            oldest[(int)kind] = Math.Max(oldest[(int)kind], now - start);
        }
        return new Snapshot(count, ticks, maxTicks, waiting, oldest);
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The longest any wait has been going on right now, in seconds (0 when nothing waits).</summary>
    public static double LongestWaitSeconds(Snapshot s) => Ms(s.OldestTicks.Max()) / 1000;

    /// <summary>One line per kind with any activity since <paramref name="since"/>: how many finished, their average and
    /// slowest time (the slowest is over the whole run), and how many are waiting now and the longest wait.</summary>
    public static IEnumerable<string> Describe(Snapshot now, Snapshot? since)
    {
        for (var k = 0; k < Kinds; k++)
        {
            var n = now.Count[k] - (since?.Count[k] ?? 0);
            var t = now.Ticks[k] - (since?.Ticks[k] ?? 0);
            if (n == 0 && now.Waiting[k] == 0) continue;
            var line = $"{Names[k]}: {n:N0} done";
            if (n > 0) line += $", avg {Ms(t / n):N1} ms, slowest in run {Ms(now.MaxTicks[k]):N0} ms";
            if (now.Waiting[k] > 0) line += $"; {now.Waiting[k]} waiting now, longest {Ms(now.OldestTicks[k]) / 1000:N0} s";
            yield return line;
        }
    }

    /// <summary>Totals for the summary since <paramref name="since"/> (the run's start): io.&lt;kind&gt;.count / .ms, and .maxMs, the slowest
    /// in this process.</summary>
    public static IEnumerable<(string Key, long Value)> Totals(Snapshot s, Snapshot? since = null)
    {
        for (var k = 0; k < Kinds; k++)
        {
            yield return ($"io.{Keys[k]}.count", s.Count[k] - (since?.Count[k] ?? 0));
            yield return ($"io.{Keys[k]}.ms", (long)Ms(s.Ticks[k] - (since?.Ticks[k] ?? 0)));
            yield return ($"io.{Keys[k]}.maxMs", (long)Ms(s.MaxTicks[k]));
        }
    }
}
