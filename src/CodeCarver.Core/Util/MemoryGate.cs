namespace CodeCarver.Core.Util;

/// <summary>
/// Holds the work running at once to a budget of memory, each piece counted at a cost per character of its text. A
/// piece waits until the ones before it leave room; one bigger than the whole budget runs alone. Only when work runs,
/// never what it finds, depends on it. Shared by the parses (tree-sitter's trees) and the link check's scans.
/// </summary>
public class MemoryGate
{
    readonly long _budget;
    readonly long _bytesPerChar;
    readonly int _minChars;
    long _inUse;
    readonly object _gate = new();

    /// <param name="bytesPerChar">Memory a piece is counted as holding per character of its text.</param>
    /// <param name="minChars">Pieces under this many characters are not counted: they are small.</param>
    public MemoryGate(long budgetBytes, long bytesPerChar, int minChars)
    {
        _budget = Math.Max(1, budgetBytes);
        _bytesPerChar = bytesPerChar;
        _minChars = minChars;
    }

    /// <summary>The memory the machine has (what the GC sees), or 0 when it can't tell.</summary>
    public static long MachineBytes()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>The most the run may hold (memoryStopMb), or 0 for no limit: set once, before any budget is taken.</summary>
    public static long RunLimitBytes { get; set; }

    /// <summary>What the budgets are fractions of: the machine's memory, or the run's limit when that is less. A run
    /// told to stop at 20 GB on a 128 GB machine had its scans sized for 128 (C19: stopped in the check).</summary>
    public static long BudgetBytes()
    {
        var machine = MachineBytes();
        var limit = RunLimitBytes;
        return limit <= 0 ? machine : machine <= 0 ? limit : Math.Min(machine, limit);
    }

    public long Budget => _budget;

    /// <summary>How long pieces waited for room, in Stopwatch ticks summed over the threads, and how many waited.</summary>
    public long WaitTicks { get { lock (_gate) return _waitTicks; } }
    public long Waits { get { lock (_gate) return _waits; } }
    long _waitTicks, _waits;

    /// <summary>Waits for room for a piece of <paramref name="chars"/> characters; the lease gives it back.</summary>
    public Lease Acquire(int chars)
    {
        if (chars < _minChars) return new Lease(this, 0);
        var cost = Math.Min(_budget, chars * _bytesPerChar);
        lock (_gate)
        {
            if (_inUse > 0 && _inUse + cost > _budget)
            {
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                while (_inUse > 0 && _inUse + cost > _budget) Monitor.Wait(_gate);
                _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _waits++;
            }
            _inUse += cost;
        }
        return new Lease(this, cost);
    }

    void Give(long cost)
    {
        if (cost == 0) return;
        lock (_gate)
        {
            _inUse -= cost;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>One piece's share; released once, by whoever is done with it.</summary>
    public sealed class Lease : IDisposable
    {
        readonly MemoryGate _owner;
        long _cost;
        internal Lease(MemoryGate owner, long cost) { _owner = owner; _cost = cost; }
        public void Release() => _owner.Give(Interlocked.Exchange(ref _cost, 0));
        public void Dispose() => Release();
    }
}
