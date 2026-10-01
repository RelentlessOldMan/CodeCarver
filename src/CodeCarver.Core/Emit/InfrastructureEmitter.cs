namespace CodeCarver.Core.Emit;

/// <summary>Outcome of the keep-by-default infrastructure pass: how many non-code files were passed
/// through verbatim, their total bytes, the relative paths (sorted), any warnings, and the files dropped as
/// garbage (VCS/scratch/editor/coverage — see <see cref="InfraClassifier.IsGarbage"/>) with their byte total.</summary>
public readonly record struct InfraEmitResult(
    int Count, long Bytes, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Garbage, long GarbageBytes);

/// <summary>
/// The keep-by-default pass that makes <c>--out</c> a COMPLETE, buildable project rather than just the
/// carved C. <see cref="FileTreeEmitter"/> writes the reachable code and its <c>#include</c> closure;
/// this pass then copies — verbatim — every OTHER file in the source tree, because a real image needs
/// far more than its C to build: Makefiles/CMake, linker scripts and scatter/<c>.cmd</c> files, startup
/// assembly, device trees, register/data tables, TRACE32 <c>.cmm</c>, prebuilt <c>.a</c>/<c>.o</c>,
/// board configs. The carve never modelled these (they aren't translation units), so an allowlist of
/// "known" build extensions always misses something and the emitted tree won't link.
///
/// The rule is EVIDENCE-BASED REMOVAL ONLY: a file is omitted only when we can prove we don't need it —
/// it is either (a) already emitted (kept code + its include closure), (b) a code file the carve modelled
/// and found unreachable (<c>droppedCodeFilesRel</c>, dead translation units / unreferenced headers), or
/// (c) GARBAGE — a file that cannot be a build/run input by universal convention (VCS metadata, compiler/IDE
/// scratch, dep/coverage artifacts, editor/OS junk, logs/temp; see <see cref="InfraClassifier.IsGarbage"/>),
/// dropped by default unless <c>pruneGarbage</c> is false or <c>--aux</c> forces a specific file back.
/// Everything else is kept. The result is a superset that builds; <c>--exclude</c> is the knob for trimming
/// board/arch variants or large non-build trees.
///
/// Write-safety mirrors the other emitters: confined under <c>--out</c>, never copies a file onto its own
/// source, and best-effort on a locked/vanished file (warn, skip) so one bad file can't sink the emit.
/// </summary>
public static class InfrastructureEmitter
{
    public static InfraEmitResult Copy(
        string sourceRoot, string outDir,
        IReadOnlyCollection<string> alreadyEmittedRel,
        IReadOnlyCollection<string> droppedCodeFilesRel,
        IReadOnlyList<string> excludeDirs,
        IReadOnlyList<string> auxGlobs,
        bool pruneGarbage = true,
        IReadOnlyCollection<string>? observed = null)
    {
        var warnings = new List<string>();
        var outFull = Path.GetFullPath(outDir);
        var files = new List<string>();
        var garbage = new List<string>();
        long bytes = 0;
        long garbageBytes = 0;
        var count = 0;

        foreach (var (rel, full, isGarbage) in Select(sourceRoot, alreadyEmittedRel, droppedCodeFilesRel, excludeDirs, auxGlobs, pruneGarbage, observed, warnings))
        {
            // Garbage (VCS/scratch/editor/coverage) is the ONLY thing dropped without evidence from the carve, and
            // only when pruneGarbage is on and --aux didn't force it back. Record it (for the report) and skip the
            // copy. One stat for the byte total — bounded by the walk we're already doing.
            if (isGarbage)
            {
                try { garbageBytes += new FileInfo(full).Length; } catch { /* vanished/locked: count the file, skip bytes */ }
                garbage.Add(rel);
                continue;
            }
            var dst = Path.Combine(outDir, rel);
            var dstFull = Path.GetFullPath(dst);
            // Belt-and-braces: NEVER write outside --out, whatever the rel path resolved to.
            if (!dstFull.StartsWith(outFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dstFull, outFull, StringComparison.OrdinalIgnoreCase))
            { warnings.Add($"skipped '{rel}': destination would fall outside --out"); continue; }
            // out overlapping source would make dst==src; File.Copy onto itself throws. The CLI refuses
            // overlapping --out, but skip defensively for direct library callers.
            if (string.Equals(full, dstFull, StringComparison.OrdinalIgnoreCase)) continue;

            var dd = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);
            try { File.Copy(full, dst, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"could not copy '{rel}' ({ex.GetType().Name}) — skipped"); continue; }
            bytes += new FileInfo(dst).Length;
            files.Add(rel);
            count++;
        }
        files.Sort(StringComparer.Ordinal);
        garbage.Sort(StringComparer.Ordinal);
        return new InfraEmitResult(count, bytes, files, warnings, garbage, garbageBytes);
    }

    /// <summary>The shared keep-by-default selection: every file under <paramref name="sourceRoot"/> that is
    /// neither already-emitted nor modelled-dead code, honoring <c>--exclude</c> (overridable per-file by
    /// <c>--aux</c>). Yields (relPath, fullPath, isGarbage); the Garbage flag marks a provable non-input
    /// (<see cref="InfraClassifier.IsGarbage"/>) that <c>--aux</c> did NOT force back, when
    /// <paramref name="pruneGarbage"/> is on. Appends any --aux glob warnings to <paramref name="warnings"/>.</summary>
    private static IEnumerable<(string Rel, string Full, bool Garbage)> Select(
        string sourceRoot,
        IReadOnlyCollection<string> alreadyEmittedRel,
        IReadOnlyCollection<string> droppedCodeFilesRel,
        IReadOnlyList<string> excludeDirs,
        IReadOnlyList<string> auxGlobs,
        bool pruneGarbage,
        IReadOnlyCollection<string>? observed,
        List<string> warnings)
    {
        var skip = new HashSet<string>(alreadyEmittedRel, StringComparer.OrdinalIgnoreCase);
        var dropped = new HashSet<string>(droppedCodeFilesRel, StringComparer.OrdinalIgnoreCase);
        // A file a real build/run was observed to OPEN is evidence it's used — never prune it as "garbage".
        var observedSet = new HashSet<string>(observed ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // --exclude matches DIRECTORY segments (not the file's own name): a rel path a/b/c.mk is excluded
        // when 'a' or 'b' is an excluded dir. Mirrors the source/asm/linker scans in the CLI.
        bool Included(string rel)
        {
            if (excludeDirs.Count == 0) return true;
            var segs = rel.Split('/');
            for (var i = 0; i < segs.Length - 1; i++)
                if (excludeDirs.Contains(segs[i], StringComparer.OrdinalIgnoreCase)) return false;
            return true;
        }

        // --aux force-includes a file even when it sits under an --exclude'd dir (a linker script kept in a
        // board-variant folder you otherwise prune). Reuses the hardened glob resolver + root-escape refusal.
        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var glob in auxGlobs)
        {
            if (BuildSupportEmitter.GlobEscapesRoot(glob))
            { warnings.Add($"--aux glob '{glob}' refused: contains '..' or an absolute path (would write outside --out)"); continue; }
            var matched = 0;
            foreach (var p in BuildSupportEmitter.MatchGlob(sourceRoot, glob))
            { forced.Add(Path.GetRelativePath(sourceRoot, p).Replace('\\', '/')); matched++; }
            if (matched == 0)
                warnings.Add($"--aux glob '{glob}' matched no files under {sourceRoot} "
                             + "(a bare pattern like '*.inc' already searches all subdirectories)");
        }

        // SourceWalk: skips unreadable dirs and doesn't recurse into junctions/symlinks (loop / double-copy),
        // while still returning symlinked files.
        foreach (var p in CodeCarver.Core.Util.SourceWalk.Files(sourceRoot))
        {
            var rel = Path.GetRelativePath(sourceRoot, p).Replace('\\', '/');
            if (skip.Contains(rel)) continue;      // already emitted (kept code + include closure)
            if (dropped.Contains(rel)) continue;   // modelled dead code — removed on carve evidence
            var isForced = forced.Contains(rel);
            if (!isForced && !Included(rel)) continue; // --exclude prunes (unless --aux forces it back)
            // Garbage is dropped only on CONVENTION (never a build/run input), only when enabled, never when --aux
            // forced the file back in, and never when a trace OBSERVED the file being opened (hard evidence it's used).
            var garbage = pruneGarbage && !isForced && !observedSet.Contains(rel) && InfraClassifier.IsGarbage(rel);
            yield return (rel, Path.GetFullPath(p), garbage);
        }
    }
}
