namespace CodeCarver.Core.Emit;

/// <summary>
/// Makes writing the carved tree crash-safe and non-destructive — the applicable kernel of a crash-recovery
/// policy for a batch tool (there is no editable session to autosave; the "sacred" data is the user's
/// INPUT tree and any PRIOR good <c>--out</c>).
///
/// The emitter writes many files into <c>--out</c> one at a time. Done directly, two failure modes bite:
///   (1) a run killed mid-emit leaves a HALF-WRITTEN tree (missing files — won't build); and
///   (2) re-emitting into a dir that holds a PREVIOUS carve overwrites the files it re-emits but leaves the
///       ones the new (tighter) carve dropped — a STALE, unsound tree that no longer matches the carve.
///
/// So all emit steps write into a private staging directory beside the final <c>--out</c> (same volume, so
/// promotion is a rename, not a copy). Only once every step has succeeded is the tree promoted: the prior
/// <c>--out</c> is moved aside, staging is moved into place, and only THEN is the old copy deleted. A crash
/// before <see cref="Promote"/> leaves the prior <c>--out</c> completely untouched and the incomplete
/// staging dir orphaned (disposal deletes it on a handled failure). The prior good output is never destroyed
/// before the new one is complete (spec §11/§41/§61).
/// </summary>
public sealed class StagedOutput : IDisposable
{
    /// <summary>The directory all emit steps should write into (stand-in for the final <c>--out</c>).</summary>
    public string Dir { get; }

    private readonly string _finalOut;   // resolved full path of the user's --out
    private readonly string _token;
    private bool _promoted;
    private bool _disposed;

    private StagedOutput(string dir, string finalOut, string token)
    {
        Dir = dir;
        _finalOut = finalOut;
        _token = token;
    }

    /// <summary>
    /// Begin a staged emit for the given output directory. Creates an empty staging directory as a sibling
    /// of <paramref name="outDir"/> (same volume) and returns a handle whose <see cref="Dir"/> the caller
    /// emits into. The final <paramref name="outDir"/> is not touched until <see cref="Promote"/>.
    /// </summary>
    public static StagedOutput Begin(string outDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        var finalOut = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        var parent = Path.GetDirectoryName(finalOut)
                     ?? throw new ArgumentException($"--out '{outDir}' has no parent directory", nameof(outDir));
        Directory.CreateDirectory(parent); // the final tree's parent must exist for the promote rename
        var token = Guid.NewGuid().ToString("N")[..8];
        var staging = Path.Combine(parent, $".ccstaging-{Path.GetFileName(finalOut)}-{token}");
        // Extremely unlikely, but never emit onto a pre-existing dir we didn't just make.
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        return new StagedOutput(staging, finalOut, token);
    }

    /// <summary>
    /// Atomically replace the final output with the staged tree. The prior output (if any) is moved to a
    /// sibling backup first; only after the staged tree is successfully in place is the backup removed. If
    /// moving staging into place fails, the prior output is restored. Idempotent-safe: throws if called
    /// after disposal.
    /// </summary>
    public void Promote()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? backup = null;
        if (Directory.Exists(_finalOut))
        {
            backup = _finalOut + $".ccold-{_token}";
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Directory.Move(_finalOut, backup);
        }
        try
        {
            Directory.Move(Dir, _finalOut);
        }
        catch
        {
            // Put the prior output back so a failed promote leaves the user exactly where they started.
            if (backup is not null && !Directory.Exists(_finalOut)) Directory.Move(backup, _finalOut);
            throw;
        }
        _promoted = true;
        if (backup is not null) TryDelete(backup); // prior output no longer needed
    }

    /// <summary>Deletes the staging directory if the emit was never promoted (a handled failure/early
    /// return). After a successful <see cref="Promote"/> there is nothing to clean up. Never throws.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_promoted) TryDelete(Dir);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
