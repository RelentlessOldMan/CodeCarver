namespace CodeCarver.Core.Util;

/// <summary>
/// Reads the next files while the current one is processed. On a network drive or a virtual disk every open waits a
/// round trip, and a run reads thousands of files one after another: a work-shaped tree on a share took 108 s against
/// 10 s on a local disk, nearly all of it waiting (headers outside the root read twice, the tree read for its macros,
/// the emit copying). Results come back in the input order, so what the caller does with them doesn't change; only
/// <see cref="Window"/> results are held at a time.
/// </summary>
public static class ReadAhead
{
    /// <summary>How many reads run ahead of the one being processed.</summary>
    public const int Window = 64;

    /// <summary>Lets a window of blocking reads run at once: the thread pool otherwise starts with one thread per core
    /// and adds more slowly, so the reads queue behind each other again.</summary>
    public static void EnsureThreads()
    {
        if (_threadsSet) return;
        ThreadPool.GetMinThreads(out var workers, out var io);
        if (workers < Window + Environment.ProcessorCount) ThreadPool.SetMinThreads(Window + Environment.ProcessorCount, io);
        _threadsSet = true;
    }
    static volatile bool _threadsSet;

    /// <summary><paramref name="f"/> of every item, <see cref="Window"/> at once, each handed out as a thread frees up:
    /// one slow item (a file of hundreds of MB) holds up only its own thread, where <see cref="Map"/> waits on its
    /// oldest item before it starts another. Results by index, so the caller's order holds; for small results (all are
    /// held at once).</summary>
    public static R[] Each<T, R>(IReadOnlyList<T> items, Func<T, R> f)
    {
        Activity.Expect(items.Count);
        var results = new R[items.Count];
        For(items.Count, i => { results[i] = f(items[i]); Activity.Tick(); });
        return results;
    }

    /// <summary>Each item's index and <paramref name="f"/> of it, in order, <see cref="Window"/> running at once and handed
    /// out as a thread frees up. A result is handed back as soon as every one before it is, and let go once the caller
    /// moves on; no item starts more than <paramref name="ahead"/> past the one the caller waits on. So one slow item
    /// holds up only the items that many after it, and only that many results are ever held (<see cref="Each"/> held
    /// every result of a phase at once: the link check over a big tree outgrew the machine's memory).</summary>
    public static IEnumerable<(int Index, R Value)> Ordered<T, R>(IReadOnlyList<T> items, Func<T, R> f, int ahead = 8 * Window)
    {
        EnsureThreads();
        Activity.Expect(items.Count);
        var n = items.Count;
        var results = new R[n];
        var done = new bool[n];
        var gate = new object();
        int next = 0, cursor = 0;
        var stop = false;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failed = null;
        void Work()
        {
            while (true)
            {
                int i;
                lock (gate)
                {
                    while (!stop && next < n && next >= cursor + ahead) Monitor.Wait(gate);
                    if (stop || next >= n) return;
                    i = next++;
                }
                R r;
                try { r = f(items[i]); }
                catch (Exception ex)
                {
                    lock (gate) { failed ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); stop = true; Monitor.PulseAll(gate); }
                    return;
                }
                lock (gate) { results[i] = r; done[i] = true; Activity.Ready(1); Monitor.PulseAll(gate); }
            }
        }
        var workers = new Task[Math.Min(Window, Math.Max(1, n))];
        for (var w = 0; w < workers.Length; w++) workers[w] = Task.Run(Work);
        try
        {
            while (cursor < n)
            {
                R value;
                lock (gate)
                {
                    while (!done[cursor] && failed is null) Monitor.Wait(gate);
                    failed?.Throw();
                    value = results[cursor];
                    results[cursor] = default!;
                    Activity.Ready(-1);
                    cursor++;
                    Monitor.PulseAll(gate);
                }
                Activity.Tick();
                yield return (cursor - 1, value);
            }
        }
        finally
        {
            lock (gate) { stop = true; Monitor.PulseAll(gate); }
            Task.WaitAll(workers);
        }
    }

    /// <summary><paramref name="body"/> for 0 to <paramref name="count"/> - 1, <see cref="Window"/> at once, each index
    /// handed out as a thread frees up (Parallel.For hands a thread a range, so a slow item holds up the rest of it).</summary>
    public static void For(int count, Action<int> body)
    {
        EnsureThreads();
        try
        {
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(Enumerable.Range(0, count),
                                 System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
                             new ParallelOptions { MaxDegreeOfParallelism = Window }, body);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>Each item with <paramref name="read"/>'s result, in order, read ahead on <see cref="Window"/> threads
    /// while the caller works: up to <paramref name="ahead"/> items, and past the next one only while what is held
    /// weighs (<paramref name="weigh"/>) under <paramref name="maxWeight"/>. For reads feeding work that runs on fewer
    /// threads (the parse: one per core, each waiting its own read on a share left the cores idle). Counts nothing in
    /// <see cref="Activity"/>: the loop it feeds does.</summary>
    public static IEnumerable<(T Item, R Value)> Prefetch<T, R>(IReadOnlyList<T> items, Func<T, R> read, Func<R, long> weigh,
                                                                long maxWeight, int ahead = 8 * Window)
    {
        EnsureThreads();
        var n = items.Count;
        var results = new R[n];
        var done = new bool[n];
        var gate = new object();
        int next = 0, cursor = 0;
        long held = 0;
        var stop = false;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failed = null;
        void Work()
        {
            while (true)
            {
                int i;
                lock (gate)
                {
                    while (!stop && next < n && (next >= cursor + ahead || (next > cursor && held >= maxWeight))) Monitor.Wait(gate);
                    if (stop || next >= n) return;
                    i = next++;
                }
                R r;
                try { r = read(items[i]); }
                catch (Exception ex)
                {
                    lock (gate) { failed ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); stop = true; Monitor.PulseAll(gate); }
                    return;
                }
                var w = weigh(r);
                lock (gate) { results[i] = r; done[i] = true; held += w; Monitor.PulseAll(gate); }
            }
        }
        var workers = new Task[Math.Min(Window, Math.Max(1, n))];
        for (var w = 0; w < workers.Length; w++) workers[w] = Task.Run(Work);
        try
        {
            while (cursor < n)
            {
                R value;
                lock (gate)
                {
                    while (!done[cursor] && failed is null) Monitor.Wait(gate);
                    failed?.Throw();
                    value = results[cursor];
                    results[cursor] = default!;
                    held -= weigh(value);
                    cursor++;
                    Monitor.PulseAll(gate);
                }
                yield return (items[cursor - 1], value);
            }
        }
        finally
        {
            lock (gate) { stop = true; Monitor.PulseAll(gate); }
            Task.WaitAll(workers);
        }
    }

    /// <summary>Each item with <paramref name="read"/>'s result, in order, the next <see cref="Window"/> read on the
    /// thread pool. <paramref name="read"/> must be safe to call from several threads and should catch what it expects.</summary>
    public static IEnumerable<(T Item, R Value)> Map<T, R>(IEnumerable<T> items, Func<T, R> read, int window = Window)
    {
        EnsureThreads();
        if (items is IReadOnlyCollection<T> c) Activity.Expect(c.Count);
        var pending = new Queue<(T Item, Task<R> Value)>();
        using var e = items.GetEnumerator();
        while (true)
        {
            while (pending.Count < window && e.MoveNext())
            {
                var item = e.Current;
                pending.Enqueue((item, Task.Run(() => read(item))));
            }
            if (pending.Count == 0) yield break;
            var (i, t) = pending.Dequeue();
            Activity.Tick();
            yield return (i, t.GetAwaiter().GetResult());
        }
    }
}
