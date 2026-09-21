using CodeCarver.Core.Emit;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Emit;

public class IntraFilePruneTests
{
    [Fact]
    public void EmitPruned_DropsUnusedGlobalTable_KeepsUsedOne()
    {
        // Global-variable pruning: an unused file-scope data table is removed; a used one stays.
        const string src = """
            static const int used_table[4] = { 1, 2, 3, 4 };
            static const int dead_table[4] = { 9, 9, 9, 9 };

            int reader(void) { return used_table[0]; }
            """;

        var work = Path.Combine(Path.GetTempPath(), "codecarver-glob-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "t.c"), src);

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[] { ("t.c", src) });
            var reader = graph.Nodes.First(n => n.Name == "reader").Id;
            var plan = ReachabilityEngine.Compute(graph, new[] { new Root(reader, RootKind.ExplicitSymbol) });

            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            var result = File.ReadAllText(Path.Combine(outDir, "t.c"));
            Assert.Contains("used_table", result);
            Assert.DoesNotContain("dead_table", result); // the unused table's definition is gone
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void EmitPruned_RemovesUnreachedFunction_KeepsReachedOnes()
    {
        const string aC = """
            int helper(void) {
                return 1;
            }

            int keep_me(void) {
                return helper();
            }

            int drop_me(void) {
                return 999;
            }
            """;

        var work = Path.Combine(Path.GetTempPath(), "codecarver-prune-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "a.c"), aC);

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[] { ("a.c", aC) });
            var keepMe = graph.Nodes.First(n => n.Name == "keep_me").Id;
            var plan = ReachabilityEngine.Compute(graph, new[] { new Root(keepMe, RootKind.ExplicitSymbol) });

            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            var result = File.ReadAllText(Path.Combine(outDir, "a.c"));
            Assert.Contains("keep_me", result);
            Assert.Contains("helper", result);       // reached via keep_me -> helper
            Assert.DoesNotContain("drop_me", result); // unreached -> removed
            Assert.DoesNotContain("999", result);     // its body is gone too
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}
