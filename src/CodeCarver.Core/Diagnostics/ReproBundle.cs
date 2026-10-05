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
///
/// <see cref="Write"/> STREAMS straight to the output via <see cref="Utf8JsonWriter"/> — nodes and edges are
/// emitted incrementally, never materialized into an in-memory object list or string, so it scales to
/// multi-million-node graphs (e.g. a 13M-node synthetic corpus) without exhausting memory. The only thing
/// retained is the name/file token maps, bounded by the count of distinct identifiers.
/// </summary>
public static class ReproBundle
{
    /// <summary>Bump when the snapshot shape changes so a replayer can tell versions apart.</summary>
    public const int FormatVersion = 1;

    private const string Note =
        "Anonymized CodeCarver repro graph. Names/paths are replaced by stable tokens with no reverse "
        + "mapping; contains NO source text, NO real identifiers, NO home paths. Replay reachability over "
        + "(nodes, edges, roots) and diff against 'reached' to reproduce a carve.";

    /// <summary>Stream the anonymized snapshot to <paramref name="output"/>. Memory stays bounded (only the
    /// token maps are held), so this works at any graph size. Counts are emitted LAST — they depend on the
    /// token maps that fill as nodes stream — but JSON field order is immaterial to a replayer.</summary>
    public static void Write(Stream output, CodeGraph graph, CarvePlan plan, bool indented = true)
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

        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = indented });
        // Utf8JsonWriter buffers until Flush: without these periodic flushes the whole document sat in memory and
        // reached the stream in one write, which is not streaming at all.
        void Drain() { if (w.BytesPending > 64 * 1024) w.Flush(); }
        w.WriteStartObject();
        w.WriteNumber("reproFormatVersion", FormatVersion);
        w.WriteString("note", Note);

        // A File node IS its path (Name holds the path); every other kind's path is in File. Route each
        // through the file-token map so the same path is one token wherever it appears.
        w.WriteStartArray("nodes");
        foreach (var n in graph.Nodes)
        {
            w.WriteStartObject();
            w.WriteNumber("id", n.Id.Value);
            w.WriteString("kind", n.Kind.ToString());
            w.WriteString("name", n.Kind == NodeKind.File ? FileTok(n.Name) : NameTok(n.Name));
            w.WriteString("file", FileTok(n.File));
            w.WriteNumber("line", n.Span.StartLine);
            w.WriteNumber("endLine", n.Span.EndLine);
            w.WriteNumber("flags", (int)n.Flags);
            w.WriteEndObject();
            Drain();
        }
        w.WriteEndArray();

        long edgeCount = 0;
        w.WriteStartArray("edges");
        foreach (var n in graph.Nodes)
            foreach (var e in graph.OutEdges(n.Id))
            {
                w.WriteStartObject();
                w.WriteNumber("from", e.From.Value);
                w.WriteNumber("to", e.To.Value);
                w.WriteString("kind", e.Kind.ToString());
                w.WriteEndObject();
                edgeCount++;
                Drain();
            }
        w.WriteEndArray();

        // Roots are recovered from the plan (nodes whose keep-reason is "is a root"), so the bundle is
        // self-contained: graph + roots + the reached set the run produced.
        long rootCount = 0;
        w.WriteStartArray("roots");
        foreach (var kv in plan.Why)
            if (kv.Value.IsRoot)
            {
                w.WriteStartObject();
                w.WriteNumber("node", kv.Key.Value);
                w.WriteString("kind", kv.Value.AsRoot!.Value.ToString());
                w.WriteEndObject();
                rootCount++;
                Drain();
            }
        w.WriteEndArray();

        w.WriteStartArray("reached");
        foreach (var r in plan.ReachedNodes) { w.WriteNumberValue(r.Value); Drain(); }
        w.WriteEndArray();

        w.WriteStartObject("counts");
        w.WriteNumber("nodes", graph.NodeCount);
        w.WriteNumber("edges", edgeCount);
        w.WriteNumber("roots", rootCount);
        w.WriteNumber("reached", plan.ReachedNodes.Count);
        w.WriteNumber("distinctNames", names.Count);
        w.WriteNumber("distinctFiles", files.Count);
        w.WriteEndObject();

        w.WriteEndObject();
        w.Flush();
    }

    /// <summary>Convenience for tests and small graphs: the snapshot as an (indented) string. Prefer
    /// <see cref="Write"/> straight to a file for large graphs — this buffers the whole document in memory.</summary>
    public static string Build(CodeGraph graph, CarvePlan plan)
    {
        using var ms = new MemoryStream();
        Write(ms, graph, plan, indented: true);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
