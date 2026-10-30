using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// Determinism is a load-bearing contract for CodeCarver: the same inputs must always produce the same
/// carve, in the same ORDER (kept-file list, dropped-file list, node stats). A firmware team diffing two
/// carves, or a CI gate comparing against a golden manifest, relies on byte-stable output — a stray
/// HashSet-iteration order or dictionary-order leak would surface as spurious diffs. These parse + carve
/// the same multi-file program twice (fresh front-ends) and assert the results are identical.
/// </summary>
public sealed class DeterminismTests
{
    // Cross-file calls, a function pointer (address-taken), a macro-reached callee, and dead code —
    // enough graph shape that any nondeterministic ordering would show up in the kept/dropped lists.
    private static readonly (string, string)[] Program =
    {
        ("main.c", """
            #include "api.h"
            int helper(void){ return 1; }
            static int (*dispatch)(void) = handler;   // address-taken
            int run(void){ return helper() + api_entry(); }
            int dead_a(void){ return 99; }
            int main(void){ return run(); }
            """),
        ("api.c", """
            #include "api.h"
            int api_entry(void){ return LEAF(); }      // reached only through a macro
            int leaf_impl(void){ return 7; }
            int dead_b(void){ return 42; }
            """),
        ("api.h", """
            #ifndef API_H
            #define API_H
            #define LEAF() leaf_impl()
            int api_entry(void);
            int handler(void);
            int leaf_impl(void);
            #endif
            """),
        ("handler.c", "int handler(void){ return 3; }\nint also_dead(void){ return 0; }\n"),
        ("unused.c", "int unused_module(void){ return 5; }\n"),   // a whole dead file: dropped
    };

    private static (CodeGraph Graph, CarvePlan Plan) CarveWithGraph()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(Program);
        var roots = new ExplicitRootProvider(symbols: new[] { "main" }).Discover(graph).ToList();
        return (graph, ReachabilityEngine.Compute(graph, roots));
    }

    private static CarvePlan Carve() => CarveWithGraph().Plan;

    [Fact]
    public void SameInput_ProducesIdenticalKeptAndDroppedOrder()
    {
        var a = Carve();
        var b = Carve();

        // Sequence equality (not set equality) — order must be stable, not just membership.
        Assert.Equal(a.KeptFiles, b.KeptFiles);
        Assert.Equal(a.DroppedFiles, b.DroppedFiles);
        Assert.Equal(a.Stats.ReachedNodes, b.Stats.ReachedNodes);
        Assert.Equal(a.Stats.TotalNodes, b.Stats.TotalNodes);
        Assert.Equal(a.Stats.KeptFiles, b.Stats.KeptFiles);
    }

    [Fact]
    public void Carve_KeepsReachable_DropsDeadCode()
    {
        // Also pins the actual behavior the determinism test rests on, so "identical" can't mean
        // "identically wrong": dead code is dropped, macro/function-pointer-reached code is kept.
        var (graph, plan) = CarveWithGraph();
        var kept = new HashSet<string>(plan.KeptFiles, StringComparer.Ordinal);

        Assert.Contains("main.c", kept);
        Assert.Contains("api.c", kept);       // api_entry reachable from run()
        Assert.Contains("handler.c", kept);   // reached only via address-taken dispatch pointer

        // The drop half of the name (review TS7): the dead file is dropped...
        Assert.Equal(new[] { "unused.c" }, plan.DroppedFiles);
        Assert.DoesNotContain("unused.c", kept);

        // ...and at symbol level every dead function is unreached while every live one is reached, including
        // those reached only through a macro (leaf_impl) or an address-taken pointer (handler).
        bool Reached(string name) => graph.Nodes.Where(n => n.Name == name && n.Kind == NodeKind.Function)
            .Any(n => plan.Reached.Contains(n.Id));
        foreach (var live in new[] { "main", "run", "helper", "api_entry", "leaf_impl", "handler" })
            Assert.True(Reached(live), $"{live} should be reached");
        foreach (var dead in new[] { "dead_a", "dead_b", "also_dead", "unused_module" })
            Assert.False(Reached(dead), $"{dead} should be dropped");
    }

    // Files are parsed on several threads and merged in path order: the graph must be the one a one-file-at-a-time parse
    // builds, node for node and edge for edge, with the same notes. A tree with every shape the per-file pass records
    // outside the graph: statics, parse errors, pastes, keep attributes, includes, a #define-only header.
    private static readonly (string, string)[] Shapes =
    {
        ("a.c", "#include \"a.h\"\nstatic int s_a(void){ return 1; }\nint fa(void){ return s_a() + MODK_CALL(fb); }\n"
              + "__attribute__((used)) int kept_a(void){ return 2; }\nint table_a[] = { 1, 2 };\n"),
        ("b.c", "#include \"a.h\"\nint fb(void){ return fa(); }\nint broken( { return 0;\nint after_broken(void){ return fb(); }\n"),
        ("a.h", "#ifndef A_H\n#define A_H\n#define MODK_CALL(f) f ## _impl()\n#define MODK_GEN(n) int n ## _gen(void)\nint fa(void);\n#endif\n"),
        ("c.c", "#include \"a.h\"\nMODK_GEN(c);\nint fb_impl(void){ return 0; }\nstatic int s_c;\nint use_c(void){ return s_c + fb_impl(); }\n"),
        ("d.c", "int main(void){ extern int fa(void); return fa(); }\n"),
    };

    private static List<string> GraphOf(Func<TreeSitterFrontEnd> make, IReadOnlyList<(string Path, string Text)> files, int threads)
    {
        using var fe = make();
        fe.ParseThreads = threads;
        var g = fe.BuildGraph(files);
        var dump = new List<string>();
        foreach (var n in g.Nodes)
        {
            dump.Add($"N {n.Id.Value} {n.Kind} {n.Name} {n.File} {n.Span.StartLine}-{n.Span.EndLine} {n.Flags}");
            foreach (var e in g.OutEdges(n.Id)) dump.Add($"  E {e.To.Value} {e.Kind}");
        }
        dump.AddRange(fe.Warnings.Select(w => "W " + w));
        dump.AddRange(fe.ForceKeepFiles.Select(f => "K " + f));
        dump.AddRange(fe.UnparsedFiles.OrderBy(f => f, StringComparer.Ordinal).Select(f => "U " + f));
        dump.AddRange(fe.SymbolBudgetKeptWhole.Select(s => $"B {s.Path} {s.Symbols}"));
        dump.AddRange(fe.CallSites.Select(c => $"C {c.From.Value} {c.Name}"));
        dump.Add($"R {fe.DefinitionsRecoveredByScan}");
        return dump;
    }

    [Fact]
    public void ParallelParse_BuildsTheSameGraph_AsOneFileAtATime()
    {
        var one = GraphOf(() => new CFrontEnd(), Shapes, 1);
        Assert.Contains(one, l => l.StartsWith("N ") && l.Contains("FileLocal"));
        Assert.Contains(one, l => l.StartsWith("C "));
        for (var run = 0; run < 3; run++)
            Assert.Equal(one, GraphOf(() => new CFrontEnd(), Shapes, 8));
    }

    [Fact]
    public void ParallelParse_WithBigFilesParsedFirst_BuildsTheSameGraph()
    {
        // Big files are parsed ahead of their turn and merged when the path order reaches them: two of them, between the
        // small ones, each defining statics and calling across files.
        static string Big(string tag)
        {
            var sb = new System.Text.StringBuilder($"#include \"a.h\"\nint {tag}_entry(void){{ return fa() + {tag}_0(); }}\n");
            for (var i = 0; sb.Length < 300_000; i++)
                sb.Append($"static int {tag}_s{i}(int x){{ return x + {i}; }}\nint {tag}_{i}(void){{ return {tag}_s{i}({i}) + fb(); }}\n");
            return sb.ToString();
        }
        var files = Shapes.Concat(new[] { ("b0_big.c", Big("bx")), ("c0_big.c", Big("cx")) })
                          .OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
        var one = GraphOf(() => new CFrontEnd(), files, 1);
        for (var run = 0; run < 3; run++)
            Assert.Equal(one, GraphOf(() => new CFrontEnd(), files, 8));

        using var fe = new CFrontEnd();
        fe.ParseThreads = 8;
        fe.BuildGraph(files);
        Assert.Equal(2, fe.Timing["files.parsedEarly"]);
    }

    [Fact]
    public void SplitParse_FindsTheSameDefinitionsAndUses_AsOnePiece()
    {
        // A big file parsed in pieces cut between top-level definitions: K&R heads, multi-line initializers, comments and
        // strings with braces, #if blocks, extern "C" and continued macros must not take a cut, and the graph must hold
        // the same nodes and edges (by name) as the file parsed whole, the same on 1 thread as on 8.
        var sb = new System.Text.StringBuilder("#include \"a.h\"\n");
        for (var i = 0; i < 400; i++)
        {
            sb.Append($"/* {i}: {{ ; }} */\nstatic int t{i}[] = {{ {i},\n  {i + 1} }};\n");
            if (i % 9 == 0) sb.Append($"#if CFG\nint cfg{i}(void) {{ return {i}; }}\n#endif\n");
            if (i % 11 == 0) sb.Append($"#define M{i}(x) \\\n  ((x) + {i})\n");
            if (i % 13 == 0) sb.Append($"int kr{i}(a)\nint a;\n{{ return a + {i}; }}\n");
            if (i % 17 == 0) sb.Append($"extern \"C\" {{\nint ex{i}(void);\n}}\n");
            sb.Append($"static int s{i}(int x)\n{{\n  const char *m = \"{{ {i}\";\n  return x + m[0];\n}}\n");
            sb.Append($"int f{i}(void)\n{{\n  return s{i}(t{i}[0]) + {(i + 1 < 400 ? $"f{i + 1}()" : "fa()")};\n}}\n\n");
        }
        var files = Shapes.Append(("big.c", sb.ToString())).OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();

        static (List<string> Graph, double Split) Run(IReadOnlyList<(string, string)> files, int threads, bool split)
        {
            using var fe = new CFrontEnd { ParseThreads = threads, SplitMinChars = split ? 16 * 1024 : int.MaxValue, SplitPieceChars = 8 * 1024 };
            var g = fe.BuildGraph(files);
            string Name(NodeId id) { var n = g.GetNode(id); return $"{n.Kind} {n.Name} {n.File} {n.Span.StartLine}-{n.Span.EndLine}"; }
            var dump = g.Nodes.Select(n => "N " + Name(n.Id))
                .Concat(g.Nodes.SelectMany(n => g.OutEdges(n.Id).Select(e => $"E {Name(n.Id)} {e.Kind} {Name(e.To)}")))
                .Concat(fe.Warnings.Select(w => "W " + w)).Concat(fe.ForceKeepFiles.Select(f => "K " + f))
                .Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
            return (dump, fe.Timing["files.parsedSplit"]);
        }
        var whole = Run(files, 1, split: false);
        var one = Run(files, 1, split: true);
        Assert.Equal(1, one.Split);
        Assert.Equal(whole.Graph, one.Graph);
        Assert.Equal(one.Graph, Run(files, 8, split: true).Graph);

        var cuts = TreeSitterFrontEnd.SplitLines(sb.ToString(), 8 * 1024, 16);
        Assert.True(cuts.Count > 4);
        var lines = sb.ToString().Split('\n');
        foreach (var (line, _) in cuts.Skip(1))
            Assert.True(lines[line - 1] is "}" or "" || lines[line - 1].EndsWith(";") || lines[line - 1].EndsWith("*/"), $"cut after '{lines[line - 1]}'");
    }

    /// <summary>The same on real code, when a corpus repo is fetched (.corpus): thousands of definitions, real parse
    /// errors and macros.</summary>
    [SkippableTheory]
    [InlineData("janet", "c")]
    [InlineData("FreeRTOS-Kernel", "c")]
    [InlineData("Monocypher", "c")]
    [InlineData("fmt", "cpp")]
    public void ParallelParse_BuildsTheSameGraph_OnARealRepo(string repoName, string lang)
    {
        var repo = FindUp(Path.Combine(".corpus", repoName));
        Skip.If(repo is null, $".corpus/{repoName} not fetched");
        var ext = lang == "c" ? new[] { ".c", ".h" } : new[] { ".cc", ".cpp", ".h", ".hpp" };
        var files = Directory.EnumerateFiles(repo!, "*", SearchOption.AllDirectories)
            .Where(f => ext.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && new FileInfo(f).Length < 2_000_000)
            .Select(f => (Path.GetRelativePath(repo!, f).Replace('\\', '/'), File.ReadAllText(f)))
            .OrderBy(f => f.Item1, StringComparer.Ordinal).Take(1500).ToList();
        Func<TreeSitterFrontEnd> make = lang == "c" ? () => new CFrontEnd() : () => new CppFrontEnd();
        var one = GraphOf(make, files, 1);
        Assert.Equal(one, GraphOf(make, files, 8));
    }

    private static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, relative);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
