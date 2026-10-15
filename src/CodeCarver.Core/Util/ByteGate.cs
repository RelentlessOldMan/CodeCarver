namespace CodeCarver.Core.Util;

/// <summary>
/// A budget of bytes for the work running at once: a piece of work takes its size before it starts and gives it back when
/// done, and waits while the others hold too much. One piece bigger than the whole budget still runs, alone. A scan of a
/// file holds many times the file's size while it runs, so a loop of <see cref="ReadAhead.Window"/> scans at once over big
/// register headers held tens of GB (a work run's link check passed 20 GB); small files never wait.
/// </summary>
public sealed class ByteGate
{
    readonly object _gate = new();
    readonly long _limit;
    long _used;

    public ByteGate(long limit) => _limit = limit;

    /// <summary>Times a piece of work waited for room.</summary>
    public long Waits => Interlocked.Read(ref _waits);
    long _waits;

    /// <summary>Takes <paramref name="bytes"/> of the budget, waiting until they fit (or nothing else holds any).</summary>
    public Held Take(long bytes)
    {
        if (bytes <= 0) return default;
        lock (_gate)
        {
            if (_used > 0 && _used + bytes > _limit) Interlocked.Increment(ref _waits);
            while (_used > 0 && _used + bytes > _limit) Monitor.Wait(_gate);
            _used += bytes;
        }
        return new Held(this, bytes);
    }

    void Give(long bytes)
    {
        lock (_gate) { _used -= bytes; Monitor.PulseAll(_gate); }
    }

    /// <summary>Bytes taken from a gate, given back on <see cref="Dispose"/>.</summary>
    public readonly struct Held : IDisposable
    {
        readonly ByteGate? _from;
        readonly long _bytes;
        internal Held(ByteGate from, long bytes) { _from = from; _bytes = bytes; }
        public void Dispose() => _from?.Give(_bytes);
    }
}
