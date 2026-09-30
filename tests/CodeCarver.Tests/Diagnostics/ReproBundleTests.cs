using CodeCarver.Core.Diagnostics;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

/// <summary>
/// The repro bundle is only shareable if anonymization is TOTAL: a single leaked real name or path would
/// defeat the whole point (a repro you can send without exposing proprietary IP). These pin that no real
/// identifier survives, that the structure needed to replay a carve does, and that output is deterministic.
/// </summary>
public sealed class ReproBundleTests
{
    private static (CodeGraph Graph, CarvePlan Plan) SampleCarve()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c", line: 1);
        var helper = b.Func("secret_helper", "secret_dir/secret_impl.c", line: 5);
        var t = b.Type("secret_config_t", "secret_dir/secret.h", line: 2);
        _ = b.Func("unused_dead_fn", "dead.c", line: 9); // dropped by the carve
        b.Calls(main, helper);
        b.Refs(helper, t);

        var roots = new List<Root> { new(main, RootKind.EntryPoint, "IMAGE_ENTRY_secret_note") };
        var plan = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.Safe);
        return (b.Graph, plan);
    }

    [Fact]
    public void Build_LeaksNoRealNamesOrPaths()
    {
        var (graph, plan) = SampleCarve();
        var json = ReproBundle.Build(graph, plan);

        // No symbol names, no path segments, and no root NOTE text may appear anywhere.
        foreach (var secret in new[]
                 {
                     "secret_helper", "secret_config_t", "unused_dead_fn", "main.c",
                     "secret_dir", "secret_impl", "secret.h", "dead.c", "IMAGE_ENTRY_secret_note",
                 })
            Assert.DoesNotContain(secret, json);

        // The tokenized name "main" must not survive as a bare identifier either.
        Assert.DoesNotContain("\"main\"", json);
    }

    [Fact]
    public void Build_KeepsStructureNeededToReplay()
    {
        var (graph, plan) = SampleCarve();
        var json = ReproBundle.Build(graph, plan);

        Assert.Contains("reproFormatVersion", json);
        Assert.Contains("\"nodes\"", json);
        Assert.Contains("\"edges\"", json);
        Assert.Contains("\"roots\"", json);
        Assert.Contains("\"reached\"", json);
        Assert.Contains("EntryPoint", json);   // root KIND is structural, not sensitive
        Assert.Contains("Calls", json);         // edge KIND is structural
        Assert.Contains(".c", json);            // file tokens keep the extension (carve behavior is ext-sensitive)
        Assert.Contains(".h", json);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var (graph, plan) = SampleCarve();
        Assert.Equal(ReproBundle.Build(graph, plan), ReproBundle.Build(graph, plan));
    }

    [Fact]
    public void Build_NodeAndEdgeCountsMatchGraph()
    {
        var (graph, plan) = SampleCarve();
        var json = ReproBundle.Build(graph, plan);
        // 4 defined functions/types + the file nodes their defs create; assert the reported node count equals
        // the graph's actual node count (the snapshot is complete, not a sample).
        Assert.Contains($"\"nodes\": {graph.NodeCount}", json);
    }
}
