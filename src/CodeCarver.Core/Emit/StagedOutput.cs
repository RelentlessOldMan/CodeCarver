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

    /// <summary>Marker file dropped in the output AREA (the parent that holds the carved tree beside the
    /// <c>codecarver/</c> metadata), NOT inside the carved tree itself — so the carved tree stays a clean,
    /// buildable project with no stray dotfile. Its PRESENCE is how a later run recognizes a directory as a
    /// prior CodeCarver output that is safe to atomically replace — as opposed to a checkout, a home
    /// directory, or arbitrary user files, which must NEVER be silently destroyed.</summary>
    public const string MarkerName = ".codecarver-output";

    /// <summary>True if <paramref name="dir"/> is a CodeCarver output AREA (carries the marker), and is
    /// therefore safe to replace. Pass the directory that HOLDS the carved tree (the parent), not the carved
    /// tree itself. A non-existent or empty directory is also safe (nothing to lose) but that is the caller's
    /// check; this only asserts "this is one of ours".</summary>
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
        // Reap orphans from prior runs that were HARD-killed (Ctrl-C / power loss / OOM kill) before Dispose
        // could clean up: a staging or backup sibling for THIS same --out. Left alone they accumulate a full
        // tree copy each. Only our own name-prefixed siblings for this exact output are touched.
        ReapOrphans(parent, Path.GetFileName(finalOut));

        var token = Guid.NewGuid().ToString("N")[..8];
        var staging = Path.Combine(parent, $".ccstaging-{Path.GetFileName(finalOut)}-{token}");
        // Extremely unlikely, but never emit onto a pre-existing dir we didn't just make.
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        // Drop the marker in the output AREA (the parent), NOT inside the staged tree — so the promoted carved
        // tree stays clean while the area is still recognizable as a CodeCarver output next time (what makes a
        // re-carve into the same place safe to auto-replace without wiping non-carve data). Best-effort: a
        // failure to write it must never sink the carve.
        try
        {
            File.WriteAllText(Path.Combine(parent, MarkerName),
                $"CodeCarver output area — created (UTC) {DateTimeOffset.UtcNow:o}.\n"
                + "The carved tree beside this marker is an atomically-replaceable carve output; CodeCarver may overwrite it.\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* marker is advisory */ }
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
        ClearReadOnlyTree(Dir); // carved output must be writable (Perforce read-only sources; in-place fallback; backup deletion)
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
        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
        var staged = new HashSet<string>(files.Select(f => Path.GetRelativePath(src, f)), StringComparer.OrdinalIgnoreCase);

        // PRE-FLIGHT: every existing destination we'll overwrite must be openable for write BEFORE we mutate
        // anything, so a persistent lock bails with the prior tree genuinely UNCHANGED (not half-replaced —
        // the torn tree eval-#10 flagged). ReadOnly is cleared here too (Perforce). Best-effort: a lock that
        // appears only AFTER this check is caught in the write loop below and reported as torn.
        foreach (var rel in staged)
        {
            var d = Path.Combine(dst, rel);
            if (!File.Exists(d)) continue;
            ClearReadOnly(d);
            try { using var _ = new FileStream(d, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PromoteFailedException(
                    $"a file in the output is locked ('{rel}') — output left unchanged", torn: false, ex);
            }
        }

        // WRITE: past pre-flight this should fully succeed; a failure now is a race -> the tree is TORN.
        foreach (var rel in staged)
        {
            var d = Path.Combine(dst, rel);
            var dd = Path.GetDirectoryName(d);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);
            try { File.Copy(Path.Combine(src, rel), d, overwrite: true); ClearReadOnly(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PromoteFailedException(
                    $"output is now PARTIALLY updated (writing '{rel}' failed) — re-run or use a fresh --out", torn: true, ex);
            }
        }

        // DELETE files the new carve dropped. Count any that can't be removed (held open) — leaving one is a
        // stale/unsound tree, so surface it rather than exit 0 silently (eval-#10).
        var staleLeft = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).ToList())
            if (!staged.Contains(Path.GetRelativePath(dst, f)))
            {
                ClearReadOnly(f);
                try { File.Delete(f); } catch { staleLeft.Add(Path.GetRelativePath(dst, f)); }
            }
        foreach (var d in Directory.EnumerateDirectories(dst, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length).ToList())
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }

        if (staleLeft.Count > 0)
            throw new PromoteFailedException(
                $"output updated, but {staleLeft.Count} file(s) the carve dropped could not be removed (held open) "
                + $"— the tree may be STALE/unsound: {string.Join(", ", staleLeft.Take(5))}"
                + (staleLeft.Count > 5 ? ", …" : ""), torn: true);
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
        {
            var d = Path.Combine(dst, Path.GetRelativePath(src, f));
            File.Copy(f, d, overwrite: true);
            ClearReadOnly(d); // keep the carved output writable/editable
        }
    }

    /// <summary>Deletes the staging directory if the emit was never promoted (a handled failure/early
    /// return). After a successful <see cref="Promote"/> there is nothing to clean up. Never throws.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_promoted) TryDelete(Dir);
    }

    /// <summary>Delete any <c>.ccstaging-&lt;name&gt;-*</c> / <c>&lt;name&gt;.ccold-*</c> siblings in
    /// <paramref name="parent"/> left by a prior run for this exact output that was hard-killed before it
    /// could clean up. Scoped to this output's name prefix so it can never touch unrelated files. Never throws.</summary>
    private static void ReapOrphans(string parent, string finalName)
    {
        try
        {
            var stagingPrefix = $".ccstaging-{finalName}-";
            var backupPrefix = $"{finalName}.ccold-";
            foreach (var d in Directory.EnumerateDirectories(parent))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(stagingPrefix, StringComparison.Ordinal) ||
                    name.StartsWith(backupPrefix, StringComparison.Ordinal))
                    TryDelete(d);
            }
        }
        catch { /* best effort */ }
    }

    private static void TryDelete(string dir)
    {
        // Clear ReadOnly first: Perforce-synced sources are read-only, the emitter copies them read-only, and
        // Directory.Delete then fails on them — leaking a full .ccold/staging copy per re-carve (eval-#10).
        try
        {
            if (!Directory.Exists(dir)) return;
            ClearReadOnlyTree(dir);
            Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
        catch { /* best effort */ }
    }

    /// <summary>Clear the ReadOnly attribute on every file under <paramref name="dir"/>. A carved tree is
    /// meant to be edited/built, and read-only outputs also break the in-place fallback and backup deletion.</summary>
    private static void ClearReadOnlyTree(string dir)
    {
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) ClearReadOnly(f); }
        catch { /* best effort */ }
    }
}

/// <summary>Thrown when the staged tree could not be put fully into place. <see cref="Torn"/> distinguishes
/// "output left UNCHANGED" (false — a lock was hit before/at the atomic swap, or the in-place pre-flight
/// bailed before touching anything) from "output is now PARTIALLY updated / possibly stale" (true — an
/// in-place write or stale-file removal failed after mutation began). Callers message accordingly.</summary>
public sealed class PromoteFailedException : IOException
{
    public bool Torn { get; }
    public PromoteFailedException(string message, bool torn, Exception? inner = null) : base(message, inner) => Torn = torn;
}
