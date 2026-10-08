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
            TempDir.Delete(work);
        }
    }

    [Fact]
    public void EmitPruned_AttributeMacroOnItsOwnLine_StaysWithItsFunction()
    {
        // An attribute macro on the line above a head is outside the parsed span. Removing the function alone
        // strands it on the next declaration: NORET on a variable is a compile error, WEAK silently makes the next
        // function weak. Such a function is kept whole; spelled-out attributes are inside the span and go with it.
        const string src = """
            #define WEAK __attribute__((weak))
            #define NORET __attribute__((noreturn))
            int used(void) { return 1; }
            WEAK
            int gone_weak(void) { return 3; }
            int next_fn(void) { return 4; }
            NORET /* never returns */
            void gone_noret(void) { for (;;); }
            int table[2] = { 1, 2 };
            __attribute__((weak))
            int gone_spelled(void) { return 5; }
            #define MULTI(a) \
                do { a; } while (0)
            int after_define(void) { return 6; }
            __attribute__((deprecated))
            __attribute__((noinline))
            int gone_two_attrs(void) { return 7; }
            DEPRECATED_MSG("use x") ALIASED(other)
            int gone_two_macros(void) { return 8; }
            int reader(void) { return used() + table[0]; }
            """;

        var work = Path.Combine(Path.GetTempPath(), "codecarver-attr-" + Guid.NewGuid().ToString("N"));
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

            var result = File.ReadAllText(Path.Combine(outDir, "t.c")).Replace("\r\n", "\n");
            Assert.Contains("WEAK\nint gone_weak(void)", result);                   // kept with its attribute
            Assert.Contains("NORET /* never returns */\nvoid gone_noret(void)", result);
            Assert.DoesNotContain("next_fn", result);                               // still pruned
            Assert.DoesNotContain("gone_spelled", result);
            Assert.DoesNotContain("\n__attribute__((weak))\n", result);
            Assert.DoesNotContain("after_define", result);                          // a #define's tail ends it
            Assert.True(!result.Contains("gone_two_attrs") && !result.Contains("__attribute__((deprecated))")
                        && !result.Contains("__attribute__((noinline))"), result);  // attributes go with it
            Assert.Contains("DEPRECATED_MSG(\"use x\") ALIASED(other)\nint gone_two_macros(void)", result);
        }
        finally
        {
            TempDir.Delete(work);
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
            TempDir.Delete(work);
        }
    }
}
