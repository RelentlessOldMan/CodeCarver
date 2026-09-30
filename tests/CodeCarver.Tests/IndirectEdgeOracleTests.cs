using System.Linq;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// Carver-side gate for the INDIRECT-EDGE soundness + precision-tax property that the CodeSpawner corpus
/// v1 additions will later exercise at scale (3-way agreement 2026-09-30: manifest gains per-symbol
/// <c>indirectEdges</c> {target, via∈{fnptr|vector-table|init_array}, dispatched, resolved} + a reachable
/// fraction dial). The engine already models this — a conservative edge (AddressTaken / VtableEntry /
/// InlineAsmRef) is followed by the <see cref="ReachabilityOptions.Safe"/> carve and dropped by
/// <see cref="ReachabilityOptions.MinimalUnsafe"/> — so the two properties are testable in-process today:
///
///   SOUNDNESS : a sound carve keeps EVERY indirect-call target reachable from the roots (dropping one
///               would break the image — the pointer/table entry would call a carved-out function).
///   TAX       : the set kept ONLY because of conservative edges (Safe.reached − Minimal.reached) is the
///               "indirection tax" — over-keep a sound static carve legitimately pays. The corpus adds the
///               runtime <c>dispatched</c> bit the static graph CAN'T carry: a reachable + dispatched:false
///               target is in this tax set and is exactly what a future trace/tightness tier would drop.
///
/// The scale oracle (carver-groundtruth-oracle.ps1) asserts these against the generator's ground truth once
/// the manifest fields ship; this locks the mechanic so a regression can't quietly turn a sound carve unsafe.
/// </summary>
public class IndirectEdgeOracleTests
{
    // main -> mid -> leaf                       (direct chain: kept by BOTH safe and minimal)
    // main -> dispatch, dispatch =&fnptr=> cmd  (cmd reachable ONLY via a conservative AddressTaken edge)
    // isr_vector =&vtbl-analog=> timer_isr       (a second indirect target via a distinct conservative edge)
    // dead_a -> dead_b                          (unreachable: dropped by BOTH)
    private static (CodeGraph Graph, IReadOnlyList<Root> Roots, System.Func<string, NodeId> Id) BuildFirmware()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var mid = b.Func("mid", "main.c");
        var leaf = b.Func("leaf", "leaf.c");
        var dispatch = b.Func("dispatch", "main.c");
        var cmd = b.Func("cmd_handler", "cmd.c", NodeFlags.AddressTaken);   // fnptr/init_array target
        var isrRoot = b.Func("vector_install", "isr.c");
        var timerIsr = b.Func("timer_isr", "isr.c", NodeFlags.AddressTaken); // vector-table target
        var deadA = b.Func("dead_a", "dead.c");
        var deadB = b.Func("dead_b", "dead.c");

        b.Calls(main, mid);
        b.Calls(mid, leaf);
        b.Calls(main, dispatch);
        b.AddressTaken(dispatch, cmd);          // conservative: kept by Safe, dropped by Minimal
        b.Calls(main, isrRoot);
        b.Edge(isrRoot, timerIsr, EdgeKind.InlineAsmRef); // another conservative kind
        b.Calls(deadA, deadB);                  // dead subgraph

        var roots = new List<Root> { new(main, RootKind.EntryPoint, "image entry") };
        NodeId Id(string n) => b.Graph.Nodes.First(x => x.Kind == NodeKind.Function && x.Name == n).Id;
        return (b.Graph, roots, Id);
    }

    [Fact]
    public void SoundCarve_KeepsEveryIndirectTarget()
    {
        var (graph, roots, Id) = BuildFirmware();
        var safe = ReachabilityEngine.Compute(graph, roots, ReachabilityOptions.Safe);

        // direct closure
        foreach (var keep in new[] { "main", "mid", "leaf", "dispatch", "vector_install" })
            Assert.True(safe.IsKept(Id(keep)), $"direct '{keep}' must be kept");
        // indirect targets — a sound carve MUST keep them or the image breaks
        Assert.True(safe.IsKept(Id("cmd_handler")), "AddressTaken target must be kept (sound)");
        Assert.True(safe.IsKept(Id("timer_isr")), "InlineAsmRef target must be kept (sound)");
        // genuinely dead stays dropped
        Assert.False(safe.IsKept(Id("dead_a")));
        Assert.False(safe.IsKept(Id("dead_b")));
    }

    [Fact]
    public void IndirectionTax_IsExactlyTheConservativeOnlyKeeps()
    {
        var (graph, roots, Id) = BuildFirmware();
        var safe = ReachabilityEngine.Compute(graph, roots, ReachabilityOptions.Safe);
        var minimal = ReachabilityEngine.Compute(graph, roots, ReachabilityOptions.MinimalUnsafe);

        // The minimal (unsafe) carve follows only direct edges → it DROPS the indirect targets.
        Assert.False(minimal.IsKept(Id("cmd_handler")), "minimal drops the fnptr target");
        Assert.False(minimal.IsKept(Id("timer_isr")), "minimal drops the vector-table target");
        // …while still keeping the direct chain.
        foreach (var keep in new[] { "main", "mid", "leaf" })
            Assert.True(minimal.IsKept(Id(keep)));

        // The tax = Safe.reached − Minimal.reached is EXACTLY the indirect-only targets. This is the number
        // the corpus's reachable+dispatched:false population will pin as legitimate over-keep.
        var tax = safe.Reached.Except(minimal.Reached).ToHashSet();
        Assert.Contains(Id("cmd_handler"), tax);
        Assert.Contains(Id("timer_isr"), tax);
        Assert.DoesNotContain(Id("main"), tax);
        Assert.DoesNotContain(Id("leaf"), tax);
    }
}
