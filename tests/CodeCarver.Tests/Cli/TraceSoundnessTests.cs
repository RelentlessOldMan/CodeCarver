using CodeCarver.Cli;
using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Review step 7 (T1–T6): file and function traces mean what they say, and a trace that matches
/// nothing is an error with a fix, not a silent no-op.</summary>
public sealed class TraceSoundnessTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-tr-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work()
        {
            Directory.CreateDirectory(Src);
            File.WriteAllText(Path.Combine(Src, "main.c"), "int main(void){ return 0; }\n");
            File.WriteAllText(Path.Combine(Src, "Tool.c"), "void tool_used(void){}\nvoid tool_unused(void){}\n");
        }
        public string F(string p) => p.Replace('\\', '/');
        public (int Code, string Out, string Err) Carve(string trace, string extra = "", bool prune = false)
        {
            var tp = Path.Combine(Root, "build.trace");
            File.WriteAllText(tp, trace);
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{F(Path.Combine(Root, "out"))}\"\n[common]\nentryPoints = [\"main\"]\n"
                + (prune ? "carveSourceFileContents = true\n" : "")
                + $"[builds.b]\nbuildTraceFiles = [\"{F(tp)}\"]\n" + extra);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so.ToString(), se.ToString());
        }
        public string Carved(string rel) => Path.Combine(Root, "out", "carved", rel);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void ObservedCodeFile_IsKept_ButItsUnreachedFunctionsStillPruned_T1()
    {
        using var w = new Work();
        var (code, o, e) = w.Carve(w.F(Path.Combine(w.Src, "Tool.c")) + "\n", prune: true);
        Assert.Equal(0, code);
        var text = File.ReadAllText(w.Carved("Tool.c"));
        Assert.DoesNotContain("tool_unused", text);   // D-D: the file is kept; its functions are not all rooted
    }

    [Fact]
    public void CaseMismatchedTracePath_StillRootsTheFile_T5()
    {
        if (!OperatingSystem.IsWindows()) return;          // a case-insensitive file system is the premise
        using var w = new Work();
        var (code, o, e) = w.Carve(w.F(Path.Combine(w.Src, "TOOL.C")) + "\n");
        Assert.Equal(0, code);
        Assert.True(File.Exists(w.Carved("Tool.c")), o + e);
        Assert.Contains("(1 code file(s) rooted)", e);
    }

    [Fact]
    public void TraceFromAnotherCheckout_IsAnError_WithAPathMapHint_T3()
    {
        using var w = new Work();
        var (code, _, e) = w.Carve("/build/agent/repo/src/Tool.c\n/build/agent/repo/src/main.c\n");
        Assert.Equal(2, code);
        Assert.Contains("pathMap", e);
        Assert.Contains("/build/agent/repo/src", e.Replace('\\', '/'));
    }

    [Fact]
    public void PathMap_MapsTheForeignPrefixOntoTheRoot_T3()
    {
        using var w = new Work();
        var (code, o, e) = w.Carve("/build/agent/repo/src/Tool.c\n",
            "[advanced]\npathMap = [{ from = \"/build/agent/repo/src\", to = \".\" }]\n");
        Assert.Equal(0, code);
        Assert.True(File.Exists(w.Carved("Tool.c")), o + e);
    }

    [Fact]
    public void AllowUnmatchedTraces_DowngradesToAWarning()
    {
        using var w = new Work();
        var (code, _, e) = w.Carve("/elsewhere/x.c\n", "[advanced]\nallowUnmatchedTraces = true\n");
        Assert.Equal(0, code);
        Assert.Contains("warn", e);
    }

    [Fact]
    public void OutsideRootCounter_IgnoresMissingAndSystemFiles_T4()
    {
        using var w = new Work();
        var outside = Path.Combine(w.Root, "vendor", "lib.c");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "int lib;\n");
        var (code, _, e) = w.Carve(string.Join("\n", w.F(Path.Combine(w.Src, "main.c")), "/usr/include/stdio.h",
            w.F(Path.Combine(w.Root, "nope", "missing.h")), w.F(outside)) + "\n");
        Assert.Equal(0, code);
        Assert.Contains("1 translation unit(s) + 0 header(s) read OUTSIDE", e);
    }

    [Fact]
    public void StraceRawLines_DecodeEscapes_AndPreferTheFdPath()
    {
        var lines = new[]
        {
            "1234  openat(AT_FDCWD, \"rel.c\", O_RDONLY) = 3</repo/sub/rel.c>",
            "1234  openat(AT_FDCWD, \"caf\\303\\251.c\", O_RDONLY) = 4",
            "1234  openat(AT_FDCWD, \"missing.h\", O_RDONLY) = -1 ENOENT (No such file or directory)",
        };
        var p = FileAccessTrace.Paths(lines);
        Assert.Contains("/repo/sub/rel.c", p);
        Assert.Contains("café.c", p);
        Assert.DoesNotContain("missing.h", p);
        Assert.DoesNotContain("rel.c", p);
        Assert.Contains(@"C:\Users\123\a.c", FileAccessTrace.Paths(new[] { @"C:\Users\123\a.c" }));
    }

    [Theory]
    [InlineData("ns::Motor::start", "start")]
    [InlineData("0x0800a1c4 uart_init", "uart_init")]
    [InlineData("12.345 Timer_ISR main.c:40", "Timer_ISR")]
    [InlineData("[1234] poll", "poll")]
    public void FunctionTrace_ReadsQualifiedAndPrefixedLines_T6(string line, string fn)
    {
        var recs = TraceFile.Parse(line + "\n", null, out var bad);
        Assert.Equal(0, bad);
        Assert.Equal(fn, Assert.Single(recs).Function);
    }
}
