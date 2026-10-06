namespace CodeCarver.Cli;

/// <summary>
/// The parse progress ETA. The first version divided the remaining bytes by the average rate since the start,
/// and the first files of a run are the slowest (startup, a stretch of multi-second big parses), so a healthy
/// 1-2 hour carve opened with "ETA ~4406h" and took until ~13% to settle (work eval, 1.0.159) - an estimate bad
/// enough to get a good run killed. Now:
/// <list type="bullet">
/// <item>rates are measured over rolling windows, a long one (<see cref="WindowSeconds"/>) and a short one
///   (<see cref="ShortWindowSeconds"/>), and the smaller of the two estimates is shown, so a slow stretch stops
///   counting soon after it ends (erring optimistic: a too-long estimate is the one that gets runs killed);</item>
/// <item>each estimate blends a byte rate and a file rate, since parse time has a per-file part as well as a
///   per-byte part;</item>
/// <item>no number is shown until <see cref="WarmupSeconds"/> have passed and 2% of the work is done.</item>
/// </list>
/// </summary>
public sealed class ParseEta
{
    public const double WindowSeconds = 60;
    public const double ShortWindowSeconds = 15;
    public const double WarmupSeconds = 15;
    private const double WarmupFraction = 0.02;

    private readonly List<(double T, long Bytes, int Files)> _samples = new() { (0, 0, 0) };

    /// <summary>Record progress at <paramref name="elapsed"/> seconds and return the long-window byte rate
    /// (bytes/s) and the ETA in seconds, or -1 while the estimate is still warming up.</summary>
    public (double BytesPerSecond, double EtaSeconds) Update(double elapsed, int filesDone, int filesTotal, long bytesDone, long bytesTotal)
    {
        // Keep the newest sample that is at least WindowSeconds old as the long window's start.
        while (_samples.Count > 1 && elapsed - _samples[1].T >= WindowSeconds) _samples.RemoveAt(0);
        var (rate, longEta) = Estimate(_samples[0], elapsed, filesDone, filesTotal, bytesDone, bytesTotal);
        var shortStart = _samples[0];
        foreach (var s in _samples) { if (elapsed - s.T >= ShortWindowSeconds) shortStart = s; else break; }
        var (_, shortEta) = Estimate(shortStart, elapsed, filesDone, filesTotal, bytesDone, bytesTotal);
        _samples.Add((elapsed, bytesDone, filesDone));

        var eta = longEta >= 0 && shortEta >= 0 ? Math.Min(longEta, shortEta) : Math.Max(longEta, shortEta);
        var warm = elapsed >= WarmupSeconds
                   && (bytesTotal <= 0 || bytesDone >= bytesTotal * WarmupFraction || filesDone >= filesTotal * WarmupFraction);
        return (rate, warm || filesDone >= filesTotal ? Math.Max(0, eta) : -1);
    }

    private static (double Rate, double Eta) Estimate((double T, long Bytes, int Files) from, double elapsed,
                                                      int filesDone, int filesTotal, long bytesDone, long bytesTotal)
    {
        var dt = elapsed - from.T;
        if (dt <= 0) return (0, -1);
        var rate = (bytesDone - from.Bytes) / dt;
        var byBytes = bytesDone > from.Bytes ? (bytesTotal - bytesDone) * dt / (bytesDone - from.Bytes) : -1;
        var byFiles = filesDone > from.Files ? (filesTotal - filesDone) * dt / (filesDone - from.Files) : -1;
        return (rate, byBytes >= 0 && byFiles >= 0 ? (byBytes + byFiles) / 2 : Math.Max(byBytes, byFiles));
    }
}
