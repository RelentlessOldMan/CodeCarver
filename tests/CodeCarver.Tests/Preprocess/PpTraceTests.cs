using CodeCarver.Core.Frontend;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>LLVM's pp-trace run on a build log's commands (docs/USED-MACROS.md). The runs need pp-trace on this machine
/// (LLVM's bin folder, or PATH) and skip without it.</summary>
public sealed class PpTraceTests
{
    /// <summary>pp-trace here, or null.</summary>
    internal static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "pp-trace.exe" } : new[] { "pp-trace" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(OperatingSystem.IsWindows() ? @"C:\Program Files\LLVM\bin" : "/usr/bin");
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d, n); if (File.Exists(p)) return p; } catch (ArgumentException) { }
            }
        return null;
    }

    static CompileCommand Cmd(string dir, string file, string? driver, params string[] args) =>
        new() { Directory = dir, File = file, Driver = driver, Arguments = args.Append(file).ToList() };

    static HashSet<ulong> Keys(params string[] names) => names.Select(n => NameKey.Short(n)).ToHashSet();

    [Fact]
    public void ReadNames_TakesEveryMacroNameTokLine()
    {
        var yaml = "---\n- Callback: MacroExpands\n  MacroNameTok: KQ_CAT\n  MacroDirective: MD_Define\n"
                 + "- Callback: Ifdef\n  Loc: \"x.c:1:8\"\n  MacroNameTok: KQ_HAS_DMA\n  MacroDefinition: []\n...\n";
        var names = new HashSet<ulong>();
        PpTrace.ReadNames(new StringReader(yaml), names);
        Assert.Equal(Keys("KQ_CAT", "KQ_HAS_DMA"), names);
    }

    [Theory]
    [InlineData("aarch64-none-elf-clang", "aarch64-none-elf")]
    [InlineData("/opt/llvm/bin/arm-none-eabi-gcc", "arm-none-eabi")]
    [InlineData(@"C:\tools\x86_64-linux-gnu-clang++-17.exe", "x86_64-linux-gnu")]
    [InlineData("clang", null)]
    [InlineData("clang-17", null)]
    [InlineData("gcc", null)]
    public void TargetOf_ReadsTheTripleBeforeTheDriverName(string driver, string? target) =>
        Assert.Equal(target, PpTrace.TargetOf(driver));

    [Fact]
    public void CompileArguments_DropOutputsAndAddTheNamedTarget()
    {
        var c = Cmd("/w", "src/a.c", "/opt/bin/aarch64-none-elf-clang", "-O2", "-Iinc", "-MMD", "-MF", "a.d", "-c", "-o", "a.o");
        Assert.Equal(new[] { "aarch64-none-elf-clang", "--target=aarch64-none-elf", "-O2", "-Iinc", "src/a.c" }, PpTrace.CompileArguments(c));
        var given = Cmd("/w", "a.c", "aarch64-none-elf-clang", "--target=aarch64-none-elf", "-c");
        Assert.Equal(new[] { "aarch64-none-elf-clang", "--target=aarch64-none-elf", "a.c" }, PpTrace.CompileArguments(given));
        Assert.Contains("-ferror-limit=0", PpTrace.CompileArguments(Cmd("/w", "start.S", "clang", "-c")));
    }

    [Theory]
    [InlineData("a.c", true)]
    [InlineData("a.S", true)]
    [InlineData("a.sx", true)]
    [InlineData("a.s", false)]
    [InlineData("a.asm", false)]
    public void PlainAssembly_IsNotPreprocessed(string file, bool preprocessed) =>
        Assert.Equal(preprocessed, PpTrace.Preprocessed(file));

    [SkippableFact]
    public void Run_GivesTheNamesTheLiveCodeUsed_AndCountsWhatDidNotRun()
    {
        var exe = Find();
        Skip.If(exe is null, "pp-trace (LLVM) not installed");
        var dir = Path.Combine(Path.GetTempPath(), "cc-ppt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "inc"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "inc", "k.h"), "#define KQ_CAT(a, b) a##b\n#define KQ_A_OFF 4\n#define KQ_B_OFF 8\n#define KQ_UNUSED 1\n");
            File.WriteAllText(Path.Combine(dir, "a.c"), "#include \"k.h\"\n#ifdef KQ_ON\nint x = KQ_CAT(KQ_A, _OFF);\n#else\nint y = KQ_B_OFF;\n#endif\n");
            File.WriteAllText(Path.Combine(dir, "start.S"), "#include \"k.h\"\n mov x0, #KQ_B_OFF\n");
            File.WriteAllText(Path.Combine(dir, "raw.s"), " nop\n");
            var cmds = new List<CompileCommand>
            {
                Cmd(dir, "a.c", "clang", "-Iinc", "-DKQ_ON", "-c", "-o", "a.o"),
                Cmd(dir, "start.S", "aarch64-none-elf-clang", "-Iinc", "-c"),
                Cmd(dir, "raw.s", "clang", "-c"),
            };
            var pt = PpTrace.Run(exe!, cmds, parallel: 2);
            Assert.Matches(@"^LLVM version \d", pt.Version);
            Assert.Equal(2, pt.Commands);
            Assert.Equal(1, pt.NotPreprocessed);
            Assert.True(pt.Complete, $"{pt.Succeeded} of {pt.Commands}");
            Assert.Superset(Keys("KQ_ON", "KQ_CAT", "KQ_A_OFF", "KQ_B_OFF"), pt.Names);
            Assert.DoesNotContain(NameKey.Short("KQ_UNUSED"), pt.Names);

            // A missing include is a failed command: the names it would have used are not all there.
            File.WriteAllText(Path.Combine(dir, "b.c"), "#include \"gone.h\"\nint z;\n");
            var bad = PpTrace.Run(exe!, new[] { Cmd(dir, "b.c", "clang", "-c"), Cmd(dir, "c.c", "clang", "-Inowhere", "-c") }, parallel: 2);
            Assert.False(bad.Complete);
            Assert.Equal(0, bad.Succeeded);
            Assert.Equal(1, bad.MissingPaths);

            // The build machine's toolchain directories aren't here: Clang skips a missing include directory, so a
            // command still runs. One inside the carve root is a pathMap the log needs.
            var outside = Path.Combine(Path.GetTempPath(), "cc-ppt-gone-" + Guid.NewGuid().ToString("N"), "sysroot", "include");
            var paths = PpTrace.Run(exe!, new[]
            {
                Cmd(dir, "a.c", "clang", "-Iinc", "-isystem", outside, "-c"),
                Cmd(dir, "a.c", "clang", "-Iinc", "-Ignone", "-c"),
                Cmd(dir, "a.c", "clang", "-include", "gone.h", "-c"),
                Cmd(Path.Combine(dir, "obj"), "../a.c", "clang", "-c"),
                Cmd(dir, "nosuch.c", "clang", "-c"),
            }, parallel: 2, root: dir);
            Assert.Equal(1, paths.Succeeded);
            Assert.Equal(1, paths.RanWithoutOutsideDirs);
            Assert.Equal(4, paths.MissingPaths);
            Assert.Equal((1, 1, 1, 1), (paths.MissingIncludeDirInRoot, paths.MissingForcedInclude, paths.MissingWorkDir, paths.MissingSource));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Each pp-trace holds about 0.5 KB per define its compile includes, outside the carve's memoryStopMb. The runs
    /// share a budget: several run at once when they fit, and when they don't, all still finish within it.</summary>
    [SkippableFact]
    public void Runs_StayWithinTheirMemoryBudget_AndStillAllFinish()
    {
        var exe = Find();
        Skip.If(exe is null, "pp-trace (LLVM) not installed");
        var dir = Path.Combine(Path.GetTempPath(), "cc-ppt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 150_000; i++) sb.Append("#define KQ_R").Append(i).Append("_ADDR(n) (0x40000000 + ((n) * 0x").Append(i.ToString("x")).Append("))\n");
            File.WriteAllText(Path.Combine(dir, "big.h"), sb.ToString());
            var cmds = Enumerable.Range(0, 6).Select(i =>
            {
                File.WriteAllText(Path.Combine(dir, $"u{i}.c"), $"#include \"big.h\"\nint x = KQ_R{i}_ADDR(1);\n");
                return Cmd(dir, $"u{i}.c", "clang", "-c");
            }).ToList();
            // A small compile first finishes before its memory is ever sampled: it must not make the big ones look small.
            File.WriteAllText(Path.Combine(dir, "main.c"), "int main(void) { return 0; }\n");
            cmds.Insert(0, Cmd(dir, "main.c", "clang", "-c"));

            var roomy = PpTrace.Run(exe!, cmds, parallel: 3, memoryBudget: 4L << 30);
            Assert.True(roomy.Complete, $"{roomy.Succeeded} of {roomy.Commands}");
            Assert.True(roomy.PeakChildBytes > 20L << 20, $"one run held {roomy.PeakChildBytes >> 20} MB");
            Assert.True(roomy.PeakRunning > 1, "runs that fit run at once");
            Assert.Equal(0, roomy.Evictions);

            // Room for about one and a half: the runs hold no more than the budget and what two grow by between two
            // samples on a slow machine (under one run; running all three at once would hold two budgets), and every
            // command still finishes.
            var budget = roomy.PeakChildBytes * 3 / 2;
            var tight = PpTrace.Run(exe!, cmds, parallel: 3, memoryBudget: budget);
            Assert.True(tight.Complete, $"{tight.Succeeded} of {tight.Commands}");
            Assert.True(tight.PeakTotalBytes <= budget + roomy.PeakChildBytes, $"{tight.PeakTotalBytes >> 20} MB held of {budget >> 20}");
            Assert.Equal(roomy.Names, tight.Names);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
