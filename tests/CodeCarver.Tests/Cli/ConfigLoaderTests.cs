using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// The TOML config loader is the front door now — it must parse the sectioned schema, coerce booleans
/// (true/false AND yes/no), and be STRICT: an unknown key or wrong type is an error, not a silent ignore.
/// These pin that, plus the annotated template always being a valid config (drift guard).
/// </summary>
public sealed class ConfigLoaderTests
{
    private static ConfigLoader.Result Parse(string toml) => ConfigLoader.Parse(toml, "test.toml");

    [Fact]
    public void Template_ParsesClean()
    {
        var r = Parse(ConfigLoader.Template);
        Assert.Empty(r.Errors);
        Assert.NotNull(r.Config);
    }

    [Fact]
    public void FullConfig_ParsesAllSections()
    {
        var r = Parse("""
            outputDirectory = "out"
            [common]
            entryPoints = ["main","Reset_Handler"]
            languages = ["c","cpp"]
            excludeDirectories = ["tests"]
            forceKeepFiles = ["prebuilt/*.a"]
            carveSourceFileContents = true
            carveHeaderFileContents = false
            [builds.main]
            buildLogs = ["make.log","console.txt"]
            compiler = "arm-none-eabi-gcc"
            defines = ["CHIP=F4"]
            [builds.codegen]
            buildLogs = ["codegen.log"]
            [runs.smoke]
            runTraceFiles = ["flash.csv"]
            runTraceLogs = ["run.log"]
            [stages.safe]
            carveSourceFileContents = false
            carveHeaderFileContents = false
            [use]
            builds = ["main","codegen"]
            runs = ["smoke"]
            """);
        Assert.Empty(r.Errors);
        var c = r.Config!;
        Assert.Equal("out", c.OutputDirectory);
        Assert.Equal(new[] { "main", "Reset_Handler" }, c.Common.EntryPoints);
        Assert.Equal(new[] { "c", "cpp" }, c.Common.Languages);
        Assert.True(c.Common.CarveSourceFileContents);
        Assert.Equal(2, c.Builds.Count);
        Assert.Equal(new[] { "make.log", "console.txt" }, c.Builds["main"].BuildLogs);
        Assert.Equal("arm-none-eabi-gcc", c.Builds["main"].Compiler);
        Assert.Single(c.Runs);
        Assert.Single(c.Stages);
        Assert.Equal(new[] { "main", "codegen" }, c.UseBuilds);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"yes\"", true)]
    [InlineData("\"no\"", false)]
    [InlineData("\"on\"", true)]
    [InlineData("\"1\"", true)]
    public void Bool_AcceptsTrueFalseAndYesNo(string literal, bool expected)
    {
        var r = Parse($"[common]\ncarveSourceFileContents = {literal}\n");
        Assert.Empty(r.Errors);
        Assert.Equal(expected, r.Config!.Common.CarveSourceFileContents);
    }

    [Fact]
    public void UnknownTopLevelKey_IsError()
    {
        var r = Parse("outptuDirectory = \"typo\"\n");   // typo
        Assert.Null(r.Config);
        Assert.Contains(r.Errors, e => e.Contains("unknown key 'outptuDirectory'"));
    }

    [Fact]
    public void UnknownKeyInSection_IsError()
    {
        var r = Parse("[common]\nentrypoints = [\"main\"]\n");   // wrong case/typo
        Assert.Null(r.Config);
        Assert.Contains(r.Errors, e => e.Contains("[common]") && e.Contains("unknown key 'entrypoints'"));
    }

    [Fact]
    public void WrongType_IsError()
    {
        var r = Parse("[common]\nentryPoints = \"main\"\n");   // should be an array
        Assert.Null(r.Config);
        Assert.Contains(r.Errors, e => e.Contains("entryPoints") && e.Contains("array"));
    }

    [Fact]
    public void UseNamingMissingBuild_IsError()
    {
        var r = Parse("""
            [builds.main]
            buildLogs = ["x.log"]
            [use]
            builds = ["ghost"]
            """);
        Assert.Null(r.Config);
        Assert.Contains(r.Errors, e => e.Contains("ghost") && e.Contains("no [builds.ghost]"));
    }

    [Fact]
    public void HeaderWithoutSource_Warns_ButAllowed()
    {
        var r = Parse("[common]\ncarveSourceFileContents = false\ncarveHeaderFileContents = true\n");
        Assert.NotNull(r.Config);   // allowed
        Assert.Contains(r.Warnings, w => w.Contains("unusual"));
    }

    [Fact]
    public void Init_WritesTemplate_ThatParsesClean_AndRefusesOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "carve.toml");
        try
        {
            var so = new StringWriter(); var se = new StringWriter();
            Assert.Equal(0, CarveCommand.Init(new[] { "init", path }, so, se));
            Assert.True(File.Exists(path));
            // The written file is a valid config.
            Assert.Empty(ConfigLoader.Load(path).Errors);
            // Refuses to clobber an existing (possibly edited) config.
            var se2 = new StringWriter();
            Assert.Equal(2, CarveCommand.Init(new[] { "init", path }, new StringWriter(), se2));
            Assert.Contains("already exists", se2.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void MalformedToml_IsError_NotCrash()
    {
        var r = Parse("[common\nentryPoints = ");   // broken
        Assert.Null(r.Config);
        Assert.NotEmpty(r.Errors);
    }
}
