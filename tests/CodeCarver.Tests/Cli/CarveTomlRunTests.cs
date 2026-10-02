using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// End-to-end tests of the redesigned CLI: `carve &lt;src&gt; --config carve.toml [--stage] [--why]`, driven
/// in-process. Covers the config-driven pipeline, the fixed output layout, stages, auto-derived world,
/// always-on verify, auto-exclude, and the per-TU #ifdef soundness that earlier evals caught.
/// </summary>
public sealed class CarveTomlRunTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var so = new StringWriter(); var se = new StringWriter();
        var code = CarveCommand.Run(args, so, se);
        return (code, so.ToString(), se.ToString());
    }

    // Creates a work dir with a src/ tree; returns (workDir, srcDir, outDir).
    private static (string Work, string Src, string Out) NewWork()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-toml-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        return (work, src, Path.Combine(work, "out"));
    }

    private static string Fwd(string p) => p.Replace("\\", "/");
    private static void Cleanup(string work) { try { Directory.Delete(work, true); } catch { } }

    // Writes carve.toml with the given body (outputDirectory is prepended) and returns its path.
    private static string Config(string work, string outDir, string body)
    {
        var path = Path.Combine(work, "carve.toml");
        File.WriteAllText(path, $"outputDirectory = \"{Fwd(outDir)}\"\n{body}");
        return path;
    }

    private static void BasicTree(string src)
    {
        File.WriteAllText(Path.Combine(src, "main.c"), "int helper(void);\nint main(void){return helper();}\n");
        File.WriteAllText(Path.Combine(src, "helper.c"), "int helper(void){return 1;}\n");
        File.WriteAllText(Path.Combine(src, "dead.c"), "int never(void){return 9;}\n");
        File.WriteAllText(Path.Combine(src, "Makefile"), "all:\n\tgcc *.c\n");
    }

    [Fact]
    public void Carve_EmitsBuildableProject_FixedLayout()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            // Fixed layout: no stages => straight under outputDirectory.
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "main.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "helper.c")));
            Assert.False(File.Exists(Path.Combine(outDir, "carved", "dead.c")));   // dead code dropped
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "Makefile")));  // infra passed through
            Assert.True(File.Exists(Path.Combine(outDir, "codecarver", "report.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "codecarver", "manifest.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "codecarver", "resolved-config.toml")));
            Assert.Contains("verify  : OK", o);
            Assert.Contains("open-world", o);   // no build log/compiler
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_WritesDecisionsLedger_PerSymbolKeepDrop()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);

            var decisions = Path.Combine(outDir, "codecarver", "decisions.txt");
            Assert.True(File.Exists(decisions));                 // always written, no flag
            Assert.Contains("decisions:", o.Replace(" ", ""));   // reported in the run output
            var text = File.ReadAllText(decisions);

            // main + helper are reachable; never() is not. Each symbol gets a verdict line.
            Assert.Contains("KEPT", text);
            Assert.Contains("CARVED", text);
            Assert.Matches(@"KEPT\s+Function\s+main\b", text);
            Assert.Matches(@"KEPT\s+Function\s+helper\b", text);
            Assert.Matches(@"CARVED\s+Function\s+never\b", text);
            // A kept non-root carries its provenance chain back toward the root.
            Assert.Contains("ROOT[", text);

            // The anonymized repro graph ships by default too, and must leak no real names/paths.
            var repro = Path.Combine(outDir, "codecarver", "repro.graph.json");
            Assert.True(File.Exists(repro));
            Assert.Contains("repro", o);                    // reported in the run output
            var rtext = File.ReadAllText(repro);
            Assert.DoesNotContain("main", rtext);           // real symbol names are tokenized away
            Assert.DoesNotContain("helper", rtext);         // (file tokens keep only the extension, e.g. f0.c)
            Assert.Contains("reproFormatVersion", rtext);   // it is the anonymized bundle
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_AutoExcludesGit_ForceKeepUndrops()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        Directory.CreateDirectory(Path.Combine(src, ".git"));
        File.WriteAllText(Path.Combine(src, ".git", "config"), "[core]\n");
        File.WriteAllText(Path.Combine(src, "keep.bak"), "stale\n");  // .bak is auto-excluded
        try
        {
            // forceKeepFiles pulls keep.bak back; .git stays excluded.
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\nforceKeepFiles = [\"keep.bak\"]\n");
            var (code, _, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.False(File.Exists(Path.Combine(outDir, "carved", ".git", "config")));  // auto-excluded
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "keep.bak")));          // forceKeep won
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_Stages_RunAll_ToSubdirs()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, """
                [common]
                entryPoints = ["main"]
                [stages.safe]
                carveSourceFileContents = false
                [stages.aggressive]
                carveSourceFileContents = true
                """);
            var (code, _, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "safe", "carved", "main.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "aggressive", "carved", "main.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "safe", "codecarver", "manifest.json")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_Stage_SelectsOne()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, """
                [common]
                entryPoints = ["main"]
                [stages.safe]
                carveSourceFileContents = false
                [stages.aggressive]
                carveSourceFileContents = true
                """);
            var (code, _, _) = Run("carve", src, "--config", cfg, "--stage", "aggressive");
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "aggressive", "carved", "main.c")));
            Assert.False(Directory.Exists(Path.Combine(outDir, "safe")));   // only the selected stage ran
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_MissingConfig_Exit2()
    {
        var (work, src, _) = NewWork();
        BasicTree(src);
        try
        {
            var (code, _, err) = Run("carve", src);
            Assert.Equal(2, code);
            Assert.Contains("--config", err);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_UnknownFlag_Exit2()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n");
            var (code, _, err) = Run("carve", src, "--config", cfg, "--prune");  // removed flag
            Assert.Equal(2, code);
            Assert.Contains("unknown or incomplete option", err);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_MissingInputFile_FailsFast()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n[builds.main]\nbuildLogs = [\"does-not-exist.log\"]\n");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(2, code);
            Assert.Contains("missing", err);
            Assert.Contains("does-not-exist.log", err);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_TypoedEntryPoint_FailsStrict()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\",\"ghost_symbol\"]\n");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(1, code);   // a missing NAMED entry point always fails
            Assert.Contains("ghost_symbol", err);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_Why_ExplainsSymbol()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg, "--why", "helper");
            Assert.Equal(0, code);
            Assert.Contains("helper", o);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_BuildLog_DerivesClosedWorld_PerTuDefinesKeepBothBranches()
    {
        // eval-#9/#11 regression, now via config buildLogs: widget.c compiled BOTH with and without -DFEATURE.
        // main calls fallback_impl (the #else branch). Per-TU config => FEATURE varies => UNKNOWN => both
        // branches kept => fallback_impl reachable => widget.c emitted. And a build log => closed-world.
        var (work, src, outDir) = NewWork();
        File.WriteAllText(Path.Combine(src, "main.c"),
            "int fallback_impl(void);\nint main(void){return fallback_impl();}\n");
        File.WriteAllText(Path.Combine(src, "widget.c"),
            "#ifdef FEATURE\nint feature_impl(void){return 1;}\n#else\nint fallback_impl(void){return 2;}\n#endif\n");
        var db = Path.Combine(work, "cc.json");
        var d = Fwd(src);
        File.WriteAllText(db,
            "[ {\"directory\":\"" + d + "\",\"file\":\"main.c\",\"arguments\":[\"gcc\",\"-c\",\"main.c\"]}," +
            "  {\"directory\":\"" + d + "\",\"file\":\"widget.c\",\"arguments\":[\"gcc\",\"-DFEATURE\",\"-c\",\"widget.c\"]}," +
            "  {\"directory\":\"" + d + "\",\"file\":\"widget.c\",\"arguments\":[\"gcc\",\"-c\",\"widget.c\"]} ]");
        try
        {
            var cfg = Config(work, outDir, $"[common]\nentryPoints = [\"main\"]\n[builds.main]\nbuildLogs = [\"{Fwd(db)}\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("closed-world", o);                                       // build log => closed-world derived
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "widget.c")));     // #else branch kept => reachable
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_AnalysisOnly_WritesReportManifest_NoCarvedTree()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "analysisOnly = true\n[common]\nentryPoints = [\"main\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("analysis only", o);
            Assert.False(Directory.Exists(Path.Combine(outDir, "carved")));          // no tree emitted
            Assert.True(File.Exists(Path.Combine(outDir, "codecarver", "manifest.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "codecarver", "report.txt")));
            Assert.Contains("\"droppedFiles\"", File.ReadAllText(Path.Combine(outDir, "codecarver", "manifest.json")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_MixedCAndCpp_OneGraph_CrossesLanguageBoundary()
    {
        // Phase 4: languages = ["c","cpp"] carves both families into ONE graph via the C++ grammar. A C root
        // (main, in a .c) reaches a C++ `extern "C"` function (in a .cpp), which reaches a C++-only helper —
        // all kept. A dead C++ class is dropped. This proves reachability crosses the C/C++ boundary.
        var (work, src, outDir) = NewWork();
        File.WriteAllText(Path.Combine(src, "main.c"),
            "int cpp_api(int);\nint main(void){return cpp_api(3);}\n");
        File.WriteAllText(Path.Combine(src, "engine.cpp"), """
            static int cpp_helper(int x){ return x * 2; }
            extern "C" int cpp_api(int x){ return cpp_helper(x); }
            struct Unused { int dead() const { return 9; } };
            """);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\",\"cpp\"]\n");
            var (code, o, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("one graph via the C++ grammar", err);                  // the merge note (stderr)
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "main.c")));     // C root
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "engine.cpp"))); // reached across the boundary
            Assert.Contains("verify  : OK", o);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_MixedCFamily_WithCSharp_Rejected()
    {
        // C# has its own graph shape and can't be merged into the C-family graph — the resolver must reject it.
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\",\"csharp\"]\n");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(2, code);
            Assert.Contains("C family", err);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_ExcludeDirectories_DropsThatTree()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        Directory.CreateDirectory(Path.Combine(src, "other_board"));
        File.WriteAllText(Path.Combine(src, "other_board", "variant.c"), "int variant(void){return 7;}\n");
        File.WriteAllText(Path.Combine(src, "other_board", "notes.txt"), "docs\n");
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\nexcludeDirectories = [\"other_board\"]\n");
            var (code, _, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.False(Directory.Exists(Path.Combine(outDir, "carved", "other_board")));  // whole dir excluded
        }
        finally { Cleanup(work); }
    }
}
