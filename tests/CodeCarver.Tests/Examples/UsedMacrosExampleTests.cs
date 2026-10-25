using Xunit;

namespace CodeCarver.Tests.Examples;

/// <summary>
/// examples/used-macros: a build captured with the -dU collector (collect/du-wrap.sh) on a Linux build machine.
/// run.sh builds it with real gcc, carves it and builds and runs every carved stage (CI's horror job); this carves from
/// the checked-in capture anywhere and pins what the max stage's register header keeps.
/// </summary>
public sealed class UsedMacrosExampleTests
{
    sealed class Run : ExampleRun { public Run(string? buildSection = null) : base("used-macros", buildSection) { } }

    // What the build's compiles used: the paste macros, the UART fields uart.c pastes, and the two block bases the
    // preprocessed memmap.txt.in names.
    static readonly string[] Used =
    {
        "KQ_CAT", "KQ_REG", "KQ_SHIFT", "KQ_MASK", "KQ_FIELD", "KQ_BLK_UART", "KQ_BLK_SPI", "KQ_UART_CTRL_OFF",
        "KQ_UART_CTRL_EN_SHFT", "KQ_UART_CTRL_EN_BMSK", "KQ_UART_CTRL_MODE_SHFT", "KQ_UART_CTRL_MODE_BMSK",
    };

    static string[] Defines(string header) =>
        File.ReadAllLines(header).Where(l => l.StartsWith("#define ", StringComparison.Ordinal))
            .Select(l => l[8..].Split(' ', '(')[0]).ToArray();

    [Fact]
    public void TheCapturedBuild_KeepsExactlyWhatItsCompilesUsed()
    {
        using var r = new Run();
        Assert.True(r.Code == 0, r.Output);
        Assert.Contains("3 of 3 compile command(s) covered", r.Output);
        Assert.Contains("world.usedMacros.trusted = 1", r.Summary("max"));
        Assert.Equal(Used.Append("KQ_REGS_H").Order(StringComparer.Ordinal),
                     Defines(r.Carved("max", "inc/kq_regs.h")).Order(StringComparer.Ordinal));
        Assert.Equal(File.ReadAllText(Path.Combine(r.Dir, "src", "inc", "kq_regs.h")), File.ReadAllText(r.Carved("safe", "inc/kq_regs.h")));
    }

    /// <summary>A Clang build's route: pp-trace, run here on the log's commands, says the same names as -dU said.</summary>
    [SkippableFact]
    public void PpTraceOnTheLog_KeepsWhatTheCompilesUsed()
    {
        var exe = CodeCarver.Tests.Preprocess.PpTraceTests.Find();
        Skip.If(exe is null, "pp-trace (LLVM) not installed");
        using var r = new Run($"[builds.main]\nbuildLogs = [\"inputs/build.log\"]\nppTrace = \"{exe!.Replace('\\', '/')}\"\n");
        Assert.True(r.Code == 0, r.Output);
        Assert.Contains("3 of 3 compile command(s) ran clean", r.Output);
        Assert.Contains("world.ppTrace.trusted = 1", r.Summary("max"));
        Assert.Equal(Used.Append("KQ_REGS_H").Order(StringComparer.Ordinal),
                     Defines(r.Carved("max", "inc/kq_regs.h")).Order(StringComparer.Ordinal));
    }

    /// <summary>Without the files the static carve models the paste macros itself: sound, and on this sample it also
    /// keeps the DMA fields behind dma.c's #if KQ_HAS_DMA, which this configuration turns off.</summary>
    [Fact]
    public void WithoutTheFiles_TheStaticCarveKeepsMore()
    {
        using var r = new Run("[builds.main]\nbuildLogs = [\"inputs/build.log\"]\n");
        Assert.True(r.Code == 0, r.Output);
        var kept = Defines(r.Carved("max", "inc/kq_regs.h"));
        Assert.Superset(Used.ToHashSet(), kept.ToHashSet());
        Assert.Contains("KQ_DMA_CFG_BURST_SHFT", kept);
    }
}
