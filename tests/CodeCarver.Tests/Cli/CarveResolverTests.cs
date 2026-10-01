using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// CarveResolver is the whole translation from the sectioned TOML to flat engine inputs: union the selected
/// builds/runs, derive open/closed world, expand entryPoints, and choose stages. These pin that behavior.
/// </summary>
public sealed class CarveResolverTests
{
    private static CarveTomlConfig Cfg(string toml)
    {
        var r = ConfigLoader.Parse(toml, "t.toml");
        Assert.Empty(r.Errors);
        return r.Config!;
    }

    [Fact]
    public void UnionsSelectedBuildsAndRuns_DerivesClosedWorld()
    {
        var cfg = Cfg("""
            outputDirectory = "out"
            [common]
            entryPoints = ["main"]
            [builds.a]
            buildLogs = ["a.log"]
            defines = ["X=1"]
            [builds.b]
            buildLogs = ["b.log"]
            compiler = "arm-none-eabi-gcc"
            defines = ["Y=2"]
            [runs.r1]
            runTraceFiles = ["f1.csv"]
            [runs.r2]
            runTraceLogs = ["r2.log"]
            """);
        var res = CarveResolver.Resolve(cfg, null);
        Assert.Empty(res.Errors);
        var c = res.Carve!;
        Assert.Equal(new[] { "a.log", "b.log" }, c.BuildLogs);          // unioned
        Assert.Equal(new[] { "X=1", "Y=2" }, c.Defines);
        Assert.Contains("arm-none-eabi-gcc", c.Compilers);
        Assert.Contains("f1.csv", c.RunTraceFiles);
        Assert.Contains("r2.log", c.RunTraceLogs);
        Assert.True(c.ClosedWorld);                                     // has build logs/compiler
        Assert.Contains("closed-world", c.WorldReason);
    }

    [Fact]
    public void UseSelectsSubset()
    {
        var cfg = Cfg("""
            outputDirectory = "out"
            [common]
            entryPoints = ["main"]
            [builds.a]
            buildLogs = ["a.log"]
            [builds.b]
            buildLogs = ["b.log"]
            [use]
            builds = ["a"]
            """);
        var c = CarveResolver.Resolve(cfg, null).Carve!;
        Assert.Equal(new[] { "a.log" }, c.BuildLogs);   // only the selected build
    }

    [Fact]
    public void NoBuildOrCompiler_IsOpenWorld()
    {
        var cfg = Cfg("outputDirectory=\"out\"\n[common]\nentryPoints=[\"main\"]\n");
        var c = CarveResolver.Resolve(cfg, null).Carve!;
        Assert.False(c.ClosedWorld);
        Assert.Contains("open-world", c.WorldReason);
    }

    [Fact]
    public void NoStages_ProducesOneImplicitStage_FromCommonToggles()
    {
        var cfg = Cfg("outputDirectory=\"out\"\n[common]\nentryPoints=[\"main\"]\ncarveSourceFileContents=true\n");
        var c = CarveResolver.Resolve(cfg, null).Carve!;
        Assert.Single(c.Stages);
        Assert.Equal("", c.Stages[0].Name);                 // implicit = no subdir
        Assert.True(c.Stages[0].CarveSourceFileContents);
    }

    [Fact]
    public void Stages_NoSelection_RunsAll_OrderedByAggressiveness()
    {
        var cfg = Cfg("""
            outputDirectory = "out"
            [common]
            entryPoints = ["main"]
            [stages.max]
            carveSourceFileContents = true
            carveHeaderFileContents = true
            [stages.safe]
            carveSourceFileContents = false
            carveHeaderFileContents = false
            [stages.mid]
            carveSourceFileContents = true
            carveHeaderFileContents = false
            """);
        var c = CarveResolver.Resolve(cfg, null).Carve!;
        Assert.Equal(new[] { "safe", "mid", "max" }, c.Stages.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Stages_Selected_RunsOne()
    {
        var cfg = Cfg("""
            outputDirectory = "out"
            [common]
            entryPoints = ["main"]
            [stages.safe]
            carveSourceFileContents = false
            [stages.max]
            carveSourceFileContents = true
            carveHeaderFileContents = true
            """);
        var c = CarveResolver.Resolve(cfg, "max").Carve!;
        Assert.Single(c.Stages);
        Assert.Equal("max", c.Stages[0].Name);
        Assert.True(c.Stages[0].CarveHeaderFileContents);
    }

    [Fact]
    public void Stages_UnknownSelection_IsError()
    {
        var cfg = Cfg("outputDirectory=\"out\"\n[common]\nentryPoints=[\"main\"]\n[stages.safe]\ncarveSourceFileContents=false\n");
        var res = CarveResolver.Resolve(cfg, "ghost");
        Assert.Null(res.Carve);
        Assert.Contains(res.Errors, e => e.Contains("ghost") && e.Contains("not defined"));
    }

    [Fact]
    public void MissingOutputDirectory_IsError()
    {
        var cfg = Cfg("[common]\nentryPoints=[\"main\"]\n");
        var res = CarveResolver.Resolve(cfg, null);
        Assert.Null(res.Carve);
        Assert.Contains(res.Errors, e => e.Contains("outputDirectory"));
    }

    [Fact]
    public void MissingEntryPoints_IsError()
    {
        var cfg = Cfg("outputDirectory=\"out\"\n");
        var res = CarveResolver.Resolve(cfg, null);
        Assert.Null(res.Carve);
        Assert.Contains(res.Errors, e => e.Contains("entryPoints"));
    }

    [Fact]
    public void EntryPointsFile_IsRead_AndMergedDeduped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var epf = Path.Combine(dir, "roots.txt");
        File.WriteAllText(epf, "# entry points\nmain\nReset_Handler\n\nmain\n");  // comment, blank, dup
        try
        {
            var cfg = Cfg($"outputDirectory=\"out\"\n[common]\nentryPoints=[\"USART1_IRQHandler\"]\nentryPointsFile=\"{epf.Replace("\\", "/")}\"\n");
            var c = CarveResolver.Resolve(cfg, null).Carve!;
            Assert.Equal(new[] { "USART1_IRQHandler", "main", "Reset_Handler" }, c.EntryPoints);  // merged, deduped, order kept
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DefaultLanguageIsC_UnknownLanguageErrors()
    {
        Assert.Equal(new[] { "c" }, CarveResolver.Resolve(Cfg("outputDirectory=\"o\"\n[common]\nentryPoints=[\"m\"]\n"), null).Carve!.Languages);
        var bad = CarveResolver.Resolve(Cfg("outputDirectory=\"o\"\n[common]\nentryPoints=[\"m\"]\nlanguages=[\"rust\"]\n"), null);
        Assert.Null(bad.Carve);
        Assert.Contains(bad.Errors, e => e.Contains("rust"));
    }
}
