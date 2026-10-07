using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A nasty BUILD, not nasty code (examples/hellbuild): parallel recursive make whose "Entering directory" lines
/// interleave, compiles hidden in <c>sh -c '...'</c> or behind a libtool-like launcher, a configure probe naming a
/// file the tree never has, sources generated from a template or a script, and quiet makefiles that name a source
/// only by its stem. Each must carve to a tree that still builds.
/// </summary>
public sealed class HorrorBuildTests
{
    static void AssertOk(TreeCarve t, (int Code, string Out, string Err) r)
        => Assert.True(r.Code == 0, $"exit {r.Code}\n{r.Out}{r.Err}\n--- verify.txt\n{t.VerifyLog}");

    // ---- the scraper -------------------------------------------------------------------------------------------

    [Fact]
    public void ParallelMake_LeavingRemovesTheNamedDirectory_AndOpenSiblingsAreAlternatives()
    {
        var cmds = BuildLogScraper.Parse(string.Join("\n",
            "make[1]: Entering directory '/w/liba'",
            "make[1]: Entering directory '/w/libb'",
            "gcc -DLIBA -c util.c -o a.o",               // printed by liba while libb was entered last
            "make[1]: Leaving directory '/w/liba'",
            "gcc -DLIBB -c util.c -o b.o",               // libb's: liba has left
            "make[1]: Leaving directory '/w/libb'"));
        Assert.Equal(2, cmds.Count);
        Assert.Equal("/w/libb", cmds[0].Directory);
        Assert.Contains("/w/liba", cmds[0].AlternativeDirectories);
        Assert.Equal("/w/libb", cmds[1].Directory);       // not /w/liba: the Leaving line named liba
        Assert.DoesNotContain("/w/liba", cmds[1].AlternativeDirectories);
    }

    [Fact]
    public void SequentialMake_HasNoAlternatives()
    {
        var cmds = BuildLogScraper.Parse(string.Join("\n",
            "make[1]: Entering directory '/w/liba'",
            "gcc -c util.c -o a.o",
            "make[1]: Leaving directory '/w/liba'"));
        Assert.Equal("/w/liba", Assert.Single(cmds).Directory);
        Assert.Empty(cmds[0].AlternativeDirectories);
    }

    [Theory]
    [InlineData("sh -c 'gcc -DSHC_MODE=2 -c shc.c -o shc.o'", "shc.c", "SHC_MODE=2")]
    [InlineData("bash -c \"cd sub && gcc -DX=1 -c x.c\"", "x.c", "X=1")]
    [InlineData("bash --norc -c 'gcc -DX=1 -c x.c'", "x.c", "X=1")]
    [InlineData("bash -o pipefail -ec 'gcc -DX=1 -c x.c'", "x.c", "X=1")]
    [InlineData("busybox sh -c 'gcc -DX=1 -c x.c'", "x.c", "X=1")]
    [InlineData("/bin/sh ../tools/ltwrap --mode=compile /opt/ccache gcc -DLT_MODE=1 -c lt.c -o lt.o", "lt.c", "LT_MODE=1")]
    [InlineData("/bin/bash ../libtool --tag=CC --mode=compile gcc -DLT_MODE=1 -c lt.c -o lt.lo", "lt.c", "LT_MODE=1")]
    public void CompilesBehindAShell_AreFound(string line, string file, string define)
    {
        var c = Assert.Single(BuildLogScraper.Parse(line));
        Assert.Equal(file, c.File);
        Assert.Contains(define, c.Defines);
    }

    [Theory]
    [InlineData("sh ./configure --prefix=/x")]
    [InlineData("sh -c 'echo gcc -c x.c'")]
    [InlineData("bash gen/mkhooks.sh mkhooks.c")]
    public void ShellLinesWithoutACompile_AreNotCompiles(string line) => Assert.Empty(BuildLogScraper.Parse(line));

    // ---- the carve ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParallelMake_SwappedDirectories_StillKeepWhatEachFileReallyCalls()
    {
        // Both libraries have a util.c. The log, as make -j prints it, attributes liba's compile to libb and libb's to
        // liba: read that way, each util.c is closed-world WITHOUT its own -D and its real branch looks dead.
        const string util = "#ifdef LIB{0}\nint lib{1}_util(void) {{ return lib{1}_only(); }}\n#else\nint lib{1}_util(void) {{ return -1; }}\n#endif\n";
        using var t = new TreeCarve()
            .W("main.c", "int liba_util(void); int libb_util(void);\nint main(void){ return liba_util() + libb_util(); }\n")
            .W("liba/util.c", "int liba_only(void);\n" + string.Format(util, "A", "a"))
            .W("liba/a_only.c", "int liba_only(void) { return 10; }\n")
            .W("libb/util.c", "int libb_only(void);\n" + string.Format(util, "B", "b"))
            .W("libb/b_only.c", "int libb_only(void) { return 20; }\n")
            .Compile("main.c");
        t.LogLines.AddRange(new[]
        {
            $"make[1]: Entering directory '{t.S("liba")}'",
            $"make[1]: Entering directory '{t.S("libb")}'",
            "gcc -DLIBA -c util.c -o liba_util.o",
            "gcc -c a_only.c -o a_only.o",
            $"make[1]: Leaving directory '{t.S("liba")}'",
            "gcc -DLIBB -c util.c -o libb_util.o",
            "gcc -c b_only.c -o b_only.o",
            $"make[1]: Leaving directory '{t.S("libb")}'",
        });
        var r = t.Carve();
        AssertOk(t, r);
        Assert.True(t.Kept("liba/a_only.c"), r.Out + r.Err);
        Assert.True(t.Kept("libb/b_only.c"), r.Out + r.Err);
    }

    [Fact]
    public void ACompileOfAFileTheTreeNeverHas_DoesNotMakeTheTraceIncomplete()
    {
        // configure's probe prints "gcc -c conftest.c" with no directory: it lands on the carve root, where no such file is.
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("legacy/old.c", "int old(void) { return 1; }\n")
            .Compile("main.c").TraceCompiled();
        t.LogLines.Insert(0, "gcc -c conftest.c -o conftest.o");
        var r = t.Carve();
        AssertOk(t, r);
        Assert.Contains("build.traceMissedCompiledFiles = 0", t.Summary);
    }

    [Theory]
    [InlineData("gen/table.c.in", "/* @BIAS@ is replaced by sed */\nint table_sum(void) { return gen_hook(); }\n")]
    [InlineData("gen/mkhooks.sh", "#!/bin/sh\ncat > \"$1\" <<EOC\nint hooks_total(void) { return gen_hook() + 1; }\nEOC\n")]
    [InlineData("gen/mkhooks.py", "import sys\nopen(sys.argv[1], 'w').write('int hooks_total(void) { return gen_hook(); }\\n')\n")]
    public void AFunctionOnlyGeneratedCodeCalls_IsKept_AndVerifyKnows(string generator, string text)
    {
        // The generated file lives in the build directory, outside the tree: only its template or generator is here.
        using var t = new TreeCarve()
            .W("main.c", "int table_sum(void);\nint main(void){ return 0; }\n")
            .W("gen/hooks.c", "int gen_hook(void) { return 3; }\nint never(void) { return 4; }\n")
            .W("other.c", "int other(void) { return 5; }\n")
            .W(generator, text);
        var r = t.Carve(log: false);
        AssertOk(t, r);
        Assert.True(t.Kept("gen/hooks.c"), r.Out + r.Err);
        Assert.False(t.Kept("other.c"), "a build script naming nothing of other.c must not keep it");
    }

    [Theory]
    [InlineData("Makefile", "OBJS := $(addsuffix .o,q1 q2)\n")]
    [InlineData("build.sh", "#!/bin/sh\ngcc -c \"it's here.c\"\n")]
    [InlineData("sub/CMakeLists.txt", "add_executable(app ../main.c ../q2.c)\n")]
    public void ADroppedFileTheBuildFilesName_BecomesAPlaceholder(string script, string text)
    {
        // No build log or trace says what was compiled, but the build files the carved tree keeps still name it.
        var dropped = text.Contains("it's") ? "it's here.c" : "q2.c";
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W(dropped, "int unused(void) { return 2; }\n")
            .W("legacy/decoy.c", "int decoy(void) { return 3; }\n")
            .W(script, text);
        var r = t.Carve(log: false);
        AssertOk(t, r);
        Assert.True(t.Placeholder(dropped), r.Out + r.Err);
        Assert.False(File.Exists(t.CarvedPath("legacy/decoy.c")), "no build file names the decoy");
    }

    [Fact]
    public void TraceOnly_ADecoyUsedOnlyInAnUncertainBranch_IsANoteNotAFailure()
    {
        // No log, so LT_MODE is unknown and both branches are kept; the trace says the build never opened the decoy.
        using var t = new TreeCarve()
            .W("main.c", "int lt_fn(void);\nint main(void){ return lt_fn(); }\n")
            .W("lt.c", "int lt_decoy(void);\n#if LT_MODE\nint lt_fn(void) { return 5; }\n#else\nint lt_fn(void) { return lt_decoy(); }\n#endif\n")
            .W("legacy/lt_decoy.c", "int lt_decoy(void) { return 666; }\n");
        t.TracePaths = new() { t.S("main.c"), t.S("lt.c") };
        var r = t.Carve(log: false);
        AssertOk(t, r);
        Assert.False(File.Exists(t.CarvedPath("legacy/lt_decoy.c")));
        Assert.Contains("NOTE lt_decoy", t.VerifyLog);
    }

    [Fact]
    public void TraceOnly_ADecoyUsedUnconditionally_StillFails()
    {
        // The same shape without the #if: the trace must have missed a file, and verify must say so.
        using var t = new TreeCarve()
            .W("main.c", "int lt_fn(void);\nint main(void){ return lt_fn(); }\n")
            .W("lt.c", "int lt_decoy(void);\nint lt_fn(void) { return lt_decoy(); }\n")
            .W("legacy/lt_decoy.c", "int lt_decoy(void) { return 666; }\n");
        t.TracePaths = new() { t.S("main.c"), t.S("lt.c") };
        var r = t.Carve(log: false);
        Assert.Equal(3, r.Code);
        Assert.Contains("FAIL lt_decoy", t.VerifyLog);
    }
}
