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

    static string Prune(string src)
    {
        var work = Path.Combine(Path.GetTempPath(), "codecarver-safe-" + Guid.NewGuid().ToString("N"));
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
            return File.ReadAllText(Path.Combine(outDir, "t.c")).Replace("\r\n", "\n");
        }
        finally { TempDir.Delete(work); }
    }

    /// <summary>Directives inside a removed span stay behind; anything but #if structure must not, so such a span
    /// is kept: an X-macro table's rows, an #include in a body, a directive-looking line inside a comment.</summary>
    [Fact]
    public void EmitPruned_SpanHoldingAnIncludeOrACommentedDirective_IsKeptWhole()
    {
        var result = Prune("""
            const int dead_tbl[] = {
            #include "vals.inc"
            };
            int dead_body(int x) {
            #include "body.inc"
                return x;
            }
            void dead_old(void) {
                /* old code:
            #include "gone.h"
                */
            }
            void dead_plain(void) {
            #if 1
                (void)0;
            #endif
            }
            int reader(void) { return 0; }
            """);
        Assert.Contains("const int dead_tbl[] = {\n#include \"vals.inc\"\n};", result);
        Assert.Contains("int dead_body(int x) {\n#include \"body.inc\"", result);
        Assert.Contains("/* old code:\n#include \"gone.h\"\n    */", result);
        Assert.DoesNotContain("dead_plain", result);                         // #if structure alone: removed
    }

    [Fact]
    public void EmitPruned_CommentOrDeclarationSharingALine_IsNotCut()
    {
        var result = Prune("""
            void dead_tail(void) { } /* note that
               continues */
            void later(void); void dead_share(void) { }
            int reader(void) { return 0; }
            """);
        Assert.Contains("void dead_tail(void) { } /* note that\n   continues */", result);
        Assert.Contains("void later(void);", result);
    }

    [Fact]
    public void EmitPruned_RemovedStaticFunction_TakesItsPrototypeAlong()
    {
        var result = Prune("""
            static void helper(void);
            static int other(int a);   /* still used */
            void dead_outer(void) { helper(); }
            static void helper(void) { }
            static int other(int a) { return a; }
            int reader(void) { return other(1); }
            """);
        Assert.DoesNotContain("helper", result);
        Assert.Contains("static int other(int a);", result);
    }

    /// <summary>What the line above a definition attaches to it, through #if blocks and comments.</summary>
    [Fact]
    public void EmitPruned_AttributeUnderIfdef_CommentPrefixed_OrPragma_StaysWithItsFunction()
    {
        var result = Prune("""
            int k1;
            #ifdef USE_RAM
            RAMFUNC
            #endif
            void dead_ram(void) { }
            /* doc */ WEAK
            void dead_weak(void) { }
            #pragma location = ".noinit"
            void dead_placed(void) { }
            #pragma GCC diagnostic ignored "-Wunused"
            void dead_after_mode(void) { }
            #ifdef FEATURE
            int feature_fn(int a,
                           int b) {
                if (a)
                    return b;
                return a;
            }
            #endif
            void dead_after_block(void) { }
            int reader(void) { return k1; }
            """);
        Assert.Contains("RAMFUNC\n#endif\nvoid dead_ram(void)", result);
        Assert.Contains("/* doc */ WEAK\nvoid dead_weak(void)", result);
        Assert.Contains("#pragma location = \".noinit\"\nvoid dead_placed(void)", result);
        Assert.DoesNotContain("dead_after_mode", result);
        Assert.DoesNotContain("dead_after_block", result);   // a complete function in the #if block above isn't an attribute
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
