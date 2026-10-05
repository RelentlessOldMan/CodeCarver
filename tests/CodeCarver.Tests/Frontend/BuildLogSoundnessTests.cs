using CodeCarver.Cli;
using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Review step 3 (BL1–BL3): every compile on a log line gets its own flags, response files and forced
/// includes are honoured, and a log that yields nothing is an error rather than a silent "closed world".</summary>
public sealed class BuildLogSoundnessTests
{
    static CompileCommand For(IReadOnlyList<CompileCommand> cmds, string file) => Assert.Single(cmds, c => c.File == file);

    [Fact]
    public void ChainedCommands_DoNotShareFlags_BL1()
    {
        var cmds = BuildLogScraper.Parse("gcc -DBAR -c a.c -o a.o && gcc -DBAR -DFOO -c b.c -o b.o\n");
        Assert.DoesNotContain("FOO", For(cmds, "a.c").Defines);
        Assert.Contains("FOO", For(cmds, "b.c").Defines);
    }

    [Fact]
    public void CdCarriesAcrossChainedCommands_AndAttachedSeparators()
    {
        var cmds = BuildLogScraper.Parse("cd sub&&gcc -DA -c x.c; gcc -DB -c y.c\n");
        Assert.Equal("sub", For(cmds, "x.c").Directory);
        Assert.Equal("sub", For(cmds, "y.c").Directory);
        Assert.DoesNotContain("B", For(cmds, "x.c").Defines);
    }

    [Fact]
    public void QuotedSeparatorInsideDefine_IsNotASplit()
    {
        var cmds = BuildLogScraper.Parse("gcc \"-DMSG=a && b\" -c a.c\n");
        Assert.Contains("MSG=a && b", For(cmds, "a.c").Defines);
    }

    [Fact]
    public void ResponseFile_IsSpliced_Nested_AndMissingMarksIncomplete_BL2()
    {
        var files = new Dictionary<string, string> { ["flags.rsp"] = "-DFOO @more.rsp", ["more.rsp"] = "-DDEEP=1" };
        var o = new BuildLogScraper.ScrapeOptions { ReadResponseFile = (_, p) => files.TryGetValue(p, out var t) ? t : null };
        var cmds = BuildLogScraper.Parse("gcc -DBAR @flags.rsp -c a.c\ngcc @missing.rsp -c b.c\n", o);
        var a = For(cmds, "a.c");
        Assert.Contains("FOO", a.Defines);
        Assert.Contains("DEEP=1", a.Defines);
        Assert.False(a.Incomplete);
        Assert.True(For(cmds, "b.c").Incomplete);
    }

    [Fact]
    public void ForcedIncludesAndPassThroughDefines_AreRecorded()
    {
        var cmds = BuildLogScraper.Parse("gcc -include force.h -imacros m.h -Wp,-DWP1,-UWP2 -Xpreprocessor -DXP -c a.c\n");
        var a = For(cmds, "a.c");
        Assert.Equal(new[] { "force.h", "m.h" }, a.ForcedIncludes);
        Assert.Contains("WP1", a.Defines);
        Assert.Contains("XP", a.Defines);
    }

    [Fact]
    public void VendorCompilers_AreRecognised_ByConfigOrGenericShape_BL3()
    {
        Assert.Single(BuildLogScraper.Parse("armcc -DX=1 -c main.c -o main.o\n"));
        Assert.Single(BuildLogScraper.Parse("iccarm -DX=1 g.c\n"));
        var named = new BuildLogScraper.ScrapeOptions { CompilerNames = new[] { "xc8-cc" } };
        Assert.Single(BuildLogScraper.Parse("xc8-cc main.c\n", named));
        Assert.Single(BuildLogScraper.Parse("ccache /opt/arm/bin/arm-none-eabi-gcc -c a.c\n"));
        Assert.Single(BuildLogScraper.Parse("[3/10] gcc -c a.c\n"));
    }

    [Fact]
    public void EchoOfACompilerName_IsNotACompile()
    {
        Assert.Empty(BuildLogScraper.Parse("echo Building gcc_helper.c foo.c -DX\n"));
    }

    [Fact]
    public void UnixPathsAreNotMsvcFlags_ForGcc()
    {
        var a = For(BuildLogScraper.Parse("gcc -c /Users/x/a.c -DOK\n"), "/Users/x/a.c");
        Assert.Equal(new[] { "OK" }, a.Defines);
        var m = For(BuildLogScraper.Parse("cl /DWIN /c a.c\n"), "a.c");
        Assert.Contains("WIN", m.Defines);
    }

    // ---- CLI ---------------------------------------------------------------------------------------------

    static (int Code, string Out, string Err) Carve(string log, string? logName = "make.log")
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-bl-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
            File.WriteAllText(Path.Combine(work, logName!), log);
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n"
                + "[common]\nentryPoints = [\"main\"]\n[builds.m]\nbuildLogs = [\""
                + Path.Combine(work, logName!).Replace('\\', '/') + "\"]\n");
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            return (code, so.ToString(), se.ToString());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Cli_LogWithNoCompileCommand_IsAConfigError()
    {
        var (code, _, err) = Carve("make: Nothing to be done for 'all'.\n");
        Assert.Equal(2, code);
        Assert.Contains("contains no compile command", err);
    }

    [Fact]
    public void Cli_LogFromAnotherCheckout_IsAConfigError()
    {
        var (code, _, err) = Carve("[{\"directory\":\"/build/agent/repo\",\"file\":\"/build/agent/repo/x/main.c\",\"command\":\"gcc -c main.c\"}]",
            "cc.json");
        Assert.Equal(2, code);
        Assert.Contains("different checkout location", err);
    }
}
