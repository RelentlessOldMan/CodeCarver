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

    /// <summary>Marker file dropped at the root of every carve output. Its PRESENCE is how a later run
    /// recognizes a directory as a prior CodeCarver output that is safe to atomically replace — as opposed
    /// to a checkout, a home directory, or arbitrary user files, which must NEVER be silently destroyed.</summary>
    public const string MarkerName = ".codecarver-output";

    /// <summary>True if <paramref name="dir"/> looks like a directory CodeCarver produced (carries the
    /// marker), and is therefore safe to replace. A non-existent or empty directory is also safe (nothing
    /// to lose) but that is the caller's check; this only asserts "this is one of ours".</summary>
    public static bool IsCodeCarverOutput(string dir)
    {
        try { return File.Exists(Path.Combine(dir, MarkerName)); }
        catch { return false; }
    }

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
        // Drop the marker so the promoted --out is recognizable as a CodeCarver output next time (which is
        // what makes a re-carve into the same dir safe to auto-replace without wiping non-carve data).
        File.WriteAllText(Path.Combine(staging, MarkerName),
            $"CodeCarver output tree — created (UTC) {DateTimeOffset.UtcNow:o}.\n"
            + "This directory is an atomically-replaceable carve output; CodeCarver may overwrite it.\n");
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
        if (!Directory.Exists(_finalOut))
        {
            PromoteStaging(Dir, _finalOut); // fresh out: move staging into place
            _promoted = true;
            return;
        }

        // A prior --out exists (the CLI has already confirmed it's a CodeCarver output — carries the marker —
        // so replacing it is authorized). Preferred path: atomic swap — move it aside, move staging in.
        var backup = _finalOut + $".ccold-{_token}";
        if (Directory.Exists(backup)) TryDelete(backup);
        try
        {
            MoveWithRetry(_finalOut, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The prior --out cannot be moved aside: a process holds a file open in it, or a shell's working
            // directory is inside it (very common: `cd out && make`). A directory rename is blocked by that,
            // but overwriting individual files within it is not. Fall back to replacing its CONTENTS in place
            // — overwrite the staged files and delete the ones the new carve dropped. Not atomic (a crash
            // mid-replace can leave a mix), but it's only reached when the atomic swap physically can't run,
            // and it restores the pre-atomic "just works with a shell open in the dir" behavior (eval-#9).
            ReplaceInPlace(Dir, _finalOut);
            TryDelete(Dir);
            _promoted = true;
            return;
        }
        try
        {
            PromoteStaging(Dir, _finalOut); // staging -> final: retry, then copy-fallback
        }
        catch
        {
            // Put the prior output back so a failed promote leaves the user exactly where they started.
            if (!Directory.Exists(_finalOut))
                try { Directory.Move(backup, _finalOut); } catch { /* best effort restore */ }
            throw;
        }
        _promoted = true;
        TryDelete(backup); // prior output no longer needed
    }

    /// <summary>
    /// Replace <paramref name="dst"/>'s CONTENTS with <paramref name="src"/>'s in place (no directory move):
    /// overwrite every staged file, then delete files/dirs <paramref name="dst"/> has that the new carve
    /// dropped. Used only as the fallback when the prior --out can't be moved aside (a held file / cwd). A
    /// file in <paramref name="dst"/> held with an exclusive (no-share) lock will still fail the overwrite —
    /// that surfaces as a clean promote error, same as the pre-atomic behavior, rather than silent partial.
    /// </summary>
    private static void ReplaceInPlace(string src, string dst)
    {
        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            staged.Add(rel);
            var d = Path.Combine(dst, rel);
            var dd = Path.GetDirectoryName(d);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);
            File.Copy(f, d, overwrite: true);
        }
        foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).ToList())
            if (!staged.Contains(Path.GetRelativePath(dst, f)))
                try { File.Delete(f); } catch { /* stale file left behind if locked — best effort */ }
        // Prune now-empty directories (deepest first), best effort.
        foreach (var d in Directory.EnumerateDirectories(dst, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length).ToList())
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
    }

    /// <summary>
    /// Move <paramref name="src"/> onto <paramref name="dst"/>, retrying transient Windows failures. A
    /// directory rename fails with IOException/UnauthorizedAccessException while ANY file under it is held
    /// open — routinely by antivirus real-time scanning or the search indexer on files just written into
    /// staging (measured ~16-28% of promotes on a Defender-on box). Retry with backoff (~2.3 s total); if
    /// the rename still won't take, fall back to copying the tree in and deleting the source, which opens
    /// each file fresh and isn't blocked by a rename-only lock.
    /// </summary>
    private static void PromoteStaging(string src, string dst)
    {
        try { MoveWithRetry(src, dst); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopyTree(src, dst);
            TryDelete(src);
        }
    }

    private static void MoveWithRetry(string src, string dst)
    {
        for (var i = 0; ; i++)
        {
            try { Directory.Move(src, dst); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < 9)
            {
                System.Threading.Thread.Sleep(50 + i * 45); // 50,95,140,... ms — ~2.3 s across 10 tries
            }
        }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dst, Path.GetRelativePath(src, f)), overwrite: true);
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
