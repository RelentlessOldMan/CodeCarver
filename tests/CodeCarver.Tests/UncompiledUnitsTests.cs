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

    static UncompiledUnits.Result All(GraphBuilder b, NodeId[] roots, bool linkCovers = false, RootKind kind = RootKind.ExplicitSymbol) =>
        UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, roots.Select(r => new Root(r, kind))),
                              Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal),
                              linkCovers ? _ => false : null, archiveMayHold: !linkCovers);

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
        Assert.Equal(1, All(b, new[] { entry }).ByNamedRoot);
        // The links take every object the log didn't write and none is its, and no archive could hold it: another target's name.
        var notLinked = UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, new[] { new Root(entry, RootKind.ExplicitSymbol) }),
            Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal), _ => false, archiveMayHold: false, rootSource: _ => "config");
        Assert.Equal((0, 1, 0, 1), (notLinked.Needed, notLinked.Variants, notLinked.ByNamedRoot, notLinked.NamedRootNotLinked));
        Assert.Equal(1, notLinked.NamedBy!["config"]);
        // An archive the log didn't build may hold it, or its object is one the links take: the entry point is there.
        Assert.Equal(1, UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, new[] { new Root(entry, RootKind.ExplicitSymbol) }),
            Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal), _ => false, archiveMayHold: true).ByNamedRoot);
        Assert.Equal(1, UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, new[] { new Root(entry, RootKind.ExplicitSymbol) }),
            Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal), f => f == "lost.c", archiveMayHold: false).ByNamedRoot);
        var b2 = new GraphBuilder();
        b2.Func("kq_entry", "main.c");
        var copy = b2.Func("kq_entry", "lost.c");
        Assert.Equal((0, 1), Split(b2, copy));
    }

    [Fact]
    public void AnImplicitRoot_InAFileNoLinkTakes_IsNotNeeded()
    {
        var b = new GraphBuilder();
        var reg = b.Func("kq_drv_register", "lost.c");   // a self-registering driver of another chip
        Assert.Equal((1, 0), (All(b, new[] { reg }, kind: RootKind.LinkerKeep) is var r ? (r.Needed, r.Variants) : default));
        Assert.Equal((0, 1), (All(b, new[] { reg }, linkCovers: true, kind: RootKind.LinkerKeep) is var c ? (c.Needed, c.Variants) : default));
        // Its object linked whole, a stale one the log didn't write: its roots are in the image.
        var whole = UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, new[] { new Root(reg, RootKind.LinkerKeep) }),
            Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal), f => f == "lost.c", archiveMayHold: false);
        Assert.Equal(1, whole.ByRoot);
    }

    [Fact]
    public void AUse_CountsWhenAnArchiveTheLogDidNotBuildMayHoldIt()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var only = b.Func("kq_only", "lost.c");
        b.Calls(main, only);
        var r = UncompiledUnits.Split(b.Graph, ReachabilityEngine.Compute(b.Graph, new[] { new Root(main, RootKind.ExplicitSymbol) }),
            Compiled.Contains, f => f.EndsWith(".c", StringComparison.Ordinal), _ => false, archiveMayHold: true);
        Assert.Equal(1, r.ByCall);
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
