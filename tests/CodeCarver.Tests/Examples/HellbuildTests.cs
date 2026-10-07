using Xunit;

namespace CodeCarver.Tests.Examples;

/// <summary>
/// examples/hellbuild: plain code, nightmare build (make -j with interleaved sub-makes, generated sources, a configure
/// step, compiles behind ccache, a libtool-like launcher and sh -c, a lost response file, a symlink, quiet rules).
/// tools/eldritch/eldritch-oracle.ps1 -Example hellbuild builds and runs every carve; this carves from the captured
/// inputs anywhere and pins what each input set must keep, drop and stub.
/// </summary>
public sealed class HellbuildTests
{
    sealed class Run : ExampleRun { public Run(string? buildSection = null) : base("hellbuild", buildSection) { } }

    static readonly string[] Stages = { "safe", "headers", "aggressive", "max" };

    // What the build needs whatever the input set: each sub-make's helper (behind swapped Entering/Leaving lines),
    // what only generated code calls, and the file only config.h (written into the build directory) selects.
    static readonly string[] Needed = { "liba/a_only.c", "libb/b_only.c", "gen/hooks.c", "cfg/spell_real.c", "lt.c", "shc.c",
                                        "café.c", "linked/lnk.c", "rsp.c", "quiet/q1.c" };

    [Fact]
    public void LogAndTrace_EveryStageVerifies_DecoysGo_UnreachedCompiledFilesAreStubs()
    {
        using var r = new Run();
        Assert.True(r.Code == 0, r.Output);
        foreach (var s in Stages)
        {
            foreach (var d in new[] { "legacy/fallback.c", "legacy/lt_decoy.c", "legacy/shc_decoy.c" })
                Assert.False(File.Exists(r.Carved(s, d)), $"{s}: {d} kept\n{r.Output}");
            foreach (var stub in new[] { "it's here.c", "quiet/q2.c" })
                Assert.True(r.Placeholder(s, stub), $"{s}: {stub} is not a placeholder\n{r.Output}");
            foreach (var need in Needed)
                Assert.True(r.Kept(s, need), $"{s}: {need} not kept\n{r.Output}");
        }
        // The log and the trace agree: configure's conftest.c and the parallel sub-makes' compiles all resolve.
        Assert.Contains("build.traceMissedCompiledFiles = 0", r.Summary("safe"));
        Assert.DoesNotContain("verify  : FAILED", r.Output);
    }

    [Fact]
    public void LogOnly_FindsTheShellAndLauncherCompiles_AndStubsWhatTheMakefileNamesByStem()
    {
        using var r = new Run("[builds.main]\nbuildLogs = [\"inputs/build.log\"]\n");
        Assert.True(r.Code == 0, r.Output);
        foreach (var s in Stages)
        {
            // lt.c (libtool-like launcher) and shc.c (sh -c) are logged with their -D, so their decoy branches are dead.
            Assert.False(File.Exists(r.Carved(s, "legacy/lt_decoy.c")), $"{s}: lt_decoy kept\n{r.Output}");
            Assert.False(File.Exists(r.Carved(s, "legacy/shc_decoy.c")), $"{s}: shc_decoy kept\n{r.Output}");
            // Compiled with no command in the log ("  CC q2.o"): the makefile names it only as $(addsuffix .o,q1 q2).
            Assert.True(r.Placeholder(s, "quiet/q2.c"), $"{s}: q2.c is not a placeholder\n{r.Output}");
            foreach (var need in Needed)
                Assert.True(r.Kept(s, need), $"{s}: {need} not kept\n{r.Output}");
        }
        Assert.DoesNotContain("verify  : FAILED", r.Output);
    }

    [Theory]
    [InlineData("[builds.main]\nbuildTraceFiles = [\"inputs/build.trace\"]\n")]
    [InlineData("")]
    public void OtherInputSets_StillVerify_AndKeepWhatTheBuildNeeds(string buildSection)
    {
        using var r = new Run(buildSection);
        Assert.True(r.Code == 0, r.Output);
        Assert.DoesNotContain("verify  : FAILED", r.Output);
        foreach (var s in Stages)
        {
            foreach (var need in Needed)
                Assert.True(r.Kept(s, need), $"{s}: {need} not kept\n{r.Output}");
            Assert.True(r.Placeholder(s, "it's here.c"), $"{s}: it's here.c is not a placeholder\n{r.Output}");
        }
    }
}
