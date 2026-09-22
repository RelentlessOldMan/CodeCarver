using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests;

public class ReachabilityTests
{
    [Fact]
    public void DirectChain_KeepsReachable_CarvesDead()
    {
        var b = new GraphBuilder();
        var foo = b.Func("foo", "a.c");
        var bar = b.Func("bar", "a.c");
        var baz = b.Func("baz", "b.c");
        var dead = b.Func("dead", "c.c");
        b.Calls(foo, bar);
        b.Calls(bar, baz);
        // 'dead' is referenced by nothing.

        var plan = ReachabilityEngine.Compute(b.Graph,
            new[] { new Root(foo, RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(foo));
        Assert.True(plan.IsKept(bar));
        Assert.True(plan.IsKept(baz));
        Assert.False(plan.IsKept(dead));
        Assert.Equal(3, plan.Stats.ReachedNodes);
        Assert.Equal(1, plan.Stats.DroppedNodes);
    }

    [Fact]
    public void Explain_TracesKeepChainToRoot_AndReportsCarved()
    {
        var b = new GraphBuilder();
        var foo = b.Func("foo", "a.c");
        var bar = b.Func("bar", "a.c");
        var dead = b.Func("dead", "b.c");
        b.Calls(foo, bar);

        var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(foo, RootKind.ExplicitSymbol) });

        var why = plan.Explain(bar);
        Assert.Contains("ROOT", why);   // walks back to the seed
        Assert.Contains("Calls", why);  // via the foo -> bar edge
        Assert.Contains("CARVED", plan.Explain(dead)); // unreached -> reported as carved
    }

    [Fact]
    public void FileLevel_DropsFilesWithNoReachedNode()
    {
        var b = new GraphBuilder();
        var foo = b.Func("foo", "keep.c");
        var bar = b.Func("bar", "keep.c");
        _ = b.Func("only_dead", "gone.c");
        b.Calls(foo, bar);

        var plan = ReachabilityEngine.Compute(b.Graph,
            new[] { new Root(foo, RootKind.ExplicitSymbol) });

        Assert.Contains("keep.c", plan.KeptFiles);
        Assert.Contains("gone.c", plan.DroppedFiles);
        Assert.DoesNotContain("gone.c", plan.KeptFiles);
    }

    [Fact]
    public void VectorTableRoot_KeepsIsr_ThatIsNeverCalled()
    {
        // The classic embedded gotcha: an ISR reachable only because the vector table roots it.
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var isr = b.Func("Timer_ISR", "isr.c", NodeFlags.AddressTaken);

        // From main alone the ISR is unreachable...
        var fromMainOnly = ReachabilityEngine.Compute(b.Graph,
            new[] { new Root(main, RootKind.EntryPoint) });
        Assert.False(fromMainOnly.IsKept(isr));

        // ...but with the vector-table root discovered, it survives.
        var withVector = ReachabilityEngine.Compute(b.Graph, new[]
        {
            new Root(main, RootKind.EntryPoint),
            new Root(isr, RootKind.VectorTable, "IRQ7"),
        });
        Assert.True(withVector.IsKept(isr));
        Assert.Equal(RootKind.VectorTable, withVector.Why[isr].AsRoot);
    }

    [Fact]
    public void ConservativeEdge_SafeKeeps_MinimalDrops()
    {
        // A handler reached only through a function pointer (AddressTaken edge).
        var b = new GraphBuilder();
        var dispatch = b.Func("dispatch", "main.c");
        var handler = b.Func("cmd_handler", "cmd.c", NodeFlags.AddressTaken);
        b.AddressTaken(dispatch, handler);

        var roots = new[] { new Root(dispatch, RootKind.ExplicitSymbol) };

        var safe = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.Safe);
        var minimal = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.MinimalUnsafe);

        Assert.True(safe.IsKept(handler));      // sound carve: keep the pointer target
        Assert.False(minimal.IsKept(handler));  // strictly-direct set would break the build
        Assert.Equal(1, safe.Stats.ReachedNodes - minimal.Stats.ReachedNodes);
    }

    [Fact]
    public void CustomEdgeFilter_Overrides_ConservativeDefault()
    {
        var b = new GraphBuilder();
        var a = b.Func("a", "a.c");
        var v = b.Func("v", "v.c");
        b.Vtable(a, v);

        var roots = new[] { new Root(a, RootKind.ExplicitSymbol) };
        var only = new ReachabilityOptions { EdgeFilter = k => k == EdgeKind.Calls }; // vtable excluded
        var plan = ReachabilityEngine.Compute(b.Graph, roots, only);

        Assert.False(plan.IsKept(v));
    }

    [Fact]
    public void Explain_WalksBackToRoot()
    {
        var b = new GraphBuilder();
        var foo = b.Func("foo", "a.c");
        var bar = b.Func("bar", "a.c");
        b.Calls(foo, bar);

        var plan = ReachabilityEngine.Compute(b.Graph,
            new[] { new Root(foo, RootKind.EntryPoint) });

        var chain = plan.Explain(bar);
        Assert.Contains("Calls", chain);
        Assert.Contains("ROOT[EntryPoint]", chain);
    }

    [Fact]
    public void Result_IsDeterministic_AcrossRuns()
    {
        CodeGraph Build()
        {
            var b = new GraphBuilder();
            var r = b.Func("r", "a.c");
            for (var i = 0; i < 50; i++)
            {
                var f = b.Func($"f{i}", $"f{i % 7}.c");
                b.Calls(r, f);
            }
            return b.Graph;
        }

        var g1 = Build();
        var g2 = Build();
        var roots1 = new[] { new Root(new NodeId(0), RootKind.ExplicitSymbol) };
        var roots2 = new[] { new Root(new NodeId(0), RootKind.ExplicitSymbol) };

        var p1 = ReachabilityEngine.Compute(g1, roots1);
        var p2 = ReachabilityEngine.Compute(g2, roots2);

        Assert.Equal(p1.ReachedNodes, p2.ReachedNodes);
        Assert.Equal(p1.KeptFiles, p2.KeptFiles);
    }

    [Fact]
    public void ExplicitRootProvider_FindsSymbolsAndFiles()
    {
        var b = new GraphBuilder();
        var foo = b.Func("foo", "a.c");
        var bar = b.Func("bar", "b.c");
        _ = b.Func("other", "b.c");

        var provider = new ExplicitRootProvider(symbols: new[] { "foo" }, files: new[] { "b.c" });
        var roots = provider.Discover(b.Graph).ToList();

        Assert.Contains(roots, r => r.Node == foo && r.Kind == RootKind.ExplicitSymbol);
        // every node in b.c becomes an ExplicitFile root
        Assert.Contains(roots, r => r.Node == bar && r.Kind == RootKind.ExplicitFile);
    }
}

public class GraphModelTests
{
    [Fact]
    public void Interning_ReturnsSameId_ForSameIdentity()
    {
        var g = new CodeGraph();
        var a = g.GetOrAddNode(NodeKind.Function, "foo", "a.c");
        var b = g.GetOrAddNode(NodeKind.Function, "foo", "a.c");
        Assert.Equal(a, b);
        Assert.Equal(1, g.NodeCount);
    }

    [Fact]
    public void Interning_MergesFlags_OnReAdd()
    {
        var g = new CodeGraph();
        var a = g.GetOrAddNode(NodeKind.Function, "foo", "a.c", flags: NodeFlags.None);
        g.GetOrAddNode(NodeKind.Function, "foo", "a.c", flags: NodeFlags.AddressTaken);
        Assert.True(g.GetNode(a).Flags.HasFlag(NodeFlags.AddressTaken));
    }

    [Fact]
    public void DifferentKind_IsDifferentNode()
    {
        var g = new CodeGraph();
        var fn = g.GetOrAddNode(NodeKind.Function, "X", "a.c");
        var ty = g.GetOrAddNode(NodeKind.Type, "X", "a.c");
        Assert.NotEqual(fn, ty);
        Assert.Equal(2, g.NodeCount);
    }

    [Theory]
    [InlineData(EdgeKind.AddressTaken, true)]
    [InlineData(EdgeKind.VtableEntry, true)]
    [InlineData(EdgeKind.InlineAsmRef, true)]
    [InlineData(EdgeKind.Calls, false)]
    [InlineData(EdgeKind.References, false)]
    [InlineData(EdgeKind.Includes, false)]
    public void IsConservative_ClassifiesEdges(EdgeKind kind, bool expected)
        => Assert.Equal(expected, kind.IsConservative());
}
