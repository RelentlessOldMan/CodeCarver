using System.Linq;
using CodeCarver.Core.Frontend;
using CodeCarver.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// In-process ground-truth carve oracle: a small synthetic firmware TU whose call graph is KNOWN, wired
/// with the same root providers the CLI uses, then asserted against the true reachable/dead sets. This is
/// the CI-safe (no compiler / no PowerShell / no WSL) permanent gate for the full-scale synthetic oracle
/// in make-carver-corpus.ps1 + carver-oracle.ps1: it locks in that a carve rooted at main keeps exactly
/// the reachable closure PLUS the implicit-root closures (vector table, constructor) and drops the dead
/// chain -- the correctness property, not just "it didn't crash".
/// </summary>
public class GroundTruthOracleTests
{
    // main -> midd -> leaf                (reachable)
    // dead_a -> dead_b                    (dead: nothing reachable calls them)
    // g_vectors[] (used, .isr_vector) -> isr_timer -> isr_helper   (implicit root: vector table)
    // __attribute__((constructor)) boot_ctor -> ctor_helper        (implicit root: constructor)
    private const string Firmware = """
        unsigned leaf(unsigned x) { return x + 1u; }
        unsigned midd(unsigned x) { return leaf(x); }
        int main(void) { return (int)midd(5); }

        unsigned dead_b(unsigned x) { return x; }
        unsigned dead_a(unsigned x) { return dead_b(x); }

        unsigned isr_helper(unsigned x) { return x * 3u; }
        void isr_timer(void) { volatile unsigned v = isr_helper(1); (void)v; }
        void (* const g_vectors[])(void) __attribute__((section(".isr_vector"), used)) = { isr_timer };

        unsigned ctor_helper(unsigned x) { return x + 9u; }
        __attribute__((constructor)) void boot_ctor(void) { volatile unsigned v = ctor_helper(2); (void)v; }
        """;

    [Fact]
    public void CarveRootedAtMain_KeepsReachableAndImplicitRoots_DropsDead()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("fw.c", Firmware) });

        // Same roots the CLI gathers: the explicit entry PLUS auto-discovered implicit roots (a `used`
        // vector table global and the `constructor` function are flagged Keep by the front-end).
        var roots = new ExplicitRootProvider(symbols: new[] { "main" }).Discover(graph)
            .Concat(new AttributeRootProvider().Discover(graph)).ToList();
        var plan = ReachabilityEngine.Compute(graph, roots);

        NodeId Id(string n) => graph.Nodes.First(x => x.Kind == NodeKind.Function && x.Name == n).Id;

        // reachable-from-main
        foreach (var keep in new[] { "main", "midd", "leaf" })
            Assert.True(plan.IsKept(Id(keep)), $"reachable '{keep}' must be kept");
        // implicit-root closures (would fail to link / not boot if dropped)
        foreach (var keep in new[] { "isr_timer", "isr_helper", "boot_ctor", "ctor_helper" })
            Assert.True(plan.IsKept(Id(keep)), $"implicit-root '{keep}' must be kept");
        // dead chain must be pruned
        foreach (var drop in new[] { "dead_a", "dead_b" })
            Assert.False(plan.IsKept(Id(drop)), $"dead '{drop}' must be dropped");
    }
}
