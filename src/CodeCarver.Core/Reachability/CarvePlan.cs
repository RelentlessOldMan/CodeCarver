using System.Text;
using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Reachability;

/// <summary>Headline counts of a carve — what it keeps and what it removes.</summary>
public readonly record struct CarveStats(
    int TotalNodes, int ReachedNodes,
    int TotalFiles, int KeptFiles)
{
    public int DroppedNodes => TotalNodes - ReachedNodes;
    public int DroppedFiles => TotalFiles - KeptFiles;

    public double NodeKeepRatio => TotalNodes == 0 ? 0 : (double)ReachedNodes / TotalNodes;
    public double FileKeepRatio => TotalFiles == 0 ? 0 : (double)KeptFiles / TotalFiles;
}

/// <summary>
/// The result of a carve: the exact set of nodes reachable from the roots, the files that survive,
/// and — for every kept node — why. Everything is deterministically ordered so two runs over the
/// same graph produce byte-identical reports.
/// </summary>
public sealed class CarvePlan
{
    private readonly CodeGraph _graph;

    internal CarvePlan(CodeGraph graph, IReadOnlySet<NodeId> reached,
                       IReadOnlyDictionary<NodeId, KeepReason> why)
    {
        _graph = graph;
        Reached = reached;
        Why = why;

        ReachedNodes = reached.OrderBy(n => n.Value).ToList();

        var kept = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in reached)
            if (graph.GetNode(id).FilePath is { } f)
                kept.Add(f);
        KeptFiles = kept.ToList();

        var all = graph.AllFiles();
        DroppedFiles = all.Where(f => !kept.Contains(f)).ToList();

        Stats = new CarveStats(graph.NodeCount, reached.Count, all.Count, kept.Count);
    }

    public IReadOnlySet<NodeId> Reached { get; }
    public IReadOnlyDictionary<NodeId, KeepReason> Why { get; }

    /// <summary>Reached nodes, ascending by id (stable).</summary>
    public IReadOnlyList<NodeId> ReachedNodes { get; }

    /// <summary>Files with at least one reached node, sorted (ordinal).</summary>
    public IReadOnlyList<string> KeptFiles { get; }

    /// <summary>Files that can be dropped entirely, sorted (ordinal).</summary>
    public IReadOnlyList<string> DroppedFiles { get; }

    public CarveStats Stats { get; }

    public bool IsKept(NodeId id) => Reached.Contains(id);

    /// <summary>Human-readable chain of why a node survived, walked back to its root.</summary>
    public string Explain(NodeId id)
    {
        if (!Reached.Contains(id))
            return $"{_graph.GetNode(id)} — CARVED (not reachable)";

        var sb = new StringBuilder();
        var cur = id;
        var guard = 0;
        while (guard++ < 1024)
        {
            var node = _graph.GetNode(cur);
            var reason = Why.TryGetValue(cur, out var r) ? r : default;
            if (reason.IsRoot)
            {
                sb.Append($"{node} <= ROOT[{reason.AsRoot}]");
                break;
            }
            sb.Append($"{node} <= [{reason.ViaEdge}] ");
            if (!reason.Via.IsValid) break;
            cur = reason.Via;
        }
        return sb.ToString();
    }
}
