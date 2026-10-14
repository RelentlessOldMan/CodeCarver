namespace CodeCarver.Frontend;

/// <summary>
/// Holds the parses running at once to a share of the machine's memory. tree-sitter's tree of a file with parse errors
/// takes ~150 bytes per character of source (an 8 MB file: 1.25 GB; our own data for it under 100 MB), so one parse per
/// core over a run of big files asked for more memory than the machine had. A file waits until the parses before it
/// leave room; one bigger than the whole budget runs alone. Only when parses run, never what they find, depends on it.
/// </summary>
internal sealed class ParseMemory
{
    /// <summary>Memory a parse is counted as holding per character of its text.</summary>
    public const long BytesPerChar = 160;

    /// <summary>Files under this many characters are not counted: their trees are small.</summary>
    public const int MinChars = 64 * 1024;

    private readonly long _budget;
    private long _inUse;
    private readonly object _gate = new();

    public ParseMemory(long budgetBytes) => _budget = Math.Max(1, budgetBytes);

    /// <summary>A quarter of the memory the machine has, or CODECARVER_PARSE_MEMORY_MB. Not more: what a tree frees stays
    /// with the process (the native heap keeps it for the next parse), so the most the parses ever held is held for
    /// the rest of the run, and the graph and the check need the rest.</summary>
    public static ParseMemory ForMachine()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_PARSE_MEMORY_MB"), out var mb) && mb > 0)
            return new ParseMemory(mb * 1024 * 1024);
        long total;
        try { total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch (InvalidOperationException) { total = 0; }
        return new ParseMemory(total > 0 ? total / 4 : 4L << 30);
    }

    public long Budget => _budget;

    /// <summary>Waits for room for a parse of <paramref name="chars"/> characters; the lease gives it back.</summary>
    public Lease Acquire(int chars)
    {
        if (chars < MinChars) return new Lease(this, 0);
        var cost = Math.Min(_budget, chars * BytesPerChar);
        lock (_gate)
        {
            while (_inUse > 0 && _inUse + cost > _budget) Monitor.Wait(_gate);
            _inUse += cost;
        }
        return new Lease(this, cost);
    }

    private void Give(long cost)
    {
        if (cost == 0) return;
        lock (_gate)
        {
            _inUse -= cost;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>One parse's share; released once, by whoever frees the tree.</summary>
    public sealed class Lease
    {
        private readonly ParseMemory _owner;
        private long _cost;
        internal Lease(ParseMemory owner, long cost) { _owner = owner; _cost = cost; }
        public void Release() => _owner.Give(Interlocked.Exchange(ref _cost, 0));
    }
}
