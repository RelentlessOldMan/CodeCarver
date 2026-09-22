using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>The compiler-free soundness gate: a KEPT function calling an in-scope function that the carve
/// dropped is a link error waiting to happen — the check must flag exactly that, and ignore external calls.</summary>
public class SoundnessCheckTests
{
    [Fact]
    public void FlagsKeptFunctionCallingDroppedInScopeFunction_IgnoresExternal()
    {
        var b = new GraphBuilder();
        var a = b.Func("a", "a.c");        // root -> kept
        var missed = b.Func("missed", "b.c"); // in-scope, but NO edge from a (a blind-spot miss) -> dropped

        var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(a, RootKind.ExplicitSymbol) });
        Assert.False(plan.IsKept(missed)); // nothing kept it

        // Raw call sites as the front-end would report them: a() textually calls missed() and printf(),
        // but the graph never got an a->missed edge (that's the bug the gate exists to catch).
        var callSites = new (NodeId, string)[] { (a, "missed"), (a, "printf") };
        var violations = SoundnessCheck.KeptCallingDropped(b.Graph, plan, callSites);

        Assert.Single(violations);                 // printf is external -> not flagged
        Assert.Equal("a", violations[0].Caller);
        Assert.Equal("missed", violations[0].Callee);
    }

    [Fact]
    public void CleanCarve_HasNoViolations()
    {
        var b = new GraphBuilder();
        var a = b.Func("a", "a.c");
        var bb = b.Func("b", "a.c");
        b.Calls(a, bb); // edge present -> reachability keeps b

        var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(a, RootKind.ExplicitSymbol) });
        var callSites = new (NodeId, string)[] { (a, "b") };

        Assert.Empty(SoundnessCheck.KeptCallingDropped(b.Graph, plan, callSites));
    }

    [Fact]
    public void DroppedCaller_IsNotChecked()
    {
        // A call FROM a dropped function is irrelevant — that function won't be compiled.
        var b = new GraphBuilder();
        var root = b.Func("root", "a.c");
        var deadCaller = b.Func("deadCaller", "a.c");
        var deadCallee = b.Func("deadCallee", "a.c");

        var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(root, RootKind.ExplicitSymbol) });
        Assert.False(plan.IsKept(deadCaller));

        var callSites = new (NodeId, string)[] { (deadCaller, "deadCallee") };
        Assert.Empty(SoundnessCheck.KeptCallingDropped(b.Graph, plan, callSites));
    }
}
