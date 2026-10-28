using Xunit;

namespace CodeCarver.Tests.Examples;

/// <summary>
/// examples/used-macros: a build captured with the -dU collector (collect/du-wrap.sh) on a Linux build machine.
/// run.sh builds it with real gcc, carves it and builds and runs every carved stage (CI's horror job); this carves from
/// the checked-in capture anywhere and pins what the max stage's register header keeps.
/// </summary>
public sealed class UsedMacrosExampleTests
{
    sealed class Run : ExampleRun { public Run(string? buildSection = null, Action<string>? before = null) : base("used-macros", buildSection, before) { } }

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
        using var r = new Run($"[builds.main]\nbuildLogs = [\"inputs/build.log\"]\nppTrace = \"{exe!.Replace('\\', '/')}\"\n"
                              + "ppTraceArgs = [\"-w\"]\nppTraceDropOptions = [\"-fkq-never-*\"]\n");
        Assert.True(r.Code == 0, r.Output);
        Assert.Contains("3 of 3 compile command(s) ran clean", r.Output);
        Assert.Contains("world.ppTrace.trusted = 1", r.Summary("max"));
        Assert.Contains("world.ppTrace.args = 1", r.Summary("max"));
        Assert.Contains("world.ppTrace.dropped.patternsUnused = 1", r.Summary("max"));
        Assert.Equal(Used.Append("KQ_REGS_H").Order(StringComparer.Ordinal),
                     Defines(r.Carved("max", "inc/kq_regs.h")).Order(StringComparer.Ordinal));
    }

    /// <summary>The names cover what the carve keeps: a source file no command compiles doesn't stop them while the carve
    /// drops it (a tree holds many a build never compiles), and makes the header carve model the macros once it is kept.</summary>
    [Fact]
    public void ASourceFileInNoCommand_MattersOnlyWhenKept()
    {
        const string Spare = "#include \"kq_regs.h\"\nunsigned kq_spare(void) { return KQ_SPI_CTRL_OFF; }\n";
        using (var dropped = new Run(before: d => File.WriteAllText(Path.Combine(d, "src", "spare.c"), Spare)))
        {
            Assert.True(dropped.Code == 0, dropped.Output);
            Assert.Contains("world.usedMacros.unitsWithoutCommand = 1", dropped.Summary("max"));
            Assert.Contains("headerCarve.keptUnitsWithoutCommand = 0", dropped.Summary("max"));
            Assert.Contains("headerCarve.fromUsedMacros = 1", dropped.Summary("max"));
            Assert.DoesNotContain("KQ_SPI_CTRL_OFF", Defines(dropped.Carved("max", "inc/kq_regs.h")));
        }
        using var kept = new Run(before: d =>
        {
            File.WriteAllText(Path.Combine(d, "src", "spare.c"), Spare);
            var cfg = Path.Combine(d, "carve.toml");
            File.WriteAllText(cfg, File.ReadAllText(cfg).Replace("entryPoints = [\"main\"]", "entryPoints = [\"main\", \"kq_spare\"]"));
        });
        Assert.True(kept.Code == 0, kept.Output);
        Assert.Contains("headerCarve.keptUnitsWithoutCommand = 1", kept.Summary("max"));
        Assert.Contains("headerCarve.fromUsedMacros = 0", kept.Summary("max"));
        Assert.Contains("KQ_SPI_CTRL_OFF", Defines(kept.Carved("max", "inc/kq_regs.h")));
    }

    /// <summary>A source file no command compiles but one that does #includes: pp-trace read it in that compile, so its
    /// macros are among the names and the stage still carves from them.</summary>
    [SkippableFact]
    public void PpTrace_ASourceFileACompileIncludes_IsCovered()
    {
        var exe = CodeCarver.Tests.Preprocess.PpTraceTests.Find();
        Skip.If(exe is null, "pp-trace (LLVM) not installed");
        using var r = new Run($"[builds.main]\nbuildLogs = [\"inputs/build.log\"]\nppTrace = \"{exe!.Replace('\\', '/')}\"\n"
                              + "ppTraceArgs = [\"-w\"]\nppTraceDropOptions = [\"-fkq-never-*\"]\n", d =>
        {
            File.WriteAllText(Path.Combine(d, "src", "part.c"), "unsigned kq_part(void) { return KQ_SPI_CTRL_OFF; }\n");
            File.AppendAllText(Path.Combine(d, "src", "uart.c"), "\n#include \"part.c\"\n");
        });
        Assert.True(r.Code == 0, r.Output);
        Assert.Contains("world.ppTrace.unitsWithoutCommand = 1", r.Summary("max"));
        Assert.Contains("world.ppTrace.unitsWithoutCommandRead = 1", r.Summary("max"));
        Assert.Contains("headerCarve.keptUnitsWithoutCommand = 0", r.Summary("max"));
        Assert.Contains("headerCarve.fromUsedMacros = 1", r.Summary("max"));
        Assert.Contains("KQ_SPI_CTRL_OFF", Defines(r.Carved("max", "inc/kq_regs.h")));
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
