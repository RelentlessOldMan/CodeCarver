using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Proves the TRACE32 .cmm front-end: subroutines, GOSUB closure, and DO includes.</summary>
public class CmmFrontEndTests
{
    [Fact]
    public void ExtractsSubroutines_AndFollowsGosubClosure()
    {
        const string script = """
            MyEntry:
              GOSUB Helper
              RETURN
            Helper:
              GOSUB Deep    ; nested call
              RETURN
            Deep:
              RETURN
            Unused:
              RETURN
            """;
        using var fe = new CmmFrontEnd();
        var graph = fe.BuildGraph(new[] { ("load.cmm", script) });

        var entry = graph.Nodes.First(n => n.Name == "MyEntry").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(entry, RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(Find(graph, "Helper")));  // GOSUB Helper
        Assert.True(plan.IsKept(Find(graph, "Deep")));    // Helper GOSUB Deep (transitive)
        Assert.False(plan.IsKept(Find(graph, "Unused"))); // never GOSUB'd
    }

    [Fact]
    public void DoInclude_KeepsRunScript_DropsUnrelated()
    {
        const string main = "Start:\n  DO worker\n  RETURN\n";
        const string worker = "WorkerSub:\n  RETURN\n";
        const string unused = "UnusedSub:\n  RETURN\n";

        using var fe = new CmmFrontEnd();
        var graph = fe.BuildGraph(new[] { ("main.cmm", main), ("worker.cmm", worker), ("unused.cmm", unused) });

        var start = graph.Nodes.First(n => n.Name == "Start").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(start, RootKind.ExplicitSymbol) });

        Assert.Contains("worker.cmm", plan.KeptFiles);    // DO worker -> worker.cmm kept
        Assert.Contains("unused.cmm", plan.DroppedFiles);
    }

    [Fact]
    public void SubroutineBlockStyle_IsExtracted_AndReached()
    {
        // Regression (bao-demos): the newer `SUBROUTINE Name ( ... )` block form, not just `Label:`.
        const string script = """
            MAIN:
              GOSUB CheckStuff
              RETURN

            SUBROUTINE CheckStuff
            (
              PRIVATE &r
              RETURN
            )
            """;
        using var fe = new CmmFrontEnd();
        var graph = fe.BuildGraph(new[] { ("s.cmm", script) });
        var main = graph.Nodes.First(n => n.Name == "MAIN").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(main, RootKind.ExplicitSymbol) });
        Assert.True(plan.IsKept(Find(graph, "CheckStuff")));
    }

    [Fact]
    public void GosubInComment_IsIgnored()
    {
        const string script = "Entry:\n  ; GOSUB NotReallyCalled\n  RETURN\nNotReallyCalled:\n  RETURN\n";
        using var fe = new CmmFrontEnd();
        var graph = fe.BuildGraph(new[] { ("s.cmm", script) });
        var entry = graph.Nodes.First(n => n.Name == "Entry").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(entry, RootKind.ExplicitSymbol) });
        Assert.False(plan.IsKept(Find(graph, "NotReallyCalled"))); // the GOSUB is commented out
    }

    private static NodeId Find(CodeGraph graph, string name) =>
        graph.Nodes.First(n => n.Kind == NodeKind.Function && n.Name == name).Id;
}
