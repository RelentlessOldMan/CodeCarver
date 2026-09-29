using CodeCarver.Core.Graph;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// The per-file SYMBOL budget is the shape-AGNOSTIC node-explosion backstop: whatever the generated shape,
/// a single file that would mint more definitions than the budget is kept WHOLE instead of blowing up the
/// graph (the eval-#14 ~20 GB cause was one node per #define). Unlike the #define-density skip, this trips
/// on ANY explosion — here we use plain function definitions, a shape the density heuristic would NOT catch.
/// These tests make the mechanism BITE: same input, budget on vs off, asserted by node count.
/// </summary>
public sealed class SymbolBudgetTests
{
    // A header of N distinct function definitions (bodies → real Function captures, not prototypes). This is
    // an "inline-fn-heavy header" shape: passes the #define-density check entirely, yet mints N graph nodes.
    private static string ManyFunctions(int n)
    {
        var sb = new System.Text.StringBuilder(n * 32);
        for (var i = 0; i < n; i++) sb.Append("int fn_").Append(i).Append("(void){return ").Append(i).Append(";}\n");
        return sb.ToString();
    }

    private static int FunctionNodes(CodeGraph g) => g.Nodes.Count(node => node.Kind == NodeKind.Function);

    [Fact]
    public void OverBudget_Header_KeptWhole_NoSymbolsMinted()
    {
        var text = ManyFunctions(300);
        using var fe = new CFrontEnd { PerFileSymbolBudget = 100 };
        var graph = fe.BuildGraph(new[] { ("regs_gen.h", text) });

        // The File node still exists (kept via #include-closure), but not one of its 300 functions was minted.
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.File && n.Name == "regs_gen.h");
        Assert.Equal(0, FunctionNodes(graph));

        // Reported for the CLI's `budget:` line / diag, with the measured symbol count that tripped it.
        var tripped = Assert.Single(fe.SymbolBudgetKeptWhole);
        Assert.Equal("regs_gen.h", tripped.Path);
        Assert.True(tripped.Symbols > 100, $"expected the tripping count to exceed the budget, got {tripped.Symbols}");

        // A header is NOT force-rooted — it rides #include-closure only (over-keeping an unreferenced giant
        // generated header would be a pointless size regression).
        Assert.DoesNotContain("regs_gen.h", fe.ForceKeepFiles);
    }

    [Fact]
    public void UnderBudget_SameInput_CarvedNormally()
    {
        // Budget disabled (0): the identical file parses normally and every function becomes a node — proving
        // the keep-whole above is the BUDGET's doing, not something else about the input. This is the test
        // biting: 300 vs 0 nodes flips purely on the budget.
        var text = ManyFunctions(300);
        using var fe = new CFrontEnd { PerFileSymbolBudget = 0 };
        var graph = fe.BuildGraph(new[] { ("regs_gen.h", text) });

        Assert.Equal(300, FunctionNodes(graph));
        Assert.Empty(fe.SymbolBudgetKeptWhole);
    }

    [Fact]
    public void OverBudget_TranslationUnit_IsForceRooted()
    {
        // A .c over budget must be FORCE-ROOTED, not merely include-closure-kept: a translation unit with no
        // referenced symbol could otherwise be carved away entirely, dropping code we chose not to analyse.
        var text = ManyFunctions(300);
        using var fe = new CFrontEnd { PerFileSymbolBudget = 100 };
        var graph = fe.BuildGraph(new[] { ("generated.c", text) });

        Assert.Equal(0, FunctionNodes(graph));
        Assert.Contains("generated.c", fe.ForceKeepFiles);
        Assert.Single(fe.SymbolBudgetKeptWhole);
    }

    [Fact]
    public void SmallFilesUnderByteFloor_NeverTrip_EvenAtTinyBudget()
    {
        // The byte-floor optimization: a file smaller than budget×2 bytes can't possibly hold budget symbols,
        // so the count is skipped and the file is carved. A handful of functions with a tiny budget must still
        // parse — the floor must never keep-whole a file that's actually under budget.
        var text = ManyFunctions(3); // ~60 bytes, 3 functions
        using var fe = new CFrontEnd { PerFileSymbolBudget = 100 };
        var graph = fe.BuildGraph(new[] { ("small.c", text) });

        Assert.Equal(3, FunctionNodes(graph));
        Assert.Empty(fe.SymbolBudgetKeptWhole);
    }
}
