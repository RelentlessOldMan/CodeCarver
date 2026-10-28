using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Kept source files the build never compiles, split by whether the build could need them. The carve resolves a call
/// by name, so a tree with a variant of a function per chip keeps every variant; the build compiles one. A kept file
/// the build compiles nothing of is <em>needed</em> only if compiled code (or a root) uses a function or variable
/// defined there that no compiled file defines: then the build must link it, and the build log lacks its compile.
/// Otherwise it is a <em>variant</em>: kept by the name match, never part of the build.
/// </summary>
public static class UncompiledUnits
{
    /// <param name="compiled">Is this file one the build's compiles read (a compiled unit, or a file one included)?</param>
    /// <param name="unit">Is this file a source file a compile command would name (.c, .cpp ...)?</param>
    public static (int Needed, int Variants) Split(CodeGraph graph, CarvePlan plan, Func<string, bool> compiled, Func<string, bool> unit)
    {
        var suspects = plan.KeptFiles.Where(f => unit(f) && !compiled(f)).ToHashSet(StringComparer.Ordinal);
        if (suspects.Count == 0) return (0, 0);

        // What the compiled files define: global names, and file-local ones per file (a static resolves only there).
        var defined = new HashSet<(NodeKind, string)>();
        var definedLocal = new HashSet<(NodeKind, string, string)>();
        foreach (var n in graph.Nodes)
            if (n.Kind is NodeKind.Function or NodeKind.Global && n.File is { } f && compiled(f))
                if ((n.Flags & NodeFlags.FileLocal) != 0) definedLocal.Add((n.Kind, n.Name, f)); else defined.Add((n.Kind, n.Name));
        bool Provided(Node n, string? from) =>
            defined.Contains((n.Kind, n.Name)) || from is not null && definedLocal.Contains((n.Kind, n.Name, from));

        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in plan.ReachedNodes)
        {
            var n = graph.GetNode(id);
            if (n.FilePath is not { } file) continue;
            if (suspects.Contains(file))
            {
                // A root only an uncompiled file defines: the build's entry point is there.
                if (n.Kind is NodeKind.Function or NodeKind.Global && plan.Why.TryGetValue(id, out var why) && why.IsRoot && !Provided(n, null))
                    needed.Add(file);
                continue;
            }
            if (!compiled(file)) continue;
            foreach (var e in graph.OutEdges(id))
            {
                if (e.Kind is EdgeKind.Contains or EdgeKind.DefinedIn or EdgeKind.Includes or EdgeKind.Expands) continue;
                var t = graph.GetNode(e.To);
                if (t.Kind is NodeKind.Function or NodeKind.Global && t.File is { } tf && suspects.Contains(tf) && !Provided(t, file))
                    needed.Add(tf);
            }
        }
        return (needed.Count, suspects.Count - needed.Count);
    }
}
