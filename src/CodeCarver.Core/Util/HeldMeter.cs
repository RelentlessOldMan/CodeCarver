using System.Collections.Concurrent;

namespace CodeCarver.Core.Util;

/// <summary>
/// What each part of a run holds right now, by its own count: a part registers a source of numbers while it runs, and
/// the heartbeat and a memory stop read them all (held.&lt;part&gt;.&lt;what&gt;). A peak alone said how much memory a
/// run had, never what had it, and each memory stop at work took a round of guessing (1.0.317-320).
/// </summary>
public static class HeldMeter
{
    static readonly ConcurrentDictionary<string, Func<IEnumerable<KeyValuePair<string, long>>>> s_sources = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="source"/> under <paramref name="part"/> until the result is disposed. It is
    /// read from another thread while the part runs, so it reads counters and counts, never walks a table that is
    /// changing.</summary>
    public static IDisposable Register(string part, Func<IEnumerable<KeyValuePair<string, long>>> source)
    {
        s_sources[part] = source;
        return new Registration(part, source);
    }

    /// <summary>Every registered part's numbers, as held.&lt;part&gt;.&lt;what&gt;. A source that fails is skipped:
    /// a report must never take the run down.</summary>
    public static List<KeyValuePair<string, long>> Now()
    {
        var all = new List<KeyValuePair<string, long>>();
        foreach (var (part, source) in s_sources.ToArray())
        {
            try { foreach (var (k, v) in source()) all.Add(new($"held.{part}.{k}", v)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { all.Add(new($"held.{part}.unreadable", 1)); }
        }
        return all;
    }

    /// <summary>One line for the heartbeat: the numbers in MB (keys ending Mb) first, largest first.</summary>
    public static string? Describe()
    {
        var now = Now();
        if (now.Count == 0) return null;
        return string.Join(", ", now.OrderByDescending(kv => kv.Key.EndsWith("Mb", StringComparison.Ordinal))
                                    .ThenByDescending(kv => kv.Value)
                                    .Select(kv => $"{kv.Key["held.".Length..]} {kv.Value:N0}"));
    }

    sealed class Registration(string part, Func<IEnumerable<KeyValuePair<string, long>>> source) : IDisposable
    {
        public void Dispose() => s_sources.TryRemove(new KeyValuePair<string, Func<IEnumerable<KeyValuePair<string, long>>>>(part, source));
    }
}
