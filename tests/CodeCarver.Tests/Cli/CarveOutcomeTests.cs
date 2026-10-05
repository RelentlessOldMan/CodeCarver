using CodeCarver.Core.Emit;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// The compiler-free half of each build-verify case (review TS2). Each case's setup lives in
/// <see cref="CarveCases"/> so the <c>X_Carve</c> test here (always runs, in CI) and the <c>X_Builds</c> test in
/// <c>BuildVerifyTests</c> (gated on a fetched toolchain) carve exactly the same input. Everything that does not
/// need a compiler — kept/dropped files, emitted text, golden-file equality, the emitted-tree link check — is
/// asserted here, so a toolchain-less run still catches a regression in what the carve emits.
/// </summary>
public sealed class CarveOutcomeTests
{
    [Theory]
    [InlineData("carved-base64-encode", "sl_base64_encode")]
    [InlineData("carved-sanitize", "sl_trim,sl_to_upper")]
    public void Example_Stringlib_CheckedInCarveIsUpToDate_Carve(string outName, string roots)
    {
        // The examples/stringlib carved trees are checked in as documentation. Regenerate them and assert they
        // match byte-for-byte (modulo line endings), so they can never silently drift from the tool.
        using var c = CarveCases.StringlibGolden(outName, roots);

        static string Norm(string s) => s.Replace("\r\n", "\n");
        var checkedIn = Directory.EnumerateFiles(c.Golden!, "*.*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(checkedIn);
        foreach (var f in checkedIn)
        {
            var rel = Path.GetRelativePath(c.Golden!, f);
            var regen = Path.Combine(c.Out, rel);
            Assert.True(File.Exists(regen), $"{outName}/{rel} is checked in but the carve no longer emits it — regenerate the example");
            Assert.True(Norm(File.ReadAllText(f)) == Norm(File.ReadAllText(regen)),
                $"{outName}/{rel} is stale — re-run the carve and commit the updated example");
        }
        var regenCount = Directory.EnumerateFiles(c.Out, "*.*", SearchOption.AllDirectories).Count();
        Assert.Equal(checkedIn.Count, regenCount); // no files emitted that aren't checked in

        // The dropped files are really gone, and nothing emitted calls into them (compiler-free link check).
        Assert.NotEmpty(c.Plan.DroppedFiles);
        foreach (var d in c.Plan.DroppedFiles) Assert.False(File.Exists(Path.Combine(c.Out, d)), $"{d} was dropped but emitted");
        CarveCases.AssertEmittedLinkClean(c);
    }

    [Fact]
    public void PrunedCarve_StillCompilesAndLinks_Carve()
    {
        using var c = CarveCases.PrunedAppUtil();

        var app = c.ReadOut("app.c");
        var util = c.ReadOut("util.c");
        Assert.DoesNotContain("unused", app);       // dead function pruned from a kept file
        Assert.DoesNotContain("orphan", util);      // ...and from the other kept file
        Assert.Contains("int used(void)", app);     // the live chain survives
        Assert.Contains("int main(void)", app);
        Assert.Contains("int helper(void)", util);
        Assert.True(File.Exists(Path.Combine(c.Out, "util.h")));
        Assert.Empty(c.Plan.DroppedFiles);
    }

    [Fact]
    public void PrunedCarve_KeepsFileScopeTableHandlers_Carve()
    {
        // Handlers referenced only from a file-scope table must not be pruned, or the table initializer
        // references a missing symbol.
        using var c = CarveCases.DispatchTable();

        var pruned = c.ReadOut("dispatch.c");
        Assert.DoesNotContain("never_used", pruned);                  // genuinely dead -> pruned
        Assert.Contains("static void handler_a(void)", pruned);       // kept via the table
        Assert.Contains("static void handler_b(void)", pruned);
        Assert.Contains("table[] = { handler_a, handler_b }", pruned); // the table itself survives
        Assert.Contains("void run(void)", pruned);
    }

    [Fact]
    public void HeaderCarve_OfBigRegisterHeader_ShrinksAndStillCompiles_Carve()
    {
        using var c = CarveCases.BigRegisterHeader();
        var carved = c.ReadOut("chip.h");

        Assert.Contains("#define REG_2000", carved);        // needed
        Assert.Contains("#define CHIP_BASE", carved);       // pulled in by REG_2000's body
        Assert.DoesNotContain("#define REG_2001", carved);  // unused -> dropped
        Assert.DoesNotContain("#define REG_0 ", carved);
        Assert.Contains("#ifndef CHIP_H", carved);          // include guard survives
        Assert.Contains("#endif", carved);
        Assert.True(c.HeaderResult!.BytesAfter < c.HeaderBytesBefore / 10,
            $"expected big shrink, {c.HeaderBytesBefore} -> {c.HeaderResult.BytesAfter}");
        Assert.Equal(new FileInfo(Path.Combine(c.Out, "chip.h")).Length, c.HeaderResult.BytesAfter);

        var app = c.ReadOut("app.c");
        Assert.Contains("int use(void)", app);
        Assert.DoesNotContain("dead", app);                 // app.c's unreached function pruned
    }

    [Fact]
    public void LinkerKeepSection_SurvivesCarve_Carve()
    {
        // A symbol kept ONLY by the linker script's KEEP(*(.init_calls*)): the carve must root it via
        // LinkerSectionRootProvider, while a dead symbol in the same file is still carved.
        using var c = CarveCases.CortexmKeepSection();

        Assert.Contains(c.Node("reg_table"), c.Plan.Reached);
        Assert.Contains(c.Node("boot_step_a"), c.Plan.Reached);
        Assert.DoesNotContain(c.Node("boot_step_dead"), c.Plan.Reached);

        var registry = c.ReadOut("registry.c");
        Assert.Contains("reg_table[] = { boot_step_a }", registry);    // the KEEP'd table survives
        Assert.Contains("static void boot_step_a(void)", registry);    // and its hook target
        Assert.DoesNotContain("void boot_step_dead(void)", registry);   // the unreferenced sibling is carved
        foreach (var f in new[] { "startup.c", "main.c", "handlers.c", "registry.c", "firmware.ld" })
            Assert.True(File.Exists(Path.Combine(c.Out, f)), $"{f} must be emitted for the image to link");
        CarveCases.AssertEmittedLinkClean(c);
    }

    [Fact]
    public void Cpp_ControlFlowMacro_TryCatch_NotPrunedAsNestedFunction_Carve()
    {
        using var c = CarveCases.CppTryCatch();
        var carved = c.ReadOut("app.cpp");
        Assert.Contains("APP_TRY {", carved);
        Assert.Contains("APP_CATCH(...) {}", carved);   // the catch macro survived (not pruned as a "function")
        Assert.Contains("return -1;", carved);          // ...and so did the rest of handle()
        Assert.Contains("int risky() { return 1; }", carved);
        Assert.DoesNotContain("dead", carved);          // genuine dead code still pruned
        Assert.Equal(CarveCases.TryCatchMacrosH, c.ReadOut("macros.h"));
    }

    [Fact]
    public void Cpp_PruneFirstClassMember_AndHeaderInline_StillCompiles_Carve()
    {
        using var c = CarveCases.CppFirstClassMember();

        // The header is emitted whole (inline method retained); the class opening survived.
        var hdr = c.ReadOut("widget.h");
        Assert.Equal(CarveCases.WidgetH, hdr);
        var app = c.ReadOut("app.cpp");
        Assert.DoesNotContain("dead", app);                               // TU still pruned
        Assert.Contains("int run(Widget* w) { return w->keep(); }", app);
        Assert.Contains("#include \"widget.h\"", app);
    }
}

/// <summary>A carved build-verify case: its temp work dir, the source it carved, the emitted tree, and the plan.</summary>
public sealed class CarvedCase : IDisposable
{
    public required TempDir Work { get; init; }
    public required string Src { get; init; }
    public required string Out { get; init; }
    public required CodeGraph Graph { get; init; }
    public required CarvePlan Plan { get; init; }
    public string? Golden { get; init; }
    public long HeaderBytesBefore { get; init; }
    public HeaderCarveResult? HeaderResult { get; init; }

    public string ReadOut(string rel) => File.ReadAllText(Path.Combine(Out, rel));
    public NodeId Node(string name) => Graph.Nodes.First(n => n.Name == name).Id;
    public void Dispose() => Work.Dispose();
}

/// <summary>
/// Shared setup for the split build-verify cases (review TS2). Each method carves one fixture into a fresh temp
/// directory and returns it; <c>CarveOutcomeTests.X_Carve</c> asserts on the emitted tree and
/// <c>BuildVerifyTests.X_Builds</c> compiles the very same tree.
/// </summary>
public static class CarveCases
{
    public static CarvedCase StringlibGolden(string outName, string roots)
    {
        var ex = TestRepo.Example("stringlib");
        var src = Path.Combine(ex, "src");
        var golden = Path.Combine(ex, outName);
        Assert.True(Directory.Exists(golden), $"examples/stringlib/{outName} is missing from the checkout");

        var work = new TempDir("cc-ex-");
        var outDir = work.Sub("out");
        var inputs = Directory.EnumerateFiles(src, "*.*")
            .Where(p => p.EndsWith(".c") || p.EndsWith(".h"))
            .Select(p => (Path.GetFileName(p)!, File.ReadAllText(p))).ToList();
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(inputs);
        var plan = ReachabilityEngine.Compute(graph,
            new ExplicitRootProvider(symbols: roots.Split(',')).Discover(graph).ToList());
        FileTreeEmitter.EmitPruned(plan, graph, src, outDir);
        return new CarvedCase { Work = work, Src = src, Out = outDir, Graph = graph, Plan = plan, Golden = golden };
    }

    public const string AppC = """
        #include "util.h"

        int used(void) {
            return helper();
        }

        int unused(void) {
            return 999;
        }

        int main(void) {
            return used();
        }
        """;

    public const string UtilH = """
        #ifndef UTIL_H
        #define UTIL_H
        int helper(void);
        int used(void);
        #endif
        """;

    public const string UtilC = """
        #include "util.h"

        int helper(void) {
            return 1;
        }

        int orphan(void) {
            return 7;
        }
        """;

    public static CarvedCase PrunedAppUtil()
    {
        var work = new TempDir("cc-bv-");
        var src = work.Sub("src");
        var outDir = work.Sub("out");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "app.c"), AppC);
        File.WriteAllText(Path.Combine(src, "util.h"), UtilH);
        File.WriteAllText(Path.Combine(src, "util.c"), UtilC);

        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("app.c", AppC), ("util.h", UtilH), ("util.c", UtilC) });
        var main = graph.Nodes.First(n => n.Name == "main").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(main, RootKind.EntryPoint) });
        FileTreeEmitter.EmitPruned(plan, graph, src, outDir);
        return new CarvedCase { Work = work, Src = src, Out = outDir, Graph = graph, Plan = plan };
    }

    public const string DispatchC = """
        typedef void (*fn_t)(void);

        static int g_count;
        static void handler_a(void) { g_count += 1; }
        static void handler_b(void) { g_count += 2; }
        static void never_used(void) { g_count += 999; }

        static fn_t table[] = { handler_a, handler_b };

        void run(void) {
            for (int i = 0; i < 2; i++) table[i]();
        }
        """;

    public static CarvedCase DispatchTable()
    {
        var work = new TempDir("cc-disp-");
        var src = work.Sub("src");
        var outDir = work.Sub("out");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "dispatch.c"), DispatchC);

        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("dispatch.c", DispatchC) });
        var run = graph.Nodes.First(n => n.Name == "run").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(run, RootKind.ExplicitSymbol) });
        FileTreeEmitter.EmitPruned(plan, graph, src, outDir);
        return new CarvedCase { Work = work, Src = src, Out = outDir, Graph = graph, Plan = plan };
    }

    /// <summary>A big auto-generated register header (passed empty = "too big to parse", kept whole via
    /// #include-closure) carved down to the #defines the code transitively needs.</summary>
    public static CarvedCase BigRegisterHeader()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("#ifndef CHIP_H\n#define CHIP_H\n#define CHIP_BASE 0x40000000\n");
        for (var i = 0; i < 5000; i++) sb.Append($"#define REG_{i} (CHIP_BASE + 0x{i * 4:X})\n");
        sb.Append("#endif\n");
        var chipH = sb.ToString();
        const string appC = "#include \"chip.h\"\nint use(void){ return REG_2000; }\nint dead(void){ return 0; }\n";

        var work = new TempDir("cc-hc-");
        var src = work.Sub("src");
        var outDir = work.Sub("out");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "app.c"), appC);
        File.WriteAllText(Path.Combine(src, "chip.h"), chipH);

        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("app.c", appC), ("chip.h", "") }); // chip.h empty = not parsed
        var plan = ReachabilityEngine.Compute(graph,
            new ExplicitRootProvider(symbols: new[] { "use" }).Discover(graph).ToList());
        FileTreeEmitter.EmitPruned(plan, graph, src, outDir); // copies chip.h whole, prunes app.c's dead()

        var before = new FileInfo(Path.Combine(outDir, "chip.h")).Length;
        var res = HeaderCarver.Carve(outDir, new[] { "chip.h" });
        return new CarvedCase
        {
            Work = work, Src = src, Out = outDir, Graph = graph, Plan = plan,
            HeaderBytesBefore = before, HeaderResult = res,
        };
    }

    public static readonly string[] CortexmSources = { "startup.c", "main.c", "handlers.c", "registry.c" };

    /// <summary>examples/cortexm-firmware carved from Reset_Handler/main with the roots the CLI composes for an
    /// embedded C carve, including the KEEP'd-section provider driven by firmware.ld; the .ld is emitted too.</summary>
    public static CarvedCase CortexmKeepSection()
    {
        var fixture = TestRepo.Example("cortexm-firmware");
        var inputs = CortexmSources.Select(f => (f, File.ReadAllText(Path.Combine(fixture, f)))).ToList();
        var linker = File.ReadAllText(Path.Combine(fixture, "firmware.ld"));

        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(inputs);
        var roots = new ExplicitRootProvider(symbols: new[] { "Reset_Handler", "main" }).Discover(graph)
            .Concat(new AttributeRootProvider().Discover(graph))
            .Concat(new LinkerSectionRootProvider(inputs.Select(i => i.Item2), new[] { linker }).Discover(graph))
            .ToList();
        var plan = ReachabilityEngine.Compute(graph, roots);

        var work = new TempDir("cc-keep-");
        var outDir = work.Sub("out");
        FileTreeEmitter.EmitPruned(plan, graph, fixture, outDir);
        BuildSupportEmitter.Copy(fixture, outDir, plan.KeptFiles, Array.Empty<string>(), Array.Empty<string>()); // emit .ld like the CLI
        return new CarvedCase { Work = work, Src = fixture, Out = outDir, Graph = graph, Plan = plan };
    }

    public const string TryCatchMacrosH = "#define APP_TRY try\n#define APP_CATCH(x) catch (x)\n";

    /// <summary>fmt-style try/catch macros: tree-sitter parses <c>APP_CATCH(...) {}</c> as a nested function
    /// definition; the front-end must not capture it (which would prune the catch).</summary>
    public static CarvedCase CppTryCatch()
    {
        const string appCpp = """
            #include "macros.h"
            int risky();
            int handle(int x) {
                APP_TRY {
                    return risky();
                }
                APP_CATCH(...) {}
                return -1;
            }
            int risky() { return 1; }
            int dead() { return 99; }
            """;
        var work = new TempDir("cc-cpptc-");
        var src = work.Sub("src");
        var outDir = work.Sub("out");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "macros.h"), TryCatchMacrosH);
        File.WriteAllText(Path.Combine(src, "app.cpp"), appCpp);

        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("macros.h", TryCatchMacrosH), ("app.cpp", appCpp) });
        var plan = ReachabilityEngine.Compute(graph,
            new ExplicitRootProvider(symbols: new[] { "handle" }).Discover(graph).ToList());
        FileTreeEmitter.EmitPruned(plan, graph, src, outDir);
        return new CarvedCase { Work = work, Src = src, Out = outDir, Graph = graph, Plan = plan };
    }

    public const string WidgetH = """
        #ifndef WIDGET_H
        #define WIDGET_H
        class Widget {
        public:
            Widget() {}                 // first member — unreached; must not break the class
            int unused_inline() const { return 42; }  // header inline — must NOT be pruned
            int keep() const { return 7; }
        };
        #endif
        """;

    /// <summary>Two C++ pruning-soundness regressions: pruning a class's FIRST member must not delete the
    /// enclosing <c>class X {</c>, and an unreached inline method in a HEADER must not be pruned.</summary>
    public static CarvedCase CppFirstClassMember()
    {
        const string appCpp = """
            #include "widget.h"
            int run(Widget* w) { return w->keep(); }
            int dead(Widget* w) { return w->keep() + 1; } // unreached — pruned from this TU
            """;
        var work = new TempDir("cc-cpp-");
        var src = work.Sub("src");
        var outDir = work.Sub("out");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "widget.h"), WidgetH);
        File.WriteAllText(Path.Combine(src, "app.cpp"), appCpp);

        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("widget.h", WidgetH), ("app.cpp", appCpp) });
        var plan = ReachabilityEngine.Compute(graph,
            new ExplicitRootProvider(symbols: new[] { "run" }).Discover(graph).ToList());
        FileTreeEmitter.EmitPruned(plan, graph, src, outDir);
        return new CarvedCase { Work = work, Src = src, Out = outDir, Graph = graph, Plan = plan };
    }

    /// <summary>The compiler-free stand-in for "it links": the independent emitted-tree check (review V1) finds
    /// no emitted use of a function that only a dropped file defines.</summary>
    public static void AssertEmittedLinkClean(CarvedCase c)
    {
        var codeExt = new[] { ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".cxx" };
        var emitted = Directory.EnumerateFiles(c.Out, "*", SearchOption.AllDirectories)
            .Where(p => codeExt.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Select(p => (Path.GetRelativePath(c.Out, p).Replace('\\', '/'), p)).ToList();
        var dropped = c.Plan.DroppedFiles.Select(d => (d, Path.Combine(c.Src, d))).ToList();
        var res = EmittedLinkCheck.Run(emitted, dropped);
        Assert.True(res.Hard.Count == 0,
            "emitted code uses functions defined only in dropped files: "
            + string.Join(", ", res.Hard.Select(v => $"{v.Name} ({v.DefinedIn} <- {v.ReferencedIn}:{v.Line})")));
    }
}
