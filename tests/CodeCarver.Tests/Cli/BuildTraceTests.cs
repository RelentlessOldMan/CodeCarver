using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A build trace (or build log) says what the build COMPILED, not what the program needs: a compiled file nothing
/// reachable calls is dropped and written as a placeholder, so build files that list it still work. A code file a fully
/// traced build never opened is not read or parsed; if kept code turns out to need it, verify says so.
/// </summary>
public sealed class BuildTraceTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-bt-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work()
        {
            Directory.CreateDirectory(Src);
            File.WriteAllText(Path.Combine(Src, "main.c"), "int helper(void);\nint main(void){ return helper(); }\n");
            File.WriteAllText(Path.Combine(Src, "helper.c"), "int helper(void){ return 1; }\n");
            File.WriteAllText(Path.Combine(Src, "tool.c"), "int tool(void){ return 2; }\n");      // compiled, never called
            File.WriteAllText(Path.Combine(Src, "other.c"), "int other(void){ return 3; }\n");    // another target's file
        }
        public string F(string p) => p.Replace('\\', '/');
        public string Path_(string rel) => F(Path.Combine(Src, rel));
        public (int Code, string Out, string Err) Carve(string build, string extra = "", bool log = false)
        {
            var tp = Path.Combine(Root, log ? "build.log" : "build.trace");
            File.WriteAllText(tp, build);
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{F(Path.Combine(Root, "out"))}\"\n[common]\nentryPoints = [\"main\"]\n"
                + $"[builds.b]\n{(log ? "buildLogs" : "buildTraceFiles")} = [\"{F(tp)}\"]\n" + extra);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so.ToString(), se.ToString());
        }
        public string Trace(params string[] rels) => string.Join("\n", rels.Select(Path_)) + "\n";
        public string Carved(string rel) => Path.Combine(Root, "out", "carved", rel);
        public string Summary => File.ReadAllText(Path.Combine(Root, "out", "codecarver", "summary.txt"));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void CompiledButUnreachedFile_IsAPlaceholder_AndBuildOpensAreNotRoots()
    {
        using var w = new Work();
        var (code, o, e) = w.Carve(w.Trace("main.c", "helper.c", "tool.c"));
        Assert.True(code == 0, o + e);
        Assert.Contains("(0 code file(s) rooted)", e);
        Assert.Contains("int helper", File.ReadAllText(w.Carved("helper.c")));
        var tool = File.ReadAllText(w.Carved("tool.c"));
        Assert.Contains("Placeholder written by CodeCarver", tool);
        Assert.DoesNotContain("int tool(", tool);
        Assert.False(File.Exists(w.Carved("other.c")));                     // never compiled: no placeholder
        Assert.Contains("stage0.placeholderFiles = 1", w.Summary);
    }

    [Fact]
    public void FileTheBuildNeverOpened_IsNotParsed_AndDropped()
    {
        using var w = new Work();
        File.WriteAllText(Path.Combine(w.Src, "other.c"), "this is not C at all {{{ (\n");   // would be a parse error if read
        var (code, o, e) = w.Carve(w.Trace("main.c", "helper.c", "tool.c"));
        Assert.True(code == 0, o + e);
        Assert.Contains("parse.filesNotBuiltSkipped = 1", w.Summary);
        Assert.False(File.Exists(w.Carved("other.c")));
    }

    [Fact]
    public void IncompleteBuildTrace_FailsVerify_NamingTheCause()
    {
        using var w = new Work();
        var (code, o, e) = w.Carve(w.Trace("main.c"));                        // helper.c missing from the trace
        Assert.True(code == 3, o + e);
        Assert.Contains(".verify.failed.definedInFileNotBuilt = 1", w.Summary);
    }

    [Fact]
    public void SkipFilesNotBuiltOff_ReadsEverything()
    {
        using var w = new Work();
        var (code, o, e) = w.Carve(w.Trace("main.c"), "[advanced]\nskipFilesNotBuilt = false\n");
        Assert.True(code == 0, o + e);
        Assert.Contains("int helper", File.ReadAllText(w.Carved("helper.c")));
        Assert.DoesNotContain("filesNotBuiltSkipped", w.Summary);
    }

    [Fact]
    public void BuildLogAlone_AlsoGivesPlaceholders()
    {
        using var w = new Work();
        var log = string.Join("\n", new[] { "main.c", "helper.c", "tool.c" }.Select(r => $"gcc -c {w.Path_(r)} -o {r}.o")) + "\n";
        var (code, o, e) = w.Carve(log, log: true);
        Assert.True(code == 0, o + e);
        Assert.Contains("Placeholder written by CodeCarver", File.ReadAllText(w.Carved("tool.c")));
        Assert.False(File.Exists(w.Carved("other.c")));
    }

    [Fact]
    public void PlaceholderFilesOff_DropsTheFileOutright()
    {
        using var w = new Work();
        var (code, o, e) = w.Carve(w.Trace("main.c", "helper.c", "tool.c"), "[advanced]\nplaceholderFiles = false\n");
        Assert.True(code == 0, o + e);
        Assert.False(File.Exists(w.Carved("tool.c")));
        Assert.Contains("stage0.placeholderFiles = 0", w.Summary);
    }
}
