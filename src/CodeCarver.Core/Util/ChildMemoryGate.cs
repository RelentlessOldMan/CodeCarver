using System.Diagnostics;

namespace CodeCarver.Core.Util;

/// <summary>
/// Holds the child processes running at once (pp-trace, one per compile) to a budget of memory, by what they hold:
/// their memory isn't the carve's, so memoryStopMb never sees it. One pp-trace holds about 0.5 KB per define of the
/// headers its compile includes (2 GB for 3.8M defines), and a carve ran one per core.
///
/// What a child will need isn't known before it runs, so the gate measures. Children run one at a time at first, and
/// one more may run at once for each that finishes: a small compile that finished before it was ever measured says
/// nothing about the big ones. A new child starts only when the running ones, each counted at what it holds or at
/// what a child is expected to need if more (the most any finished child held, or any running one holds), plus that
/// again for each one starting, fit. If the running children grow past the budget, the newest are stopped until the
/// rest fit, and still count until they have exited; each caller runs its child again later
/// (<see cref="Lease.Evicted"/>). The oldest always runs on, so the work ends: one child that alone needs more than
/// the budget runs alone, as one compile of the build needed as much.
/// </summary>
public sealed class ChildMemoryGate : IDisposable
{
    readonly long _budget;
    readonly int _maxRunning;
    readonly object _gate = new();
    readonly List<Lease> _running = new();
    readonly List<Lease> _stopping = new();   // stopped, still holding their memory until they exit
    readonly Timer _sampler;
    int _starting;
    long _order;
    long _biggest;   // the most any finished child held
    int _cap = 1;    // children allowed at once: one more for each that finished, back to what fit when one is stopped

    /// <summary>The most one child held, as sampled.</summary>
    public long PeakChildBytes { get; private set; }
    /// <summary>The most the running children held together, as sampled.</summary>
    public long PeakTotalBytes { get; private set; }
    /// <summary>The most children that ran at once.</summary>
    public int PeakRunning { get; private set; }
    /// <summary>Children stopped because the running ones grew past the budget.</summary>
    public int Evictions { get; private set; }
    /// <summary>Children that held more than the whole budget alone.</summary>
    public int OverBudgetAlone { get; private set; }
    public long Budget => _budget;

    public ChildMemoryGate(long budgetBytes, int maxRunning, int sampleMs = 100)
    {
        _budget = Math.Max(1, budgetBytes);
        _maxRunning = Math.Max(1, maxRunning);
        _sampler = new Timer(_ => Sample(), null, sampleMs, sampleMs);
    }

    /// <summary>The budget for a run's children: the run's limit (memoryStopMb, or the machine's memory when less),
    /// less what the run itself holds now, and never under 1 MB.</summary>
    public static long BudgetForRun()
    {
        long own;
        try { using var me = Process.GetCurrentProcess(); own = me.PrivateMemorySize64; } catch { own = 0; }
        var limit = MemoryGate.BudgetBytes();
        return Math.Max(1 << 20, limit - own);
    }

    /// <summary>Waits until another child may start; then start it and <see cref="Lease.Track"/> it.</summary>
    public Lease Admit()
    {
        lock (_gate)
        {
            while (_running.Count + _starting + _stopping.Count > 0
                   && (_running.Count + _starting >= Math.Min(_maxRunning, _cap) || Reserved() + (_starting + 1) * Expected() > _budget))
                Monitor.Wait(_gate, 250);
            _starting++;
            return new Lease(this, ++_order);
        }
    }

    // What a new child is taken to need: the most any finished child held, or a running one holds if more. A small
    // compile that finished first doesn't make the big ones running beside it look small.
    long Expected()
    {
        var most = _biggest;
        foreach (var l in _running) most = Math.Max(most, l.Held);
        return most;
    }

    // A child starts small and grows: one that just started holds little but will likely need what the biggest did.
    long Reserved()
    {
        var expected = Expected();
        long sum = 0;
        foreach (var l in _running) sum += Math.Max(l.Held, expected);
        foreach (var l in _stopping) sum += l.Held;
        return sum;
    }

    static void Measure(Lease l)
    {
        try
        {
            l.Process!.Refresh();
            l.Held = Math.Max(l.Held, l.Process.PrivateMemorySize64);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* exited */ }
        l.Peak = Math.Max(l.Peak, l.Held);
    }

    void Sample()
    {
        var stop = new List<Lease>();
        lock (_gate)
        {
            if (_running.Count + _stopping.Count == 0) return;
            long sum = 0;
            foreach (var l in _running.Concat(_stopping))
            {
                Measure(l);
                sum += l.Held;
                PeakChildBytes = Math.Max(PeakChildBytes, l.Held);
            }
            PeakTotalBytes = Math.Max(PeakTotalBytes, sum);
            // Over: stop the newest until the rest fit, all in this sample; the oldest runs on.
            while (sum > _budget && _running.Count > 1)
            {
                var l = _running.MaxBy(x => x.Order)!;
                _running.Remove(l);
                _stopping.Add(l);
                l.Evicted = true;
                Evictions++;
                stop.Add(l);
                sum -= l.Held;
                _cap = Math.Max(1, _running.Count);
            }
        }
        foreach (var l in stop) try { l.Process!.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    void Started(Lease l)
    {
        lock (_gate)
        {
            _starting--;
            _running.Add(l);
            PeakRunning = Math.Max(PeakRunning, _running.Count);
        }
    }

    void Done(Lease l)
    {
        lock (_gate)
        {
            if (l.Process is null) _starting--;
            else if (!_running.Remove(l)) _stopping.Remove(l);
            if (!l.Evicted)
            {
                _biggest = Math.Max(_biggest, l.Peak);
                _cap = Math.Min(_maxRunning, _cap + 1);
                if (l.Peak > _budget) OverBudgetAlone++;
            }
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose() => _sampler.Dispose();

    /// <summary>One child's place: <see cref="Track"/> it once started, dispose when it has exited.</summary>
    public sealed class Lease : IDisposable
    {
        readonly ChildMemoryGate _owner;
        int _done;
        internal readonly long Order;
        internal Process? Process;
        internal long Held, Peak;
        internal Lease(ChildMemoryGate owner, long order) { _owner = owner; Order = order; }

        /// <summary>The gate stopped this child to stay within its budget: run it again.</summary>
        public bool Evicted { get; internal set; }

        public void Track(Process p) { Process = p; _owner.Started(this); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _owner.Done(this);
        }
    }
}
