using CodeCarver.Core.Preprocess;
using CodeCarver.Tests.Cli;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The build's own preprocessor decides #if branches: a conditional block is live when a line of it came through any
/// compile's -E output, and dead otherwise — but only where the compiles seen are all there are.
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

    /// <summary>A multi-line macro call prints its whole expansion on the call's first line: a block among its arguments
    /// has no line of its own in the output, so it is never decided dead. A parenthesis in a comment or string doesn't
    /// open a call.</summary>
    [Fact]
    public void BlockInsideAMacroCall_IsNeverDead()
    {
        var call = "int r = CHECK(1\n#ifdef D\n  && dbg()\n#else\n  && 0\n#endif\n);\n";
        var dead = CompilerPreprocess.DeadLineMap(call, new HashSet<int> { 1 })!;
        Assert.False(dead[3]);
        Assert.False(dead[5]);
        var quoted = "/* ( */ const char *s = \"(\";\n#ifdef D\nint a;\n#endif\n";
        Assert.True(CompilerPreprocess.DeadLineMap(quoted, new HashSet<int> { 1 })![3]);
    }

    [Fact]
    public void PreprocessArguments_DropOutputsAndDependencyFlags_KeepTheConfiguration()
    {
        var args = new[] { "-c", "-o", "a.o", "-MD", "-MF", "a.d", "-Wp,-MD,a.d,-DKEEP_ME", "-DX=1", "-Iinc", "-dumpbase", "a",
                           "-dynamic-thing", "-Werror", "-MJ", "a.json", "--write-dependencies", "-save-temps", "a.c" };
        Assert.Equal(new[] { "-Wp,-DKEEP_ME", "-DX=1", "-Iinc", "-dynamic-thing", "a.c", "-E", "-dD" },
                     CompilerPreprocess.PreprocessArguments(args));
    }

    [Theory]
    [InlineData("gcc", true)]
    [InlineData("/opt/x/bin/arm-none-eabi-gcc", true)]
    [InlineData(@"C:\tc\bin\arm-none-eabi-g++.exe", true)]
    [InlineData("clang-17", true)]
    [InlineData("cc", true)]
    [InlineData("cl", false)]
    [InlineData("clang-cl", false)]
    [InlineData("armcc", false)]
    [InlineData("/usr/bin/python3", false)]
    public void OnlyGccStyleDrivers_Run(string driver, bool runs) => Assert.Equal(runs, CompilerPreprocess.IsGccFamily(driver));

    [Theory]
    [InlineData("-wrapper")]
    [InlineData("-specs=evil.specs")]
    [InlineData("-fplugin=x.so")]
    [InlineData("-B/tmp/tools")]
    [InlineData("-fpass-plugin=x.so")]
    public void FlagsThatRunOtherCode_AreRefused(string flag)
        => Assert.True(CompilerPreprocess.Refuse(new[] { "-c", flag, "a.c" }));

    [Fact]
    public void MapArguments_RewritesSourcesAndIncludePaths()
    {
        string Map(string p) => p.StartsWith("C:/work/", StringComparison.Ordinal) ? "/here/" + p[8..] : p;
        Assert.Equal(new[] { "-I/here/inc", "-isystem", "/here/sys", "-DX=C:/work/no", "/here/a.c", "-include/here/cfg.h" },
                     CompilerPreprocess.MapArguments(new[] { "-IC:/work/inc", "-isystem", "C:/work/sys", "-DX=C:/work/no",
                                                             "C:/work/a.c", "-includeC:/work/cfg.h" }, Map));
    }

    // ---- end to end with a real gcc -------------------------------------------------------------------------------

    static string Gcc(string gcc) => $"compiler = \"{gcc.Replace('\\', '/')}\"\n";

    /// <summary>A feature macro a config header defines picks the branch, so the file only the other branch calls is
    /// dropped.</summary>
    [Fact]
    public void ConfigHeaderMacro_DecidedByTheCompiler_DropsTheOtherBranch()
    {
        var gcc = FindGcc();
        if (gcc is null) return;   // no compiler on this machine
        using var t = new TreeCarve();
        t.W("cfg.h", "#ifndef CFG_H\n#define CFG_H\n#define FEATURE_FAST 1\n#endif\n")
         .W("main.c", "#include \"cfg.h\"\nint fast_path(void);\nint slow_path(void);\n"
                    + "int main(void){\n#if FEATURE_FAST\n  return fast_path();\n#else\n  return slow_path();\n#endif\n}\n")
         .W("fast.c", "int fast_path(void){ return 1; }\n")
         .W("slow.c", "int slow_path(void){ return 2; }\n")
         .Compile("main.c").Compile("fast.c").Compile("slow.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.Contains("world.preprocessed.failed = 0", t.Summary);
        Assert.True(t.Kept("fast.c"), o + e);
        Assert.False(t.Kept("slow.c"), o + e);
    }

    /// <summary>An #ifdef among a multi-line macro call's arguments, taken by the build: its callee stays.</summary>
    [Fact]
    public void BlockInMacroArguments_TakenByTheBuild_KeepsItsCallee()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        t.W("main.c", "int dbg(void);\n#define CHECK(x) ((x) ? 0 : 1)\nint main(void){\n  return CHECK(1\n#ifdef DEBUG\n    && dbg()\n#endif\n  );\n}\n")
         .W("dbg.c", "int dbg(void){ return 1; }\n")
         .Compile("main.c", "-DDEBUG").Compile("dbg.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("dbg.c"), o + e);
    }

    /// <summary>A command whose response file is gone ran with other flags than the build's: it isn't run, and the file
    /// keeps the scanner's (open-world) answer.</summary>
    [Fact]
    public void IncompleteCommand_IsNotRun()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        t.W("main.c", "int dbg(void);\nint main(void){\n#ifdef DEBUG\n  return dbg();\n#endif\n  return 0;\n}\n")
         .W("dbg.c", "int dbg(void){ return 1; }\n")
         .Compile("main.c", "@flags.rsp").Compile("dbg.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("dbg.c"), o + e);
    }

    /// <summary>A translation unit the log doesn't compile might include a header with another configuration: no
    /// header is decided.</summary>
    [Fact]
    public void UnloggedUnit_LeavesHeadersUndecided()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        t.W("h.h", "int impl(void);\n#ifdef USE_NET\nstatic inline int net_go(void){ return impl(); }\n#endif\n")
         .W("netcfg.h", "#define USE_NET 1\n")
         .W("net.c", "#include \"netcfg.h\"\n#include \"h.h\"\nint net(void){ return net_go(); }\n")
         .W("main.c", "#include \"h.h\"\nint net(void);\nint main(void){ return net(); }\n")
         .W("impl.c", "int impl(void){ return 1; }\n")
         .Compile("main.c").Compile("impl.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("impl.c"), o + e);
    }

    /// <summary>An include directory that doesn't exist here: gcc would skip it and find the same name elsewhere.</summary>
    [Fact]
    public void MissingIncludeDirectory_IsNotRun()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        t.W("default/config.h", "#define FEATURE 0\n")
         .W("main.c", "#include \"config.h\"\nint feat(void);\nint main(void){\n#if FEATURE\n  return feat();\n#endif\n  return 0;\n}\n")
         .W("feat.c", "int feat(void){ return 1; }\n")
         .Compile("main.c", $"-I{t.Outside("gone/cfgA")} -Idefault").Compile("feat.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.Contains("world.preprocessed.failedMissingPaths = 1", t.Summary);
        Assert.True(t.Kept("feat.c"), o + e);
    }

    /// <summary>A .c another unit #includes is a header for that unit: when that unit's command didn't run, the file
    /// is not decided by its own command alone.</summary>
    [Fact]
    public void IncludedSource_WithAnIncluderNotRun_IsNotDecided()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        t.W("tbl.c", "int big(void);\nint tbl(void){\n#ifdef BIG\n  return big();\n#endif\n  return 0;\n}\n")
         .W("main.c", "#include \"tbl.c\"\nint main(void){ return tbl(); }\n")
         .W("big.c", "int big(void){ return 1; }\n")
         .Compile("main.c", $"-DBIG -include {t.Outside("gone/cfg.h")}").Compile("tbl.c").Compile("big.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("big.c"), o + e);
    }

    /// <summary>One header reached by two compiles under two spellings (a directory link): one file, both compiles'
    /// evidence.</summary>
    [Fact]
    public void HeaderThroughALink_CountsForTheSameFile()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        t.W("inc/h.h", "int fast(void);\n#ifdef X\nstatic inline int pick(void){ return fast(); }\n#else\nstatic inline int pick(void){ return 0; }\n#endif\n")
         .W("a.c", "#include \"h.h\"\nint a(void){ return pick(); }\n")
         .W("b.c", "#include \"h.h\"\nint a(void);\nint main(void){ return a() + pick(); }\n")
         .W("fast.c", "int fast(void){ return 1; }\n");
        var link = Path.GetFullPath(t.Outside("inc_link"));
        Assert.True(MakeDirectoryLink(link, Path.GetFullPath(Path.Combine(t.Src, "inc"))), "could not make a directory link");
        link = link.Replace('\\', '/');
        t.Compile("a.c", "-Iinc").Compile("b.c", $"-I{link} -DX").Compile("fast.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.True(t.Kept("fast.c"), o + e);
    }

    /// <summary>A command line too long to pass directly goes through a response file and still decides.</summary>
    [Fact]
    public void LongCommand_RunsThroughAResponseFile()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        var many = string.Join(" ", Enumerable.Range(0, 500).Select(i => $"-DMODD_SOME_LONG_SETTING_NAME_{i}=1"));
        t.W("main.c", "int slow(void);\nint main(void){\n#ifdef MODD_SOME_LONG_SETTING_NAME_499\n  return 0;\n#else\n  return slow();\n#endif\n}\n")
         .W("slow.c", "int slow(void){ return 1; }\n")
         .Compile("main.c", many).Compile("slow.c");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.Contains("world.preprocessed.failed = 0", t.Summary);
        Assert.False(t.Kept("slow.c"), o + e);
    }

    /// <summary>A log naming a wrapper program: the carve never runs it.</summary>
    [Fact]
    public void Wrapper_IsNeverRun()
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        using var t = new TreeCarve();
        var marker = t.Outside("ran.txt");
        t.W("main.c", "int main(void){ return 0; }\n")
         .Compile("main.c", $"-wrapper {t.Outside("evil.sh")},-c,touch");
        File.WriteAllText(t.Outside("evil.sh"), $"#!/bin/sh\ntouch {marker}\n");
        var (code, o, e) = t.Carve(extraToml: Gcc(gcc));
        Assert.True(code == 0, o + e);
        Assert.False(File.Exists(marker));
        Assert.Contains("world.preprocessed.refused = 1", t.Summary);
    }

    static string? FindGcc()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, ".toolchains", "w64devkit", "bin", "gcc.exe");
            if (File.Exists(p)) return p;
        }
        var name = OperatingSystem.IsWindows() ? "gcc.exe" : "gcc";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name); } catch (ArgumentException) { }
        }
        return null;
    }

    static bool MakeDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
