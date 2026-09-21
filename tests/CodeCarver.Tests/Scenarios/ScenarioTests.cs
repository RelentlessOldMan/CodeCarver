using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests.Scenarios;

/// <summary>
/// Runs every <see cref="CarveScenario"/> through the sound carve and asserts its expectations. This
/// is the regression harness for diverse "we only need XYZ" conditions; adding a scenario to
/// <see cref="ScenarioLibrary"/> automatically gets it covered here.
/// </summary>
public class ScenarioTests
{
    public static IEnumerable<object[]> ScenarioNames =>
        ScenarioLibrary.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Scenario_MeetsExpectations(string name)
    {
        var s = ScenarioLibrary.Get(name);
        var plan = ReachabilityEngine.Compute(s.Graph, s.Roots, ReachabilityOptions.Safe);

        foreach (var keep in s.MustKeep)
            Assert.True(IsSymbolKept(s.Graph, plan, keep),
                $"[{name}] '{keep}' must survive the carve but was dropped (soundness failure). Intent: {s.Intent}");

        foreach (var drop in s.MustDrop)
            Assert.False(IsSymbolKept(s.Graph, plan, drop),
                $"[{name}] '{drop}' should have been carved but survived. Intent: {s.Intent}");

        foreach (var file in s.MustDropFiles)
        {
            Assert.Contains(file, plan.DroppedFiles);
            Assert.DoesNotContain(file, plan.KeptFiles);
        }
    }

    [Fact]
    public void EverySymbol_HasAResolvableName()
    {
        // Guards the scenario library itself: every MustKeep/MustDrop name must exist as a node, or a
        // typo would make an assertion vacuously pass.
        foreach (var s in ScenarioLibrary.All)
            foreach (var symbol in s.MustKeep.Concat(s.MustDrop))
                Assert.True(FindSymbol(s.Graph, symbol) is not null,
                    $"[{s.Name}] scenario references unknown symbol '{symbol}'");
    }

    private static bool IsSymbolKept(CodeGraph graph, CarvePlan plan, string name)
    {
        var node = FindSymbol(graph, name)
                   ?? throw new InvalidOperationException($"symbol '{name}' not in graph");
        return plan.IsKept(node);
    }

    /// <summary>First non-file node with the given name (scenario symbol names are unique).</summary>
    private static NodeId? FindSymbol(CodeGraph graph, string name)
    {
        foreach (var n in graph.Nodes)
            if (n.Kind != NodeKind.File && n.Name == name)
                return n.Id;
        return null;
    }
}
