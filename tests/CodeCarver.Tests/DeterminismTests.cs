using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// Determinism is a load-bearing contract for CodeCarver: the same inputs must always produce the same
/// carve, in the same ORDER (kept-file list, dropped-file list, node stats). A firmware team diffing two
/// carves, or a CI gate comparing against a golden manifest, relies on byte-stable output — a stray
/// HashSet-iteration order or dictionary-order leak would surface as spurious diffs. These parse + carve
/// the same multi-file program twice (fresh front-ends) and assert the results are identical.
/// </summary>
public sealed class DeterminismTests
{
    // Cross-file calls, a function pointer (address-taken), a macro-reached callee, and dead code —
    // enough graph shape that any nondeterministic ordering would show up in the kept/dropped lists.
    private static readonly (string, string)[] Program =
    {
        ("main.c", """
            #include "api.h"
            int helper(void){ return 1; }
            static int (*dispatch)(void) = handler;   // address-taken
            int run(void){ return helper() + api_entry(); }
            int dead_a(void){ return 99; }
            int main(void){ return run(); }
            """),
        ("api.c", """
            #include "api.h"
            int api_entry(void){ return LEAF(); }      // reached only through a macro
            int leaf_impl(void){ return 7; }
            int dead_b(void){ return 42; }
            """),
        ("api.h", """
            #ifndef API_H
            #define API_H
            #define LEAF() leaf_impl()
            int api_entry(void);
            int handler(void);
            int leaf_impl(void);
            #endif
            """),
        ("handler.c", "int handler(void){ return 3; }\nint also_dead(void){ return 0; }\n"),
        ("unused.c", "int unused_module(void){ return 5; }\n"),   // a whole dead file: dropped
    };

    private static (CodeGraph Graph, CarvePlan Plan) CarveWithGraph()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(Program);
        var roots = new ExplicitRootProvider(symbols: new[] { "main" }).Discover(graph).ToList();
        return (graph, ReachabilityEngine.Compute(graph, roots));
    }

    private static CarvePlan Carve() => CarveWithGraph().Plan;

    [Fact]
    public void SameInput_ProducesIdenticalKeptAndDroppedOrder()
    {
        var a = Carve();
        var b = Carve();

        // Sequence equality (not set equality) — order must be stable, not just membership.
        Assert.Equal(a.KeptFiles, b.KeptFiles);
        Assert.Equal(a.DroppedFiles, b.DroppedFiles);
        Assert.Equal(a.Stats.ReachedNodes, b.Stats.ReachedNodes);
        Assert.Equal(a.Stats.TotalNodes, b.Stats.TotalNodes);
        Assert.Equal(a.Stats.KeptFiles, b.Stats.KeptFiles);
    }

    [Fact]
    public void Carve_KeepsReachable_DropsDeadCode()
    {
        // Also pins the actual behavior the determinism test rests on, so "identical" can't mean
        // "identically wrong": dead code is dropped, macro/function-pointer-reached code is kept.
        var (graph, plan) = CarveWithGraph();
        var kept = new HashSet<string>(plan.KeptFiles, StringComparer.Ordinal);

        Assert.Contains("main.c", kept);
        Assert.Contains("api.c", kept);       // api_entry reachable from run()
        Assert.Contains("handler.c", kept);   // reached only via address-taken dispatch pointer

        // The drop half of the name (review TS7): the dead file is dropped...
        Assert.Equal(new[] { "unused.c" }, plan.DroppedFiles);
        Assert.DoesNotContain("unused.c", kept);

        // ...and at symbol level every dead function is unreached while every live one is reached, including
        // those reached only through a macro (leaf_impl) or an address-taken pointer (handler).
        bool Reached(string name) => graph.Nodes.Where(n => n.Name == name && n.Kind == NodeKind.Function)
            .Any(n => plan.Reached.Contains(n.Id));
        foreach (var live in new[] { "main", "run", "helper", "api_entry", "leaf_impl", "handler" })
            Assert.True(Reached(live), $"{live} should be reached");
        foreach (var dead in new[] { "dead_a", "dead_b", "also_dead", "unused_module" })
            Assert.False(Reached(dead), $"{dead} should be dropped");
    }
}
