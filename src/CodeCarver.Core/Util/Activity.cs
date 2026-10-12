namespace CodeCarver.Core.Util;

/// <summary>
/// How far the step a run is in has got: files done (of how many, when known) and bytes, for the heartbeat a long run
/// prints once a minute. Only one step runs at a time, so one counter is enough; whoever starts a step resets it, and
/// the loops that go through files tick it (<see cref="ReadAhead"/> for every file it reads).
/// </summary>
public static class Activity
{
    static readonly object Gate = new();
    static string _what = "files";
    static long _done, _total = -1, _bytes;

    /// <summary>Starts counting a new step: <paramref name="what"/> names what is counted ("files copied").</summary>
    public static void Begin(string what = "files", long total = -1)
    {
        lock (Gate) { _what = what; _total = total; Interlocked.Exchange(ref _done, 0); Interlocked.Exchange(ref _bytes, 0); }
    }

    /// <summary>Raises the total when a loop knows it and nothing set one.</summary>
    public static void Expect(long total)
    {
        lock (Gate) if (_total < 0 && Interlocked.Read(ref _done) == 0) _total = total;
    }

    /// <summary>One more item done, of <paramref name="bytes"/> bytes when known.</summary>
    public static void Tick(long bytes = 0)
    {
        Interlocked.Increment(ref _done);
        if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
    }

    /// <summary>"12,400/48,000 files copied, 3.1 GB", or null before anything was counted.</summary>
    public static string? Describe()
    {
        string what; long total;
        lock (Gate) { what = _what; total = _total; }
        var done = Interlocked.Read(ref _done);
        var bytes = Interlocked.Read(ref _bytes);
        if (done == 0) return null;
        return (total > 0 ? $"{done:N0}/{total:N0} {what}" : $"{done:N0} {what}")
               + (bytes > 0 ? $", {bytes / 1e9:N2} GB" : "");
    }
}
