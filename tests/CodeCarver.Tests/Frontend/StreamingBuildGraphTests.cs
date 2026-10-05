using CodeCarver.Core.Graph;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// The streaming BuildGraph overload (paths + on-demand reader) must produce exactly the same graph as the
/// eager tuple overload, while reading each file lazily through the callback — this is what lets a huge tree
/// be carved without holding every source byte in memory. It also pins the keep-whole contract: a path whose
/// reader returns "" gets a File node but no parsed symbols.
/// </summary>
public sealed class StreamingBuildGraphTests
{
    private static HashSet<string> FuncNames(CodeGraph g) =>
        g.Nodes.Where(n => n.Kind == NodeKind.Function).Select(n => n.Name).ToHashSet();

    /// <summary>The graph as sorted, id-free node and edge descriptors, so two graphs compare structurally.</summary>
    private static (List<string> Nodes, List<string> Edges) Shape(CodeGraph g)
    {
        string N(Node n) => $"{n.Kind}|{n.Name}|{n.File}|{n.Span}|{n.Flags}";
        var nodes = g.Nodes.Select(N).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var edges = g.Nodes.SelectMany(n => g.OutEdges(n.Id))
            .Select(e => $"{N(g.GetNode(e.From))} -{e.Kind}-> {N(g.GetNode(e.To))}")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        return (nodes, edges);
    }

    [Fact]
    public void StreamingOverload_MatchesTupleOverload_AndReadsViaCallback()
    {
        var files = new[]
        {
            ("a.c", "int helper(void){return 1;}\nint root(void){return helper();}\n"),
            ("b.c", "int only_in_b(void){return 2;}\n"),
        };

        using var feEager = new CFrontEnd();
        var eager = FuncNames(feEager.BuildGraph(files));

        var map = files.ToDictionary(f => f.Item1, f => f.Item2, StringComparer.Ordinal);
        var reads = new List<string>();
        using var feStream = new CFrontEnd();
        var stream = FuncNames(feStream.BuildGraph(
            files.Select(f => f.Item1).ToList(),
            p => { reads.Add(p); return map[p]; }));

        // Identical result: same set of function definitions discovered...
        Assert.Equal(eager.OrderBy(x => x), stream.OrderBy(x => x));
        // ...and (review TS7) the same GRAPH: every node with its kind, file, span and flags, and every edge.
        using var feEager2 = new CFrontEnd();
        using var feStream2 = new CFrontEnd();
        var g1 = feEager2.BuildGraph(files);
        var g2 = feStream2.BuildGraph(files.Select(f => f.Item1).ToList(), p => map[p]);
        Assert.Equal(Shape(g1).Nodes, Shape(g2).Nodes);
        Assert.Equal(Shape(g1).Edges, Shape(g2).Edges);
        Assert.Contains(Shape(g1).Edges, e => e.Contains("root") && e.Contains("helper")); // the call edge is there
        Assert.Contains("helper", stream);
        Assert.Contains("root", stream);
        Assert.Contains("only_in_b", stream);

        // Text came ONLY through the reader (no eager materialization) — every file was fetched via callback.
        Assert.Contains("a.c", reads);
        Assert.Contains("b.c", reads);
    }

    [Fact]
    public void StreamingOverload_EmptyReaderResult_KeepsFileWhole_NoSymbols()
    {
        // A reader returning "" models a skip/keep-whole/unreadable file: the File node exists (so it can be
        // kept via #include-closure and copied verbatim), but nothing inside it is parsed into the graph.
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(
            new[] { "big.h" },
            _ => "");   // never yields text

        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.File && n.Name == "big.h");
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKind.Function);
    }
}
