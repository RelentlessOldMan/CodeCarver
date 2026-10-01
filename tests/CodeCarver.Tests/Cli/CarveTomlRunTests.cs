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
