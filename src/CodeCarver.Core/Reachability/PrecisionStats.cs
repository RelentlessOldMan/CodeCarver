using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Why the kept code is kept, as numbers only (for summary.txt): where a carve could get tighter. Each kept file is
/// counted once, under the first reason that applies:
/// <list type="bullet">
/// <item><c>root</c>: the file, or a definition in it, is a root (entry point, trace, link flag, force-keep, ...).</item>
/// <item><c>indirectOnly</c>: kept only through an edge that over-approximates (an address taken, a vtable, inline
///   asm): following direct calls and references alone would drop it. The deliberate price of soundness, and the
///   biggest lever for precision.</item>
/// <item><c>header</c>: a header kept because kept code includes it.</item>
/// <item><c>reached</c>: code a root reaches through direct calls and references.</item>
/// <item><c>other</c>: none of these (kept by the emit closure, for instance).</item>
/// </list>
/// </summary>
public static class PrecisionStats
{
    public static SortedDictionary<string, long> Compute(CodeGraph graph, CarvePlan plan, IReadOnlyList<Root> roots, Func<string, long> bytesOf)
    {
        var direct = ReachabilityEngine.Compute(graph, roots, ReachabilityOptions.MinimalUnsafe);
        var directFiles = direct.KeptFiles.ToHashSet(StringComparer.Ordinal);
        var rootFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roots)
            if (graph.GetNode(r.Node).FilePath is { } f) rootFiles.Add(f);
        var fileNodes = graph.Nodes.Where(n => n.Kind == NodeKind.File).ToDictionary(n => n.Name, n => n.Id, StringComparer.Ordinal);

        var files = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var bytes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var sizes = new List<long>();
        foreach (var f in plan.KeptFiles)
        {
            var reason = rootFiles.Contains(f) ? "root"
                : !directFiles.Contains(f) ? "indirectOnly"
                : fileNodes.TryGetValue(f, out var id) && plan.Why.TryGetValue(id, out var why) && why.ViaEdge == EdgeKind.Includes ? "header"
                : directFiles.Contains(f) ? "reached"
                : "other";
            var b = Math.Max(0, bytesOf(f));
            files[reason] = files.GetValueOrDefault(reason) + 1;
            bytes[reason] = bytes.GetValueOrDefault(reason) + b;
            sizes.Add(b);
        }
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (k, v) in files) result[$"keep.files.{k}"] = v;
        foreach (var (k, v) in bytes) result[$"keep.bytes.{k}"] = v;
        // How concentrated the kept bytes are: a few huge files (one big header, a generated table) or spread thin.
        sizes.Sort((x, y) => y.CompareTo(x));
        var total = sizes.Sum();
        result["keep.bytes.total"] = total;
        result["keep.bytes.largest10Percent"] = total == 0 ? 0 : (long)Math.Round(100.0 * sizes.Take(10).Sum() / total);
        result["keep.bytes.largestFile"] = sizes.Count > 0 ? sizes[0] : 0;
        result["reach.keptNodesDirectOnly"] = direct.Stats.ReachedNodes;
        // Kept nodes by kind, and the conservative edges they were first reached through.
        foreach (var g in plan.ReachedNodes.Select(graph.GetNode).GroupBy(n => n.Kind))
            result[$"keep.nodes.{g.Key}"] = g.Count();
        foreach (var g in plan.Why.Values.Where(w => w.ViaEdge is { } e && e.IsConservative()).GroupBy(w => w.ViaEdge!.Value))
            result[$"keep.firstReachedVia.{g.Key}"] = g.Count();
        return result;
    }
}
