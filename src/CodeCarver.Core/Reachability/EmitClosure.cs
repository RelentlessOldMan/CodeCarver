using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Closes a carve over what the emitter will actually WRITE (review F1, owner decision D-A).
///
/// Reachability decides which definitions are needed, but the emitter writes text: a file-level stage copies
/// every kept file whole, and the pruned stage keeps every unreached span it cannot cleanly remove. Each
/// unreached definition that is written anyway still calls and references things — and if those were
/// dropped, the carved tree does not link (an unreached <c>b_unused()</c> in a kept <c>a.c</c> calling a
/// dropped <c>c()</c>). This closure roots every such retained definition and recomputes, until a fixpoint:
/// the plan is then closed over the emitted text, so the output links without <c>--gc-sections</c>.
///
/// Incremental: each round only re-examines files that gained a reached node, so the cost is one BFS over the
/// graph plus one pass over the touched files — not one full recompute per round.
/// </summary>
public static class EmitClosure
{
    /// <param name="retainedIn">For a kept file and the current keep-test, the definitions the emitter will
    /// write although they are unreached. Called again whenever the file gains a reached node.</param>
    /// <param name="constructorsIn">Optional: the C++ constructors a kept file's text releases
    /// (<see cref="ConstructorGate"/>); rooted as <see cref="RootKind.Constructor"/>. Called once per kept file.</param>
    /// <param name="stubbed">Optional: the definitions whose body the emitter replaces with a stub (<see cref="Emit.StubBodies"/>):
    /// the functions and variables only such a body used are not reached through it.</param>
    public static CarvePlan Close(CodeGraph graph, IEnumerable<Root> roots,
                                  Func<string, Func<NodeId, bool>, IEnumerable<NodeId>> retainedIn,
                                  ReachabilityOptions? options = null,
                                  Func<string, IEnumerable<NodeId>>? constructorsIn = null,
                                  Func<NodeId, bool>? stubbed = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= ReachabilityOptions.Safe;

        var reached = new HashSet<NodeId>();
        var why = new Dictionary<NodeId, KeepReason>();
        var work = new Queue<NodeId>();
        var dirty = new SortedSet<string>(StringComparer.Ordinal);

        void Seed(NodeId id, RootKind kind)
        {
            if (!reached.Add(id)) return;
            why[id] = KeepReason.Root(kind);
            work.Enqueue(id);
        }
        foreach (var r in roots) Seed(r.Node, r.Kind);

        while (true)
        {
            while (work.Count > 0)
            {
                var from = work.Dequeue();
                if (graph.GetNode(from).FilePath is { } f) dirty.Add(f);
                // A stubbed body uses nothing: only what its signature and file need (its file, types, macros).
                var bodyless = stubbed is not null && stubbed(from);
                foreach (var edge in graph.OutEdges(from))
                {
                    if (!options.Follows(edge.Kind) || (bodyless && UsedByBody(graph, edge)) || !reached.Add(edge.To)) continue;
                    why[edge.To] = KeepReason.Reached(from, edge.Kind);
                    work.Enqueue(edge.To);
                }
            }
            if (dirty.Count == 0) break;
            var files = dirty.ToList();
            dirty.Clear();
            foreach (var f in files)
            {
                foreach (var id in retainedIn(f, reached.Contains))
                    Seed(id, RootKind.EmittedWhole);
                if (constructorsIn is not null)
                    foreach (var id in constructorsIn(f))
                        Seed(id, RootKind.Constructor);
            }
            if (work.Count == 0) break;
        }
        return new CarvePlan(graph, reached, why);
    }

    /// <summary>An edge a stubbed body no longer has: to a function or variable it called or named. Its file, the
    /// types and macros of its signature, stay.</summary>
    static bool UsedByBody(CodeGraph graph, Edge edge) => edge.Kind is not (EdgeKind.DefinedIn or EdgeKind.Includes)
                                                          && graph.GetNode(edge.To).Kind is NodeKind.Function or NodeKind.Global;

    /// <summary>Index of definition nodes (Function/Global with a known span) per file, for the policies.</summary>
    public static Dictionary<string, List<Node>> DefinitionsByFile(CodeGraph graph)
    {
        var map = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (n.Kind is not (NodeKind.Function or NodeKind.Global) || n.FilePath is not { } f) continue;
            if (!map.TryGetValue(f, out var l)) map[f] = l = new List<Node>();
            l.Add(n);
        }
        return map;
    }

    /// <summary>
    /// Header policy shared by both stages: a header is emitted whole, but a compiler only emits code for a
    /// header definition that is non-<c>static</c>, non-<c>inline</c>, non-template and at file/namespace scope
    /// (an unused inline or a method in a class body produces nothing to link). Those are the header
    /// definitions to retain. Decided with the same tokenizer as <see cref="EmittedLinkCheck"/>, so the
    /// closure and the verify agree on what a header contributes.
    /// </summary>
    public static IEnumerable<NodeId> RetainedInHeader(IReadOnlyList<Node> defs, string? headerText)
    {
        if (string.IsNullOrEmpty(headerText)) yield break;
        var emitting = new HashSet<(string, int)>();
        foreach (var d in EmittedLinkCheck.Scan(headerText).Definitions)
            if (!d.Static && !d.Inline && !d.Data) emitting.Add((d.Name, d.Line));
        if (emitting.Count == 0) yield break;
        foreach (var n in defs)
        {
            if (n.Kind != NodeKind.Function || !n.Span.IsKnown) continue;
            for (var line = n.Span.StartLine; line <= Math.Min(n.Span.EndLine, n.Span.StartLine + 8); line++)
                if (emitting.Contains((n.Name, line))) { yield return n.Id; break; }
        }
    }
}
