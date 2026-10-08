using CodeCarver.Core.Preprocess;
using CodeCarver.Tests.Cli;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The build's own preprocessor decides #if branches: a conditional block is live when a line of it came through any
/// compile's -E output, and dead otherwise.
/// </summary>
public sealed class CompilerPreprocessTests
{
    const string Text =
        "int a;\n"                 // 1
        + "#if X\n"                // 2
        + "int b;\n"               // 3
        + "#else\n"                // 4
        + "int c;\n"               // 5
        + "#ifdef Y\n"             // 6
        + "int d;\n"               // 7
        + "#endif\n"               // 8
        + "#endif\n"               // 9
        + "#ifdef Z\n"             // 10
        + "#include \"z.h\"\n"     // 11
        + "#endif\n"               // 12
        + "#ifdef W\n"             // 13
        + "#define W2 1\n"         // 14
        + "#endif\n";              // 15

    [Fact]
    public void Blocks_AreLive_ByWhatCameThrough()
    {
        var dead = CompilerPreprocess.DeadLineMap(Text, new HashSet<int> { 1, 7 })!;
        Assert.False(dead[1]);
        Assert.True(dead[3]);     // #if X: nothing came through
        Assert.False(dead[5]);    // #else: live because its nested block is
        Assert.False(dead[7]);
        Assert.False(dead[11]);   // an #include needs no evidence (a guarded header prints nothing the second time)
        Assert.True(dead[14]);    // -dD prints a live #define; this one wasn't
        Assert.False(CompilerPreprocess.DeadLineMap(Text, new HashSet<int> { 14 })![14]);
    }

    [Theory]
    [InlineData("#line 10 \"x.y\"\nint a;\n")]
    [InlineData("#if X\nint a;\n")]
    public void Renumbered_OrUnbalanced_IsNotDecided(string text)
        => Assert.Null(CompilerPreprocess.DeadLineMap(text, new HashSet<int>()));

    /// <summary>End to end with a real gcc: a feature macro a config header defines picks the branch, so the file only
    /// the other branch calls is dropped. Needs gcc (on PATH, or the repo's Windows toolchain).</summary>
    [Fact]
    public void ConfigHeaderMacro_DecidedByTheCompiler_DropsTheOtherBranch()
    {
        var gcc = FindGcc();
        if (gcc is null) return;   // no compiler on this machine (CI's Linux job has one)
        using var t = new TreeCarve();
        t.W("cfg.h", "#ifndef CFG_H\n#define CFG_H\n#define FEATURE_FAST 1\n#endif\n")
         .W("main.c", "#include \"cfg.h\"\nint fast_path(void);\nint slow_path(void);\n"
                    + "int main(void){\n#if FEATURE_FAST\n  return fast_path();\n#else\n  return slow_path();\n#endif\n}\n")
         .W("fast.c", "int fast_path(void){ return 1; }\n")
         .W("slow.c", "int slow_path(void){ return 2; }\n")
         .Compile("main.c").Compile("fast.c").Compile("slow.c");
        var (code, o, e) = t.Carve(common: "", extraToml: $"compiler = \"{gcc.Replace('\\', '/')}\"\n");
        Assert.True(code == 0, o + e);
        Assert.Contains("world.preprocessed.failed = 0", t.Summary);
        Assert.True(t.Kept("fast.c"), o + e);
        Assert.False(t.Kept("slow.c"), o + e);
    }

    static string? FindGcc()
    {
        var repo = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(repo); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, ".toolchains", "w64devkit", "bin", "gcc.exe");
            if (File.Exists(p)) return p;
        }
        if (OperatingSystem.IsWindows()) return null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(dir, "gcc"))) return Path.Combine(dir, "gcc");
        return null;
    }
}
