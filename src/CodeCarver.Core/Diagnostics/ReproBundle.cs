using System.Text.Json;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;

namespace CodeCarver.Core.Diagnostics;

/// <summary>
/// Serializes the dependency graph a carve ran over into an <b>anonymized, source-free</b> JSON snapshot
/// that a developer can replay to reproduce a mis-carve WITHOUT the proprietary source — the point being
/// that most carve bugs ("wrong symbol/file kept or dropped") live in the reachability + emit logic, which
/// only needs the graph's STRUCTURE, not any real identifier or byte of source.
///
/// Every symbol name and file path is replaced by a stable token (<c>s0</c>, <c>s1</c>… / <c>f0.c</c>,
/// <c>f1.h</c>… — the extension is kept because carve behavior differs by it). The mapping is consistent
/// WITHIN a bundle (the same name always gets the same token, so edges/roots stay meaningful) but carries
/// no way back to the original — there is no reverse table, no salt to leak. What survives is exactly what
/// the algorithm consumes: node kinds, flags, spans, the edge set, the root set, and the reached set the
/// run produced (so a replay can diff its result against what actually happened). No home paths, no source,
/// no real names.
/// </summary>
public static class ReproBundle
{
    /// <summary>Bump when the snapshot shape changes so a replayer can tell versions apart.</summary>
    public const int FormatVersion = 1;

    public static string Build(CodeGraph graph, CarvePlan plan)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        string NameTok(string? n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            if (!names.TryGetValue(n, out var t)) { t = "s" + names.Count.ToString("x"); names[n] = t; }
            return t;
        }
        string FileTok(string? f)
        {
            if (string.IsNullOrEmpty(f)) return "";
            if (!files.TryGetValue(f, out var t))
            {
                // Keep the extension only (carve behavior is ext-sensitive: .c vs .h vs .inc); the rest is opaque.
                t = "f" + files.Count.ToString("x") + System.IO.Path.GetExtension(f);
                files[f] = t;
            }
            return t;
        }

        // A File node IS its path (Name holds the path); every other kind's path is in File. Route each
        // through the file-token map so the same path is one token wherever it appears.
        var nodes = new List<object>(graph.NodeCount);
        foreach (var n in graph.Nodes)
            nodes.Add(new
            {
                id = n.Id.Value,
                kind = n.Kind.ToString(),
                name = n.Kind == NodeKind.File ? FileTok(n.Name) : NameTok(n.Name),
                file = FileTok(n.File),
                line = n.Span.StartLine,
                endLine = n.Span.EndLine,
                flags = (int)n.Flags,
            });

        var edges = new List<object>();
        foreach (var n in graph.Nodes)
            foreach (var e in graph.OutEdges(n.Id))
                edges.Add(new { from = e.From.Value, to = e.To.Value, kind = e.Kind.ToString() });

        // Roots are recovered from the plan (nodes whose keep-reason is "is a root"), so the bundle is
        // self-contained: graph + roots + the reached set the run produced.
        var roots = new List<object>();
        foreach (var kv in plan.Why)
            if (kv.Value.IsRoot)
                roots.Add(new { node = kv.Key.Value, kind = kv.Value.AsRoot!.Value.ToString() });
        var reached = plan.ReachedNodes.Select(r => r.Value).ToList();

        var doc = new
        {
            reproFormatVersion = FormatVersion,
            note = "Anonymized CodeCarver repro graph. Names/paths are replaced by stable tokens with no "
                 + "reverse mapping; contains NO source text, NO real identifiers, NO home paths. Replay "
                 + "reachability over (nodes, edges, roots) and diff against 'reached' to reproduce a carve.",
            counts = new
            {
                nodes = graph.NodeCount,
                edges = edges.Count,
                roots = roots.Count,
                reached = reached.Count,
                distinctNames = names.Count,
                distinctFiles = files.Count,
            },
            nodes,
            edges,
            roots,
            reached,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
}
