using CodeCarver.Cli;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Branches of the 1.0.160 changes the first coverage run did not reach.</summary>
public sealed class CoverageGapTests
{
    static NodeId Fn(CodeGraph g, string name, string file, NodeFlags flags = NodeFlags.None)
    {
        var f = g.GetOrAddNode(NodeKind.File, file);
        var id = g.GetOrAddNode(NodeKind.Function, name, file, new SourceSpan(1, 1), flags);
        g.AddEdge(id, f, EdgeKind.DefinedIn);
        return id;
    }

    [Fact]
    public void ClassifyViolations_NamesEveryCause()
    {
        var g = new CodeGraph();
        var root = Fn(g, "root", "use.c");
        var caller = Fn(g, "caller", "use.c");                 // never reached
        Fn(g, "loc", "def.c", NodeFlags.FileLocal);
        Fn(g, "unmod", "def.c");
        Fn(g, "unp", "def.c");
        var unr = Fn(g, "unr", "def.c");
        var oth = Fn(g, "oth", "def.c");
        g.AddEdge(caller, unr, EdgeKind.Calls);
        g.AddEdge(root, oth, EdgeKind.Calls);
        var plan = ReachabilityEngine.Compute(g, new[] { new Root(root, RootKind.EntryPoint) });

        LinkViolation V(string name, string refIn = "use.c") => new(name, "def.c", refIn, 1, false);
        var vs = new[] { V("notrec"), V("loc"), V("unmod"), V("unmod", "hdr.h"), V("unp", "unp.c"), V("unr"), V("oth") };
        var why = CarveCommand.ClassifyViolations(g, plan, vs, new HashSet<string> { "unp.c" });

        Assert.Equal("definitionNotRecognized", why[vs[0]]);
        Assert.Equal("definitionFileLocal", why[vs[1]]);
        Assert.Equal("useNotModelled", why[vs[2]]);
        Assert.Equal("useInHeaderNotModelled", why[vs[3]]);
        Assert.Equal("useInUnparsedFile", why[vs[4]]);
        Assert.Equal("useInUnreachedCode", why[vs[5]]);
        Assert.Equal("other", why[vs[6]]);
    }

    static CodeGraph Graph(string path, string src)
    {
        using var fe = path.EndsWith(".c") ? (TreeSitterFrontEnd)new CFrontEnd() : new CppFrontEnd();
        return fe.BuildGraph(new[] { (path, src) });
    }

    static Node? Function(CodeGraph g, string name) => g.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Function && n.Name == name);

    [Fact]
    public void SplitHead_ReturningAPointer_IsRecovered()
    {
        var g = Graph("s.c", "#ifdef A\nchar *name_a(int x)\n#else\nchar *name_b(int x)\n#endif\n{\n  return 0;\n}\n");
        Assert.NotNull(Function(g, "name_a"));
        Assert.NotNull(Function(g, "name_b"));
    }

    [Fact]
    public void SplitHead_InsideANamespace_IsRecovered()
    {
        var g = Graph("s.cpp", "namespace n {\n#ifdef A\nvoid h_a(int x)\n#else\nvoid h_b(int x)\n#endif\n{\n  (void)x;\n}\n}\n");
        Assert.NotNull(Function(g, "h_a"));
        Assert.NotNull(Function(g, "h_b"));
    }

    [Fact]
    public void SplitHead_InABlockWithOtherCode_StaysWithItsFile()
    {
        // The #if block also declares something else: removing the span would take that with it, so the heads are
        // kept with the file instead of being removable on their own.
        var g = Graph("s.c", "#ifdef A\nint other_decl;\nvoid h_a(int x)\n#else\nvoid h_b(int x)\n#endif\n{\n  (void)x;\n}\nint keep(void){ return 0; }\n");
        var file = g.Nodes.First(n => n.Kind == NodeKind.File && n.Name == "s.c");
        var plan = ReachabilityEngine.Compute(g, new[] { new Root(Function(g, "keep")!.Id, RootKind.EntryPoint) });
        Assert.True(plan.IsKept(file.Id));
        Assert.True(plan.IsKept(Function(g, "h_a")!.Id));
    }

    [Fact]
    public void DefinerMacro_DefinedObject_StaysWithItsFile_DefinedFunctionDoesNot()
    {
        // `static Registrar n##_reg(...)` registers at start-up: never removable while its file is kept.
        const string src = "struct Registrar { Registrar(int); };\n"
            + "#define REGISTER(n) static Registrar n##_reg(1)\n#define TASK(n) void n(void)\n"
            + "REGISTER(alpha);\nTASK(beta) { }\nint keep(void){ return 0; }\n";
        var g = Graph("r.cpp", src);
        var plan = ReachabilityEngine.Compute(g, new[] { new Root(Function(g, "keep")!.Id, RootKind.EntryPoint) });
        var reg = g.Nodes.First(n => n.Kind == NodeKind.Global && n.Name == "alpha_reg");
        Assert.True(plan.IsKept(reg.Id));
        Assert.False(plan.IsKept(Function(g, "beta")!.Id));
    }
}

/// <summary>The fixes from the code review of 1.0.160.</summary>
public sealed class ReviewFixTests
{
    [Fact]
    public void DefinerBuild_SkipsTypedefs_AndCommentWords()
    {
        var bodies = new Dictionary<string, (bool, List<string>)>
        {
            ["DECLARE_TYPE"] = (true, new() { "n) typedef struct n##_s n##_t" }),
            ["DEFINE_LOCK"] = (true, new() { "n) static lock_t n##_lock /* protects n state */" }),
            ["SAY"] = (true, new() { "n) static const char *n##_msg = \"the n is\"" }),
        };
        var d = DefinerMacros.Build(bodies);
        Assert.False(d.ContainsKey("DECLARE_TYPE"));
        Assert.Equal(new DefinerMacros.Template(0, "", "_lock", false), Assert.Single(d["DEFINE_LOCK"]));
        Assert.Equal(new DefinerMacros.Template(0, "", "_msg", false), Assert.Single(d["SAY"]));
    }

    [Fact]
    public void AnalysisVerify_AgreesWithANormalEmit_WhenAnIncludedTableCallsOut()
    {
        // A kept .c includes a non-graph tables.inc that calls helper(), which only a dropped file defines: the
        // analysis run's verify must see tables.inc (an emit writes it), so the carried-over result is a failure.
        var root = Path.Combine(Path.GetTempPath(), "cc-closure-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){ return 0; }\nvoid table(void){\n#include \"tables.inc\"\n}\n");
            File.WriteAllText(Path.Combine(src, "tables.inc"), "helper();\n");
            File.WriteAllText(Path.Combine(src, "helper.c"), "int helper(void){ return 0; }\n");
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace("\\", "/")}\"\nanalysisOnly = true\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n");
            var so = new StringWriter(); var se = new StringWriter();
            var analysis = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            var normal = Path.Combine(root, "normal.toml");
            File.WriteAllText(normal, File.ReadAllText(cfg).Replace("analysisOnly = true", "analysisOnly = false").Replace("/out\"", "/out2\""));
            var emitted = CarveCommand.Run(new[] { "carve", src, "--config", normal }, new StringWriter(), new StringWriter());
            // Whatever a normal emit concludes about this tree, the analysis run must conclude the same.
            Assert.True(analysis == emitted, $"analysis exit {analysis}, normal emit exit {emitted}\n{so}\n{se}");
        }
        finally { TempDir.Delete(root); }
    }
}
