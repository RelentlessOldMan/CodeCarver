using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// The deterministic heart of CodeCarver. Given a graph and a complete set of roots, it computes the
/// transitive closure in a single pass — no build loop, no hardware, no guessing.
///
/// The design commitment worth stating plainly: <b>the answer is exact given the roots and the
/// graph's edges.</b> The only thing that makes carving hard is graph completeness — edges and roots
/// that don't appear syntactically (function pointers, vtables, interrupt vector tables, static
/// initializers). Those are handled where the graph is <em>built</em> (conservative edges) and where
/// roots are <em>discovered</em>, not here. This engine just walks what it's given, so it stays a
/// small, obviously-correct BFS whose result is independent of traversal order.
/// </summary>
public static class ReachabilityEngine
{
    public static CarvePlan Compute(CodeGraph graph, IEnumerable<Root> roots, ReachabilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(roots);
        options ??= ReachabilityOptions.Safe;

        var reached = new HashSet<NodeId>();
        var why = new Dictionary<NodeId, KeepReason>();
        var work = new Queue<NodeId>();

        // Seed. Roots are deduped; the first reason wins so "why" is stable.
        foreach (var root in roots)
        {
            if (reached.Add(root.Node))
            {
                why[root.Node] = KeepReason.Root(root.Kind);
                work.Enqueue(root.Node);
            }
        }

        // Single-pass transitive closure. The reached SET is invariant to order; we enqueue in edge
        // order purely so a node's recorded reason is its first (shortest-discovered) predecessor.
        while (work.Count > 0)
        {
            var from = work.Dequeue();
            foreach (var edge in graph.OutEdges(from))
            {
                if (!options.Follows(edge.Kind))
                    continue;
                if (reached.Add(edge.To))
                {
                    why[edge.To] = KeepReason.Reached(from, edge.Kind);
                    work.Enqueue(edge.To);
                }
            }
        }

        return new CarvePlan(graph, reached, why);
    }
}
