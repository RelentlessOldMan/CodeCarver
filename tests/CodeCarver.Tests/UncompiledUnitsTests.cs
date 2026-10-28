using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests;

public class UncompiledUnitsTests
{
    static readonly HashSet<string> Compiled = new(StringComparer.Ordinal) { "main.c", "drv_a.c", "local.c", "inc/x.h" };

    static (int Needed, int Variants) Split(GraphBuilder b, params NodeId[] roots) =>
        All(b, roots) is var r ? (r.Needed, r.Variants) : default;

    static UncompiledUnits.Result All(GraphBuilder b, NodeId[] roots, bool linkCovers = false) =>
        UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, roots.Select(r => new Root(r, RootKind.ExplicitSymbol))),
                              Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal), linkCovers);

    [Fact]
    public void AVariantOfACompiledFunction_IsNotNeeded()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var a = b.Func("kq_init", "drv_a.c");
        var other = b.Func("kq_init", "drv_b.c");          // the other chip's copy: the call matches both
        var helper = b.Func("kq_b_helper", "drv_b_util.c");  // reached only through the variant
        b.Calls(main, a);
        b.Calls(main, other);
        b.Calls(other, helper);
        Assert.Equal((0, 2), Split(b, main));
    }

    [Fact]
    public void WhatOnlyAnUncompiledFileDefines_IsNeeded()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var only = b.Func("kq_only", "lost.c");     // compiled code calls it and nothing compiled defines it
        var table = b.Global("kq_table", "lost2.c");
        b.Calls(main, only);
        b.Graph.AddEdge(main, table, EdgeKind.References);
        Assert.Equal((2, 0), Split(b, main));
        Assert.Equal(new UncompiledUnits.Result(2, 0, 0, 1, 1, 0), All(b, new[] { main }));
        Assert.Equal(new UncompiledUnits.Result(0, 2, 0, 0, 0, 0), All(b, new[] { main }, linkCovers: true));   // the link says the log is the build
    }

    [Fact]
    public void AStaticElsewhere_DoesNotProvideTheName()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        b.Func("kq_f", "local.c", NodeFlags.FileLocal);   // a static in another compiled file
        var f = b.Func("kq_f", "lost.c");
        b.Calls(main, f);
        Assert.Equal((1, 0), Split(b, main));

        var b2 = new GraphBuilder();
        var caller = b2.Func("kq_g", "local.c");
        var mine = b2.Func("kq_f", "local.c", NodeFlags.FileLocal);   // the caller's own static
        var f2 = b2.Func("kq_f", "lost.c");
        b2.Calls(caller, mine);
        b2.Calls(caller, f2);
        Assert.Equal((0, 1), Split(b2, caller));
    }

    [Fact]
    public void ARootOnlyAnUncompiledFileDefines_IsNeeded()
    {
        var b = new GraphBuilder();
        var entry = b.Func("kq_entry", "lost.c");
        Assert.Equal((1, 0), Split(b, entry));
        Assert.Equal(1, All(b, new[] { entry }, linkCovers: true).ByRoot);   // the link can't settle a root the build never compiles
        var b2 = new GraphBuilder();
        b2.Func("kq_entry", "main.c");
        var copy = b2.Func("kq_entry", "lost.c");
        Assert.Equal((0, 1), Split(b2, copy));
    }

    [Fact]
    public void AnInlineInACompiledHeader_CountsAsCompiledCode()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var inl = b.Func("kq_inl", "inc/x.h");
        var only = b.Func("kq_only", "lost.c");
        b.Calls(main, inl);
        b.Calls(inl, only);
        Assert.Equal((1, 0), Split(b, main));
        Assert.Equal(1, All(b, new[] { main }).FromHeader);
    }
}
