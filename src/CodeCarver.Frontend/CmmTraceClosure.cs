using CodeCarver.Core.Graph;

namespace CodeCarver.Frontend;

/// <summary>Outcome of the trace-seeded .cmm closure: which scripts to keep, which to drop, and any
/// unresolved-dispatch / oversized warnings to surface. <see cref="ObservedSeeds"/> is 0 when the run trace
/// opened no .cmm (so nothing was tightened and <see cref="Kept"/> is everything, <see cref="Dropped"/> empty).</summary>
public readonly record struct CmmClosureResult(
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> Warnings,
    int Total, int ObservedSeeds, int ClosureAdded, int OversizedKeptWhole);

/// <summary>
/// Tightens the set of TRACE32 <c>.cmm</c> scripts kept as infrastructure, using a RUN trace. The scripts a
/// run actually opened are the SEEDS; <see cref="CmmFrontEnd"/> builds the static DO/GOSUB graph, and we keep
/// every script reachable from a seed. Anything neither observed nor reachable is dropped — turning "keep all
/// 1,600 scripts because we can't prove which run" into "keep the few the run needs."
///
/// It never pretends to be sound where PRACTICE isn't statically analyzable: a dynamic <c>DO &amp;var</c> (the
/// common runtime-chosen path) and an oversized script kept whole without parsing are SURFACED as warnings, not
/// silently dropped — the caller can widen the trace or force-keep. With no observed seed we tighten nothing and
/// keep every script (the sound default), exactly as a no-trace carve does today.
/// </summary>
public static class CmmTraceClosure
{
    /// <param name="allCmm">Every .cmm under the carve root (forward-slash rel path + byte size).</param>
    /// <param name="observedCmm">The subset a run file-trace observed being opened (the seeds).</param>
    /// <param name="read">Reads a script's text by rel path (only called for under-cap files).</param>
    /// <param name="maxParseBytes">Scripts larger than this are kept whole and NOT DO-parsed (memory backstop).</param>
    /// <param name="dropUnobserved">Owner decision D-C: scripts are only DROPPED when the user opted in
    /// (<c>[runs.X] dropUnobservedCmm = true</c>); otherwise the closure is computed for the report and every
    /// script is kept. Even when opted in, nothing is dropped while a kept script has a dynamic <c>DO &amp;var</c>.</param>
    public static CmmClosureResult Compute(
        IReadOnlyList<(string Rel, long Bytes)> allCmm,
        IReadOnlyCollection<string> observedCmm,
        Func<string, string> read,
        long maxParseBytes,
        bool dropUnobserved = true)
    {
        var observed = new HashSet<string>(observedCmm, StringComparer.OrdinalIgnoreCase);
        var seeds = allCmm.Where(c => observed.Contains(c.Rel)).Select(c => c.Rel).ToList();

        // No seed => can't prove which scripts run => keep them all (sound). Nothing dropped, nothing to warn.
        if (seeds.Count == 0)
            return new CmmClosureResult(
                allCmm.Select(c => c.Rel).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Array.Empty<string>(), Array.Empty<string>(), allCmm.Count, 0, 0, 0);

        // Oversized scripts are registered but handed EMPTY text so CmmFrontEnd keeps the file node without
        // splitting a 300 MB file into lines — the same keep-whole backstop big C headers get. Their DO/GOSUB
        // closure is therefore not followed; we warn below if such a script ends up kept.
        // Streamed: the front-end reads one script at a time (review RB1). An unreadable script is kept whole
        // like an oversized one and said so — never silently treated as having no DO.
        var oversized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        foreach (var (rel, bytes) in allCmm) if (bytes > maxParseBytes) oversized.Add(rel);
        string ReadOne(string rel)
        {
            if (oversized.Contains(rel)) return "";
            try { return read(rel); }
            catch (Exception) { unreadable.Add(rel); oversized.Add(rel); return ""; }
        }

        var fe = new CmmFrontEnd();
        var graph = fe.BuildGraph(allCmm.Select(c => c.Rel).ToList(), ReadOne);

        // File-level dependency: script X depends on script Y when ANY node in X does `DO Y` (an Includes edge).
        // We keep whole .cmm files (never carve inside a script), so the closure is over FILES, not subroutines
        // — a kept script drags in everything it can DO from anywhere in it (top-level or inside a subroutine).
        var deps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in graph.Nodes)
        {
            if (n.FilePath is not { } fromFile) continue;
            foreach (var e in graph.OutEdges(n.Id))
            {
                if (e.Kind != EdgeKind.Includes) continue;
                if (graph.GetNode(e.To).FilePath is not { } toFile) continue;
                if (!deps.TryGetValue(fromFile, out var l)) deps[fromFile] = l = new List<string>();
                l.Add(toFile);
            }
        }

        // BFS over the file graph from the observed seeds.
        var kept = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(seeds);
        while (queue.Count > 0)
            if (deps.TryGetValue(queue.Dequeue(), out var outs))
                foreach (var t in outs)
                    if (kept.Add(t)) queue.Enqueue(t);

        var closureCount = kept.Count;
        // D-C: drop only on explicit opt-in, and never while a kept script dispatches dynamically (we can't
        // see what it runs). Otherwise everything stays; the closure is still reported.
        var dynamicInKept = fe.Warnings.Any(w => w.Contains("variable path", StringComparison.Ordinal)
                                                 && w.IndexOf(':') is > 0 and var c && kept.Contains(w[..c]));
        var holdBack = !dropUnobserved || dynamicInKept || unreadable.Any(kept.Contains);
        if (holdBack) foreach (var x in allCmm) kept.Add(x.Rel);
        var keptList = allCmm.Where(c => kept.Contains(c.Rel)).Select(c => c.Rel).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var dropped = allCmm.Where(c => !kept.Contains(c.Rel)).Select(c => c.Rel).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // Surface unresolved-dispatch warnings, but drop the ones about scripts we didn't keep (a dynamic DO in
        // a DROPPED script is moot). Fail OPEN: only suppress a warning we can POSITIVELY attribute to a dropped
        // script (its `{path}:` prefix is a known .cmm that isn't kept). A warning whose shape we don't recognise
        // is surfaced, not silently swallowed — better a stray warning than a hidden one.
        var allRel = new HashSet<string>(allCmm.Select(c => c.Rel), StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var w in fe.Warnings)
        {
            var colon = w.IndexOf(':');
            var prefix = colon > 0 ? w[..colon] : "";
            if (prefix.Length > 0 && allRel.Contains(prefix) && !kept.Contains(prefix)) continue; // attributed to a dropped script
            warnings.Add(w);
        }
        foreach (var u in unreadable)
            warnings.Add($"{u}: could not be read — kept whole; DO/GOSUB closure not computed for it");
        if (dropUnobserved && dynamicInKept)
            warnings.Add($"a kept script uses a dynamic `DO &var`: dropping nothing ({allCmm.Count - closureCount} unobserved script(s) kept)");
        var oversizedKept = keptList.Where(oversized.Contains).ToList();
        foreach (var o in oversizedKept)
            warnings.Add($"{o}: kept whole (over {maxParseBytes:N0} B) — DO/GOSUB closure not computed; "
                + "scripts it reaches may be missing (widen the trace or forceKeep them)");

        return new CmmClosureResult(keptList, dropped, warnings, allCmm.Count,
            seeds.Count, closureCount - seeds.Count, oversizedKept.Count);
    }
}
