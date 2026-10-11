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

    /// <summary>Each item with <paramref name="read"/>'s result, in order, the next <see cref="Window"/> read on the
    /// thread pool. <paramref name="read"/> must be safe to call from several threads and should catch what it expects.</summary>
    public static IEnumerable<(T Item, R Value)> Map<T, R>(IEnumerable<T> items, Func<T, R> read, int window = Window)
    {
        EnsureThreads();
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
            yield return (i, t.GetAwaiter().GetResult());
        }
    }
}
