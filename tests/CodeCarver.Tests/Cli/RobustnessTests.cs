using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Review section 6 robustness items reachable through the CLI (RB10, RB13, RB15).</summary>
public sealed class RobustnessTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-rob-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work() => Directory.CreateDirectory(Src);
        public void W(string rel, string text)
        {
            var p = Path.Combine(Src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        public (int Code, string Out, string Err) Carve(string extra = "")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, "out").Replace('\\', '/')}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\n" + extra);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so.ToString(), se.ToString());
        }
        public string Out(string rel) => Path.Combine(Root, "out", rel);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void PruneGarbageFalse_KeepsLogsAndScratch_RB15()
    {
        using var w = new Work();
        w.W("main.c", "int main(void){ return 0; }\n");
        w.W("build.log", "log\n");
        var (code, o, e) = w.Carve("[advanced]\npruneGarbage = false\n");
        Assert.Equal(0, code);
        Assert.True(File.Exists(w.Out("carved/build.log")), o + e);

        var (code2, o2, e2) = w.Carve();
        Assert.Equal(0, code2);
        Assert.False(File.Exists(w.Out("carved/build.log")), o2 + e2);
    }

    [Theory]
    [InlineData("maxParseBytes = -1")]
    [InlineData("maxSymbolsPerFile = 0")]
    [InlineData("maxSymbolsPerFile = 4294967296")]
    [InlineData("parseTimeout = -5")]
    [InlineData("parseTimeout = 3000000")]
    public void AdvancedIntegers_OutOfRange_AreConfigErrors_RB13(string line)
    {
        using var w = new Work();
        w.W("main.c", "int main(void){ return 0; }\n");
        var (code, _, e) = w.Carve("[advanced]\n" + line + "\n");
        Assert.Equal(2, code);
        Assert.Contains("out of range", e);
    }

    [Fact]
    public void ManyUnresolvedIncludes_AreCappedOnConsole_AndAllWrittenToWarningsTxt_RB10()
    {
        using var w = new Work();
        var includes = string.Concat(Enumerable.Range(0, 30).Select(i => $"#include \"missing_{i}.inc\"\n"));
        w.W("main.c", includes + "int main(void){ return 0; }\n");
        var (code, o, e) = w.Carve();
        Assert.Equal(0, code);
        Assert.Equal(20, e.Split('\n').Count(l => l.Contains("resolved to no file")));
        Assert.Contains("+10 more include warnings", e);
        var all = File.ReadAllText(w.Out("codecarver/warnings.txt"));
        Assert.Equal(30, all.Split('\n').Count(l => l.Contains("resolved to no file")));
        Assert.Contains("warnings.include = 30", File.ReadAllText(w.Out("codecarver/summary.txt")));
    }
}
