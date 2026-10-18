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
    static long _inFlight, _largest;   // bytes of the items being worked on now; the largest item started in the step
    static bool _summed;   // the total is what the step's loops expect, added up

    /// <summary>Starts counting a new step: <paramref name="what"/> names what is counted ("files copied").</summary>
    public static void Begin(string what = "files", long total = -1)
    {
        lock (Gate) { _what = what; _total = total; _summed = false; Interlocked.Exchange(ref _done, 0); Interlocked.Exchange(ref _bytes, 0); Interlocked.Exchange(ref _largest, 0); _parts.Clear(); Interlocked.Exchange(ref _ready, 0); }
    }

    // ---- what the memory goes to, per part of a step's work (C20/C21: 9 GB in 90 s with 0.5 MB of files in hand) ----

    sealed class PartStats { public long Allocated, MaxItem, InFlight, Items, Notes, NoteBytes, Ticks, MaxTicks; }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PartStats> _parts = new(StringComparer.Ordinal);
    static long _ready;
    [ThreadStatic] static long t_notes, t_noteBytes;


    /// <summary>Counts what the calling thread allocates until the scope is disposed against <paramref name="part"/>,
    /// with how many items of the part are running at once. Nested scopes each count their own (an outer one includes
    /// an inner one's).</summary>
    public static PartScope Part(string part, long itemBytes = 0) => new(part, itemBytes);

    /// <summary>Something the current part's item pulled in (a header a walk read): counted with the item.</summary>
    public static void Note(long bytes) { t_notes++; t_noteBytes += bytes; }

    /// <summary>Results finished ahead of the loop consuming them (<see cref="ReadAhead"/>), held until it gets there.</summary>
    public static void Ready(int delta) => Interlocked.Add(ref _ready, delta);

    /// <summary>Forgets the parts counted so far: a loop that reported them hands the next a clean count.</summary>
    public static void ClearParts() => _parts.Clear();

    public readonly struct PartScope : IDisposable
    {
        readonly PartStats _s; readonly long _start, _notes, _noteBytes, _t0;
        internal PartScope(string name, long itemBytes)
        {

            _s = _parts.GetOrAdd(name, _ => new PartStats());
            Interlocked.Increment(ref _s.InFlight);
            _notes = t_notes; _noteBytes = t_noteBytes;
            _start = GC.GetAllocatedBytesForCurrentThread();
            _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_s is null) return;
            var used = GC.GetAllocatedBytesForCurrentThread() - _start;
            Interlocked.Add(ref _s.Allocated, used);
            Interlocked.Increment(ref _s.Items);
            var took = System.Diagnostics.Stopwatch.GetTimestamp() - _t0;
            Interlocked.Add(ref _s.Ticks, took);
            for (var was = Interlocked.Read(ref _s.MaxTicks); took > was; was = Interlocked.Read(ref _s.MaxTicks))
                if (Interlocked.CompareExchange(ref _s.MaxTicks, took, was) == was) break;
            Interlocked.Add(ref _s.Notes, t_notes - _notes);
            Interlocked.Add(ref _s.NoteBytes, t_noteBytes - _noteBytes);
            for (var was = Interlocked.Read(ref _s.MaxItem); used > was; was = Interlocked.Read(ref _s.MaxItem))
                if (Interlocked.CompareExchange(ref _s.MaxItem, used, was) == was) break;
            Interlocked.Decrement(ref _s.InFlight);
        }
    }

    /// <summary>The step's parts so far: part.&lt;name&gt;.allocatedMb, .itemMaxMb, .items, .inFlight, .pulledIn,
    /// .pulledInMb, .ms, .itemMaxMs, and readyAhead.</summary>
    public static IEnumerable<KeyValuePair<string, long>> Parts()
    {
        foreach (var (name, s) in _parts.ToArray())
        {
            yield return new($"part.{name}.allocatedMb", Interlocked.Read(ref s.Allocated) >> 20);
            yield return new($"part.{name}.itemMaxMb", Interlocked.Read(ref s.MaxItem) >> 20);
            yield return new($"part.{name}.items", Interlocked.Read(ref s.Items));
            yield return new($"part.{name}.inFlight", Interlocked.Read(ref s.InFlight));
            yield return new($"part.{name}.pulledIn", Interlocked.Read(ref s.Notes));
            yield return new($"part.{name}.pulledInMb", Interlocked.Read(ref s.NoteBytes) >> 20);
            // Time inside the part, summed over its items on every thread (a loop of 8 threads busy 1 min reads 8 min).
            yield return new($"part.{name}.ms", Interlocked.Read(ref s.Ticks) * 1000 / System.Diagnostics.Stopwatch.Frequency);
            yield return new($"part.{name}.itemMaxMs", Interlocked.Read(ref s.MaxTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency);
        }
        yield return new("readyAhead", Interlocked.Read(ref _ready));
    }

    /// <summary>A loop of <paramref name=total/> items starts: the step's total is what its loops expect, added up, unless
    /// the step set one. (Only the first loop's used to count, so a step of several loops showed 21,930 of 9,934.)</summary>
    public static void Expect(long total)
    {
        lock (Gate)
        {
            if (_total < 0 && Interlocked.Read(ref _done) == 0) { _total = total; _summed = true; }
            else if (_summed) _total += total;
        }
    }

    /// <summary>One more item done, of <paramref name="bytes"/> bytes when known.</summary>
    public static void Tick(long bytes = 0)
    {
        Interlocked.Increment(ref _done);
        if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
    }

    /// <summary>An item of <paramref name="bytes"/> bytes is being worked on until the result is disposed: a run stopped
    /// for memory reports what was in hand (<see cref="InHand"/>).</summary>
    public static IDisposable Working(long bytes)
    {
        Interlocked.Add(ref _inFlight, bytes);
        for (var was = Interlocked.Read(ref _largest); bytes > was; was = Interlocked.Read(ref _largest))
            if (Interlocked.CompareExchange(ref _largest, bytes, was) == was) break;
        return new Release(bytes);
    }

    sealed class Release(long bytes) : IDisposable
    {
        int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Add(ref _inFlight, -bytes); }
    }

    /// <summary>Bytes of the items being worked on now, and the largest item started in this step.</summary>
    public static (long InFlight, long Largest) InHand => (Interlocked.Read(ref _inFlight), Interlocked.Read(ref _largest));

    /// <summary>Items done in this step so far.</summary>
    public static long Done => Interlocked.Read(ref _done);

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
