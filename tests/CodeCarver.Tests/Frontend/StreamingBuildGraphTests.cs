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

        // Identical result: same set of function definitions discovered.
        Assert.Equal(eager.OrderBy(x => x), stream.OrderBy(x => x));
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
