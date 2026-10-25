using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// <c>[builds.x] usedMacros</c>: what the build's compiles printed with <c>-E -dU</c> (docs/USED-MACROS.md). When every
/// compile command has its file, the max stage's big headers keep only the defines some compile used; otherwise the
/// header carve models the paste macros itself, as without the key.
/// </summary>
public sealed class UsedMacrosCarveTests
{
    const string Stages = "[stages.safe]\ncarveSourceFileContents = false\ncarveHeaderFileContents = false\n"
                        + "[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n"
                        + "[advanced]\nmaxParseBytes = 2000\nmemoryStopMb = 4000\n";

    static TreeCarve Tree()
    {
        var t = new TreeCarve();
        var chip = new System.Text.StringBuilder("#ifndef KQ_CHIP_H\n#define KQ_CHIP_H\n");
        for (var i = 0; i < 200; i++) chip.Append($"#define KQ_REG_{i} (0x4000 + {i * 4})\n");
        chip.Append("#endif\n");
        t.W("inc/kq_chip.h", chip.ToString())
         .W("main.c", "#include \"inc/kq_chip.h\"\nint kq_dev(void);\nint main(void){ return KQ_REG_3 + kq_dev(); }\n")
         .W("dev.c", "#include \"inc/kq_chip.h\"\nint kq_dev(void){ return KQ_REG_5; }\n");
        t.Compile("main.c").Compile("dev.c");
        return t;
    }

    /// <summary>A file as the example collector writes it for one compile of <paramref name="rel"/>.</summary>
    static void Du(TreeCarve t, string name, string rel, params string[] names) =>
        t.WOut("du/" + name, string.Join("\n", new[] { $"{UsedMacros.CwdPrefix}{t.S("")}", $"# 0 \"{rel}\"", "# 0 \"<built-in>\"" }
            .Concat(names.Select(n => $"#define {n} x")).Append(UsedMacros.OkLine)) + "\n");

    static string MaxSummary(TreeCarve t) =>
        Directory.GetDirectories(Path.Combine(t.Root, "out"))
            .Select(d => Path.Combine(d, "codecarver", "summary.txt"))
            .Where(File.Exists).Select(File.ReadAllText)
            .Single(s => s.Contains("headerCarve.fromUsedMacros"));

    static string MaxChip(TreeCarve t) =>
        Directory.GetDirectories(Path.Combine(t.Root, "out"))
            .Select(d => Path.Combine(d, "carved", "inc", "kq_chip.h"))
            .Where(File.Exists).Select(File.ReadAllText)
            .MinBy(s => s.Length)!;

    [Fact]
    public void EveryCommandCovered_KeepsOnlyWhatTheCompilesUsed()
    {
        using var t = Tree();
        // dev.c's compile also used KQ_REG_9 (a configuration the carve can't see): kept although no source names it.
        Du(t, "main.du", "main.c", "KQ_CHIP_H", "KQ_REG_3");
        Du(t, "dev.du", "dev.c", "KQ_CHIP_H", "KQ_REG_5", "KQ_REG_9");
        var (code, o, e) = t.Carve(extraToml: $"usedMacros = [\"{t.Outside("du")}\"]\n" + Stages);
        Assert.True(code == 0, o + e);
        Assert.Contains("the big headers keep only the defines the compiles used", e);
        Assert.Contains("world.usedMacros.trusted = 1", MaxSummary(t));
        Assert.Contains("world.usedMacros.commandsCovered = 2", MaxSummary(t));
        Assert.Matches(@"(?m)headerCarve\.fromUsedMacros = 1\r?$", MaxSummary(t));
        var chip = MaxChip(t);
        foreach (var n in new[] { "KQ_CHIP_H", "KQ_REG_3 ", "KQ_REG_5 ", "KQ_REG_9 " }) Assert.Contains("#define " + n, chip);
        Assert.DoesNotContain("KQ_REG_4 ", chip);
        Assert.DoesNotContain("KQ_REG_199 ", chip);
    }

    [Fact]
    public void ACommandWithoutItsFile_FallsBackToTheStaticCarve()
    {
        using var t = Tree();
        Du(t, "main.du", "main.c", "KQ_CHIP_H", "KQ_REG_3");
        var (code, o, e) = t.Carve(extraToml: $"usedMacros = [\"{t.Outside("du")}\"]\n" + Stages);
        Assert.True(code == 0, o + e);
        Assert.Contains("NOT trusted (1 command(s) without a file)", e);
        Assert.Contains("world.usedMacros.trusted = 0", MaxSummary(t));
        Assert.Matches(@"(?m)headerCarve\.fromUsedMacros = 0\r?$", MaxSummary(t));
        var chip = MaxChip(t);
        Assert.Contains("#define KQ_REG_5 ", chip);      // the static carve read dev.c itself
        Assert.DoesNotContain("KQ_REG_9 ", chip);
    }

    [Fact]
    public void AMissingPath_FailsFast()
    {
        using var t = Tree();
        var (code, _, e) = t.Carve(extraToml: $"usedMacros = [\"{t.Outside("nowhere")}\"]\n" + Stages);
        Assert.Equal(2, code);
        Assert.Contains("not found", e);
    }
}
