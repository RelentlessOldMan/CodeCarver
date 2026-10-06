using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary><c>--emit-from</c>: write the tree a prior analysis-only run decided on, without parsing again (work
/// request after a ~1 hour real-scale dry run).</summary>
public sealed class EmitFromTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-emitfrom-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work()
        {
            Directory.CreateDirectory(Src);
            W("main.c", "#include \"util.h\"\nint main(void){ return used(); }\n");
            W("util.h", "int used(void);\n");
            W("util.c", "#include \"util.h\"\nint used(void){ return 1; }\n");
            W("dead.c", "int dead(void){ return 2; }\n");
            W("Makefile", "all:\n\tcc main.c util.c\n");
        }
        public void W(string rel, string text) => File.WriteAllText(Path.Combine(Src, rel), text);
        public string Config(string outDir, bool analysis, string extra = "")
        {
            var cfg = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, outDir).Replace('\\', '/')}\"\n"
                                   + $"analysisOnly = {(analysis ? "true" : "false")}\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n" + extra);
            return cfg;
        }
        public (int Code, string Out) Carve(string cfg, params string[] more)
        {
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }.Concat(more).ToArray(), so, se);
            return (code, so + "\n" + se);
        }
        public string[] Tree(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/') + ":" + File.ReadAllText(f).Length)
                     .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        public void Dispose() => TempDir.Delete(Root);
    }

    [Fact]
    public void EmitFrom_WritesTheSameTreeAsANormalFileLevelCarve()
    {
        using var w = new Work();
        var (c1, o1) = w.Carve(w.Config("plan", analysis: true));
        Assert.True(c1 == 0, o1);
        Assert.True(File.Exists(Path.Combine(w.Root, "plan", "codecarver", EmitFrom.PlanFile)), o1);
        Assert.False(Directory.Exists(Path.Combine(w.Root, "plan", "carved")));

        // Same settings, now emitting — into the analysis run's own output directory, the usual workflow.
        var (c2, o2) = w.Carve(w.Config("plan", analysis: true), "--emit-from", Path.Combine(w.Root, "plan"));
        Assert.True(c2 == 0, o2);
        Assert.Contains("no parse", o2);
        var (c3, o3) = w.Carve(w.Config("normal", analysis: false));
        Assert.True(c3 == 0, o3);

        Assert.Equal(w.Tree(Path.Combine(w.Root, "normal", "carved")), w.Tree(Path.Combine(w.Root, "plan", "carved")));
        Assert.DoesNotContain("dead.c:", string.Join(" ", w.Tree(Path.Combine(w.Root, "plan", "carved"))));
        var summary = File.ReadAllText(Path.Combine(w.Root, "plan", "codecarver", "summary.txt"));
        Assert.Contains("emitFrom = true", summary);
        Assert.Contains("exitCode = 0", summary);
    }

    [Fact]
    public void EmitFrom_RefusesAfterASourceChange()
    {
        using var w = new Work();
        Assert.Equal(0, w.Carve(w.Config("plan", analysis: true)).Code);
        w.W("util.c", "#include \"util.h\"\nint used(void){ return dead(); }\n");   // now needs dead.c
        var (code, o) = w.Carve(w.Config("plan", analysis: false), "--emit-from", Path.Combine(w.Root, "plan"));
        Assert.Equal(2, code);
        Assert.Contains("source tree changed", o);
        Assert.False(Directory.Exists(Path.Combine(w.Root, "plan", "carved")));
    }

    [Fact]
    public void EmitFrom_RefusesAfterAConfigChange()
    {
        using var w = new Work();
        Assert.Equal(0, w.Carve(w.Config("plan", analysis: true)).Code);
        var changed = w.Config("plan", analysis: false);
        File.AppendAllText(changed, "[builds.b]\ndefines = [\"X=1\"]\n[use]\nbuilds = [\"b\"]\n");
        var (code, o) = w.Carve(changed, "--emit-from", Path.Combine(w.Root, "plan"));
        Assert.Equal(2, code);
        Assert.Contains("configuration changed", o);
    }

    [Fact]
    public void EmitFrom_RefusesStagesThatCarveInsideFiles()
    {
        using var w = new Work();
        Assert.Equal(0, w.Carve(w.Config("plan", analysis: true)).Code);
        var (code, o) = w.Carve(w.Config("plan", analysis: false, "[stages.aggressive]\ncarveSourceFileContents = true\n"),
                                "--emit-from", Path.Combine(w.Root, "plan"));
        Assert.Equal(2, code);
        Assert.Contains("carve inside files", o);
    }

    [Fact]
    public void EmitFrom_WithoutAPlan_ExplainsHowToGetOne()
    {
        using var w = new Work();
        var (code, o) = w.Carve(w.Config("out", analysis: false), "--emit-from", Path.Combine(w.Root, "nothing-here"));
        Assert.Equal(2, code);
        Assert.Contains("analysisOnly = true", o);
    }

    [Fact]
    public void EmitFrom_CarriesAFailedVerifyOver_AsExit3()
    {
        using var w = new Work();
        w.W("main.c", "int HELPER(int);\nint main(void){ return HELPER(1); }\n");
        w.W("caps.c", "HELPER(int x) { return x; }\n");                 // not recognised: verify fails
        Assert.Equal(3, w.Carve(w.Config("plan", analysis: true)).Code);
        var (code, o) = w.Carve(w.Config("plan", analysis: true), "--emit-from", Path.Combine(w.Root, "plan"));
        Assert.Equal(3, code);
        Assert.Contains("FAILED in the analysis run", o);
    }
}

public sealed class EmitFromRefusalTests
{
    [Fact]
    public void RefusesAPlanFromAnotherVersion_AndAChangedBuildLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-emitfrom2-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){ return 0; }\n");
            var log = Path.Combine(root, "build.log");
            File.WriteAllText(log, $"gcc -c -DX=1 {Path.Combine(src, "main.c").Replace('\\', '/')} -o main.o\n");
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace('\\', '/')}\"\nanalysisOnly = true\n"
                + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n"
                + $"[builds.b]\nbuildLogs = [\"{log.Replace('\\', '/')}\"]\n");
            int Run(params string[] more)
            {
                var so = new StringWriter(); var se = new StringWriter();
                return CarveCommand.Run(new[] { "carve", src, "--config", cfg }.Concat(more).ToArray(), so, se);
            }
            Assert.Equal(0, Run());
            var plan = Path.Combine(root, "out", "codecarver", EmitFrom.PlanFile);
            var original = File.ReadAllText(plan);

            File.WriteAllText(plan, original.Replace("\"codecarverVersion\": \"", "\"codecarverVersion\": \"0.0.0-other+"));
            Assert.Equal(2, Run("--emit-from", Path.Combine(root, "out")));

            File.WriteAllText(plan, original);
            File.AppendAllText(log, "# a later build\n");                // the build log changed since the analysis
            Assert.Equal(2, Run("--emit-from", Path.Combine(root, "out")));

            File.WriteAllText(plan, "{ \"format\": 99 }");
            Assert.Equal(2, Run("--emit-from", Path.Combine(root, "out")));
        }
        finally { TempDir.Delete(root); }
    }
}
