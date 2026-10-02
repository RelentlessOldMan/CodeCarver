using System.Text;
using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// The per-symbol keep/drop ledger: one line for every definition in the graph saying whether it was
/// KEPT or CARVED and — for kept symbols — the chain back to the root that pulled it in. This is the
/// always-written audit trail that answers "why is this still here / why did that disappear?" across the
/// whole tree at once (<c>--why &lt;symbol&gt;</c> answers one symbol; this is the exhaustive table).
/// Deterministically ordered so two runs over the same graph produce byte-identical output.
/// </summary>
public static class DecisionsReport
{
    public static string Render(CodeGraph graph, CarvePlan plan, string stageName)
    {
        var sb = new StringBuilder();
        sb.Append("# CodeCarver decisions");
        if (stageName.Length > 0) sb.Append($" — stage '{stageName}'");
        sb.Append('\n');
        sb.Append("# Every symbol the carve considered. KEPT = reachable from a root; CARVED = proven unreachable.\n");
        sb.Append("# For a kept symbol, the trailing chain traces the first hop back toward the root that kept it.\n");
        sb.Append($"# {plan.Stats.ReachedNodes}/{plan.Stats.TotalNodes} symbols kept, {plan.Stats.DroppedNodes} carved.\n\n");

        // Containers (the file / translation-unit vertices) aren't symbols — file-level keep/drop lives in the
        // report + manifest. Here we list the actual definitions, grouped by file then source order.
        var nodes = graph.Nodes
            .Where(n => n.Kind is not (NodeKind.File or NodeKind.TranslationUnit))
            .OrderBy(n => n.FilePath ?? "￿", StringComparer.Ordinal)
            .ThenBy(n => n.Span.StartLine)
            .ThenBy(n => n.Name, StringComparer.Ordinal);

        foreach (var n in nodes)
        {
            bool kept = plan.IsKept(n.Id);
            string loc = n.FilePath is { } f ? $"{f}:{n.Span.StartLine}" : "(no file)";
            sb.Append(kept ? "KEPT   " : "CARVED ");
            sb.Append($"{n.Kind,-9} {n.Name}  @ {loc}");
            if (kept)
            {
                string via = Via(graph, plan, n.Id);
                if (via.Length > 0) sb.Append($"   {via}");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Compact one-line provenance: <c>&lt;= [Call] caller &lt;= ROOT[EntryPoint]</c>.</summary>
    private static string Via(CodeGraph graph, CarvePlan plan, NodeId id)
    {
        var sb = new StringBuilder();
        var cur = id;
        var guard = 0;
        while (guard++ < 1024)
        {
            if (!plan.Why.TryGetValue(cur, out var r)) break;
            if (r.IsRoot) { sb.Append($"<= ROOT[{r.AsRoot}]"); break; }
            if (!r.Via.IsValid) break;
            sb.Append($"<= [{r.ViaEdge}] {graph.GetNode(r.Via).Name} ");
            cur = r.Via;
        }
        return sb.ToString().TrimEnd();
    }
}
