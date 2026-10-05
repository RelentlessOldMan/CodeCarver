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
        // Review TS7: the tree must make the stages DIFFER, or the per-stage EmitPruned dispatch is unverified.
        // helper.c carries an unreached function: kept whole at safe (file-level), pruned at aggressive.
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        File.WriteAllText(Path.Combine(src, "helper.c"),
            "int helper(void){return 1;}\nint helper_unused(void){return 2;}\n");
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
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            foreach (var stage in new[] { "safe", "aggressive" })
            {
                Assert.True(File.Exists(Path.Combine(outDir, stage, "carved", "main.c")));
                Assert.False(File.Exists(Path.Combine(outDir, stage, "carved", "dead.c")));   // file-level drop at both
                Assert.True(File.Exists(Path.Combine(outDir, stage, "codecarver", "manifest.json")));
            }
            Assert.False(File.Exists(Path.Combine(outDir, "carved", "main.c")));            // nothing outside the stage dirs

            var safeHelper = File.ReadAllText(Path.Combine(outDir, "safe", "carved", "helper.c"));
            var aggrHelper = File.ReadAllText(Path.Combine(outDir, "aggressive", "carved", "helper.c"));
            Assert.Contains("helper_unused", safeHelper);       // safe = whole kept files
            Assert.DoesNotContain("helper_unused", aggrHelper); // aggressive = EmitPruned removed it
            Assert.Contains("int helper(void)", aggrHelper);    // ...and kept what is reached
            Assert.Contains("\"carveSourceFileContents\": true",
                File.ReadAllText(Path.Combine(outDir, "aggressive", "codecarver", "manifest.json")));
            Assert.Contains("stage   : safe", o);
            Assert.Contains("stage   : aggressive", o);
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
            // Review TS7: "helper" alone would pass on a CARVED verdict too. Assert the actual explanation: the
            // keep chain from helper back through its caller to the root, and no carved verdict.
            Assert.Contains("Function 'helper' @ helper.c", o);
            Assert.Contains("<= [Calls] Function 'main' @ main.c", o);
            Assert.Contains("ROOT[", o);
            Assert.DoesNotContain("CARVED", o);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_Why_CarvedSymbol_SaysCarved()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg, "--why", "never");
            Assert.Equal(0, code);
            Assert.Contains("Function 'never' @ dead.c", o);
            Assert.Contains("CARVED (not reachable)", o);
            Assert.DoesNotContain("ROOT[", o);
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

    [Theory]
    [InlineData(false)]   // FEATURE absent from every compile command: the #ifdef branch is dead
    [InlineData(true)]    // -DFEATURE on every compile command: the #else branch is dead
    public void Carve_BuildLog_ClosedWorld_UniformMacro_DropsDeadBranchCallee(bool defined)
    {
        // Review TS7: the CLI closed-world test above covers only the "varies per TU" case, where nothing may be
        // dropped. Here the build log pins FEATURE the same way for every TU, so closed-world resolves it and the
        // file only the dead branch calls is dropped — while the live branch's callee is kept.
        var (work, src, outDir) = NewWork();
        File.WriteAllText(Path.Combine(src, "main.c"),
            "#ifdef FEATURE\nint feature_impl(void);\nint main(void){return feature_impl();}\n"
            + "#else\nint fallback_impl(void);\nint main(void){return fallback_impl();}\n#endif\n");
        File.WriteAllText(Path.Combine(src, "feature.c"), "int feature_impl(void){return 1;}\n");
        File.WriteAllText(Path.Combine(src, "fallback.c"), "int fallback_impl(void){return 2;}\n");
        var db = Path.Combine(work, "cc.json");
        var d = Fwd(src);
        // The absent case passes one unrelated -D; the zero -D case is Carve_BuildLog_NoDefinesAtAll_StillResolvesIfdefs.
        var flag = defined ? "\"-DFEATURE\"," : "\"-DUNRELATED=1\",";
        File.WriteAllText(db, "[" + string.Join(",", new[] { "main.c", "feature.c", "fallback.c" }.Select(f =>
            "{\"directory\":\"" + d + "\",\"file\":\"" + f + "\",\"arguments\":[\"gcc\"," + flag + "\"-c\",\"" + f + "\"]}")) + "]");
        try
        {
            var cfg = Config(work, outDir, $"[common]\nentryPoints = [\"main\"]\n[builds.main]\nbuildLogs = [\"{Fwd(db)}\"]\n");
            var (code, o, e) = Run("carve", src, "--config", cfg);
            Assert.True(code == 0, o + e);
            Assert.Contains("closed-world", o);
            var live = defined ? "feature.c" : "fallback.c";
            var dead = defined ? "fallback.c" : "feature.c";
            Assert.True(File.Exists(Path.Combine(outDir, "carved", live)), $"{live} (live branch's callee) must be kept");
            Assert.False(File.Exists(Path.Combine(outDir, "carved", dead)), $"{dead} (dead branch's callee) must be dropped");
            Assert.Contains($"dropped : {dead}", o);
            Assert.Contains("verify  : OK", o);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_BuildLog_NoDefinesAtAll_StillResolvesIfdefs()
    {
        var (work, src, outDir) = NewWork();
        File.WriteAllText(Path.Combine(src, "main.c"),
            "#ifdef FEATURE\nint feature_impl(void);\nint main(void){return feature_impl();}\n"
            + "#else\nint fallback_impl(void);\nint main(void){return fallback_impl();}\n#endif\n");
        File.WriteAllText(Path.Combine(src, "feature.c"), "int feature_impl(void){return 1;}\n");
        File.WriteAllText(Path.Combine(src, "fallback.c"), "int fallback_impl(void){return 2;}\n");
        var db = Path.Combine(work, "cc.json");
        var d = Fwd(src);
        File.WriteAllText(db, "[" + string.Join(",", new[] { "main.c", "feature.c", "fallback.c" }.Select(f =>
            "{\"directory\":\"" + d + "\",\"file\":\"" + f + "\",\"arguments\":[\"gcc\",\"-c\",\"" + f + "\"]}")) + "]");
        try
        {
            var cfg = Config(work, outDir, $"[common]\nentryPoints = [\"main\"]\n[builds.main]\nbuildLogs = [\"{Fwd(db)}\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("closed-world", o);
            Assert.False(File.Exists(Path.Combine(outDir, "carved", "feature.c")));   // kept today
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_RunTrace_TightensCmm_ViaDoClosure()
    {
        // The real headline for the work repo: a C/C++ carve whose .cmm are infrastructure. A RUN trace that
        // opened one script must keep that script + its static DO closure, and DROP .cmm reachable by nobody.
        var (work, src, outDir) = NewWork();
        BasicTree(src);                                           // main.c -> helper.c ; dead.c
        var scripts = Path.Combine(src, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.cmm"),   "DO flash\n");          // the observed seed
        File.WriteAllText(Path.Combine(scripts, "flash.cmm"),  "Flash:\n  DO common\n  RETURN\n");
        File.WriteAllText(Path.Combine(scripts, "common.cmm"), "Common:\n  RETURN\n");
        File.WriteAllText(Path.Combine(scripts, "orphan.cmm"), "Orphan:\n  RETURN\n"); // reached by nobody
        var trace = Path.Combine(work, "run.trace");
        File.WriteAllText(trace, Fwd(Path.Combine(scripts, "main.cmm")) + "\n");       // run opened main.cmm
        try
        {
            var cfg = Config(work, outDir,
                $"[common]\nentryPoints = [\"main\"]\n[runs.smoke]\nrunTraceFiles = [\"{Fwd(trace)}\"]\ndropUnobservedCmm = true\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);

            Assert.Contains("cmm     :", o);                                           // the tightening ran
            // Seed + its DO closure are kept; the orphan is dropped.
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "main.cmm")));
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "flash.cmm")));
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "common.cmm")));
            Assert.False(File.Exists(Path.Combine(outDir, "carved", "scripts", "orphan.cmm")));
            // And the C carve is unaffected.
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "main.c")));
            Assert.False(File.Exists(Path.Combine(outDir, "carved", "dead.c")));

            var manifest = File.ReadAllText(Path.Combine(outDir, "codecarver", "manifest.json"));
            Assert.Contains("scripts/orphan.cmm", manifest.Replace("\\/", "/"));       // recorded as droppedCmm
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_RunTrace_WithoutOptIn_KeepsUnobservedCmm()
    {
        // Owner decision D-C: one run is one scenario. Without dropUnobservedCmm nothing is dropped; the count is reported.
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        var scripts = Path.Combine(src, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.cmm"), "DO flash\n");
        File.WriteAllText(Path.Combine(scripts, "flash.cmm"), "Flash:\n  RETURN\n");
        File.WriteAllText(Path.Combine(scripts, "menu.cmm"), "Menu:\n  RETURN\n");     // reached from a dialog, not this run
        var trace = Path.Combine(work, "run.trace");
        File.WriteAllText(trace, Fwd(Path.Combine(scripts, "main.cmm")) + "\n");
        try
        {
            var cfg = Config(work, outDir, $"[common]\nentryPoints = [\"main\"]\n[runs.smoke]\nrunTraceFiles = [\"{Fwd(trace)}\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "menu.cmm")));
            Assert.Contains("1 neither (kept", o);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_RunTrace_DynamicDoInKeptScript_DropsNothing()
    {
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        var scripts = Path.Combine(src, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.cmm"), "DO &board\n");
        File.WriteAllText(Path.Combine(scripts, "board_a.cmm"), "A:\n  RETURN\n");
        var trace = Path.Combine(work, "run.trace");
        File.WriteAllText(trace, Fwd(Path.Combine(scripts, "main.cmm")) + "\n");
        try
        {
            var cfg = Config(work, outDir,
                $"[common]\nentryPoints = [\"main\"]\n[runs.smoke]\nrunTraceFiles = [\"{Fwd(trace)}\"]\ndropUnobservedCmm = true\n");
            var (code, _, e) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "board_a.cmm")));
            Assert.Contains("dropping nothing", e);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_NoRunTrace_KeepsAllCmm()
    {
        // Without a run trace we can't prove which scripts run — keep every .cmm (sound default, no tightening).
        var (work, src, outDir) = NewWork();
        BasicTree(src);
        var scripts = Path.Combine(src, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "a.cmm"), "A:\n  RETURN\n");
        File.WriteAllText(Path.Combine(scripts, "b.cmm"), "B:\n  RETURN\n");
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\n");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.DoesNotContain("cmm     :", o);                                     // no trace => no tightening
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "a.cmm")));
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "scripts", "b.cmm")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Example_CmmTrace_CheckedInCarveIsUpToDate()
    {
        // The examples/cmm-trace carved tree is checked in as documentation of the trace-seeded .cmm closure.
        // Regenerate it through the real CLI and assert it matches byte-for-byte, so it can't drift from the tool.
        // Review TS3: the example is committed, so a missing fixture is a FAILURE, not a silent pass.
        var ex = TestRepo.Example("cmm-trace");
        var golden = Path.Combine(ex, "carved");
        Assert.True(Directory.Exists(golden), "examples/cmm-trace/carved (the checked-in golden tree) is missing");

        var work = Path.Combine(Path.GetTempPath(), "cc-cmmex-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(work);
        try
        {
            var trace = Fwd(Path.Combine(ex, "inputs", "run.trace"));
            var cfg = Config(work, outDir,
                $"[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n[runs.smoke]\nrunTraceFiles = [\"{trace}\"]\ndropUnobservedCmm = true\n");
            var (code, o, _) = Run("carve", Path.Combine(ex, "src"), "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("cmm     : 4/6 script(s) kept (1 observed + 3 via DO/GOSUB closure), 2 dropped", o);

            var carved = Path.Combine(outDir, "carved");
            static string Norm(string s) => s.Replace("\r\n", "\n");
            // The .codecarver-output marker carries a per-run UTC timestamp — it's an internal marker, not part
            // of the buildable tree, so it isn't checked in and is excluded from the diff.
            static bool IsTree(string f) => Path.GetFileName(f) != ".codecarver-output";
            var checkedIn = Directory.EnumerateFiles(golden, "*.*", SearchOption.AllDirectories).Where(IsTree).ToList();
            foreach (var f in checkedIn)
            {
                var rel = Path.GetRelativePath(golden, f);
                var regen = Path.Combine(carved, rel);
                Assert.True(File.Exists(regen), $"carved/{rel} is checked in but the carve no longer emits it — regenerate the example");
                Assert.True(Norm(File.ReadAllText(f)) == Norm(File.ReadAllText(regen)), $"carved/{rel} is stale — re-run the carve and commit the example");
            }
            Assert.Equal(checkedIn.Count, Directory.EnumerateFiles(carved, "*.*", SearchOption.AllDirectories).Where(IsTree).Count());

            // The two scripts the trace can't justify are gone; the dynamic-DO one is warned, not silent.
            Assert.False(File.Exists(Path.Combine(carved, "scripts", "debug_dump.cmm")));
            Assert.False(File.Exists(Path.Combine(carved, "scripts", "board_rev_a.cmm")));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
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
        // A dead C++ translation unit: a class nothing reachable refers to.
        File.WriteAllText(Path.Combine(src, "unused.cpp"),
            "class DeadClass {\npublic:\n  int idle() const { return 1; }\n};\nint dead_free(){ DeadClass d; return d.idle(); }\n");
        try
        {
            var cfg = Config(work, outDir, """
                [common]
                entryPoints = ["main"]
                languages = ["c","cpp"]
                [stages.safe]
                carveSourceFileContents = false
                [stages.aggressive]
                carveSourceFileContents = true
                """);
            var (code, o, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("one graph via the C++ grammar", err);                  // the merge note (stderr)
            foreach (var stage in new[] { "safe", "aggressive" })
            {
                var carved = Path.Combine(outDir, stage, "carved");
                Assert.True(File.Exists(Path.Combine(carved, "main.c")));           // C root
                Assert.True(File.Exists(Path.Combine(carved, "engine.cpp")));       // reached across the boundary
                Assert.False(File.Exists(Path.Combine(carved, "unused.cpp")));      // dead class's TU dropped
                Assert.Contains("unused.cpp", File.ReadAllText(Path.Combine(outDir, stage, "codecarver", "manifest.json")));
                var decisions = File.ReadAllText(Path.Combine(outDir, stage, "codecarver", "decisions.txt"));
                Assert.Matches(@"KEPT\s+Function\s+cpp_helper\b", decisions);       // C++-only helper reached from C
                Assert.Matches(@"CARVED\s+Type\s+Unused\b", decisions);             // the dead class is carved...
                Assert.Matches(@"CARVED\s+Type\s+DeadClass\b", decisions);
                // Its method: at safe, engine.cpp is emitted whole, so what it contains is rooted (EmittedWhole);
                // at aggressive the method is genuinely carved.
                Assert.Matches(stage == "safe" ? @"KEPT\s+Function\s+dead\b.*EmittedWhole" : @"CARVED\s+Function\s+dead\b", decisions);
            }
            Assert.Contains("dropped : unused.cpp", o);
            // ...and at aggressive it is physically gone from the kept engine.cpp, while the live code stays.
            var engine = File.ReadAllText(Path.Combine(outDir, "aggressive", "carved", "engine.cpp"));
            Assert.DoesNotContain("Unused", engine);
            Assert.Contains("cpp_api", engine);
            Assert.Contains("cpp_helper", engine);
            Assert.Contains("struct Unused", File.ReadAllText(Path.Combine(outDir, "safe", "carved", "engine.cpp"))); // safe = whole file
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
    public void Carve_UnscannedIncludeInExcludedDir_IsListedInManifest()
    {
        // main.c includes a header that lives in an excluded directory. The emitter rightly copies it (the tree would
        // not compile without it), and manifest.json must list it (includeClosureFiles).
        var (work, src, outDir) = NewWork();
        Directory.CreateDirectory(Path.Combine(src, "vendor"));
        File.WriteAllText(Path.Combine(src, "main.c"), "#include \"vendor/cfg.h\"\nint main(void){return CFG;}\n");
        File.WriteAllText(Path.Combine(src, "vendor", "cfg.h"), "#define CFG 1\n");
        try
        {
            var cfg = Config(work, outDir, "[common]\nentryPoints = [\"main\"]\nexcludeDirectories = [\"vendor\"]\n");
            var (code, _, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "carved", "vendor", "cfg.h")));   // emitted (correct)
            using var m = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "codecarver", "manifest.json")));
            var listed = new[] { "keptFiles", "includeClosureFiles", "infrastructureFiles" }
                .SelectMany(p => m.RootElement.GetProperty(p).EnumerateArray().Select(e => e.GetString()))
                .ToList();
            Assert.Contains("vendor/cfg.h", listed);
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
