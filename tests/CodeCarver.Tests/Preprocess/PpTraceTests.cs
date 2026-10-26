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
        // ppTraceArgs go after the command's own flags, so an added -isystem is searched after the build's directories.
        Assert.Equal(new[] { "clang", "-Iinc", "-isystem", "/kq/libc", "a.c" },
                     PpTrace.CompileArguments(Cmd("/w", "a.c", "clang", "-Iinc", "-c"), new[] { "-isystem", "/kq/libc" }));
    }

    [Fact]
    public void Drop_TakesOutWhatThePatternsMatch_AndCountsEach()
    {
        var patterns = new[] { "-fkq-*", "-Xkq *", "-mkq=fast", "-fnever-*" }.Select(PpTrace.DropPattern.Parse).ToList();
        var used = new int[patterns.Count];
        var (kept, n) = PpTrace.Drop(new[] { "-O2", "-fkq-a", "-Xkq", "v", "-fkq-b=1", "-mkq=fast", "-mkq=slow", "-Xkq" }, patterns, used);
        // A two-word pattern needs its value: the last -Xkq has none and stays.
        Assert.Equal(new[] { "-O2", "-mkq=slow", "-Xkq" }, kept);
        Assert.Equal(5, n);
        Assert.Equal(new[] { 2, 1, 1, 0 }, used);
    }

    [Theory]
    [InlineData("a.c", true)]
    [InlineData("a.S", true)]
    [InlineData("a.sx", true)]
    [InlineData("a.s", false)]
    [InlineData("a.asm", false)]
    public void PlainAssembly_IsNotPreprocessed(string file, bool preprocessed) =>
        Assert.Equal(preprocessed, PpTrace.Preprocessed(file));

    [Theory]
    [InlineData("PLEASE submit a bug report to the LLVM project\nStack dump:", PpTrace.Failure.Crash, null)]
    [InlineData("a.c:1:10: fatal error: 'kq/gen.h' file not found", PpTrace.Failure.HeaderNotFound, "kq/gen.h")]
    // A rejected option alone doesn't fail a run: it counts only when no other error came with it.
    [InlineData("error: unknown argument: '-mkq-fast'\na.c:3:1: error: unknown type name 'kq_t'\n    3 | kq_t v;", PpTrace.Failure.Error, null)]
    [InlineData("error: unsupported option '-mkq' for target 'aarch64-none-elf'", PpTrace.Failure.Option, null)]
    [InlineData("a.c:3:1: error: unknown type name 'kq_t'", PpTrace.Failure.Error, null)]
    [InlineData("error: unknown argument: '-fkq-x'\nh.c:2:2: error: \"kq needs the vendor compiler\"\n    2 | #error \"kq needs the vendor compiler\"", PpTrace.Failure.HashError, null)]
    [InlineData("h.c:2:2: error: \"old style\"\n#  error \"old style\"", PpTrace.Failure.HashError, null)]
    [InlineData("a.c:9:1: error: \"x\"\n    9 | int kq_error_count;", PpTrace.Failure.Error, null)]
    [InlineData("a.c:19:1: error: unknown type name 'kq_t'\nfatal error: too many errors emitted, stopping now [-ferror-limit=]", PpTrace.Failure.ErrorLimit, null)]
    [InlineData("", PpTrace.Failure.NoMessage, null)]
    public void Classify_SaysWhyARunFailed(string lines, PpTrace.Failure why, string? header) =>
        Assert.Equal((why, header), PpTrace.Classify(lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)));

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

            // Clang skips a missing include directory, as the build did: the build machine's toolchain outside the
            // carve root, and inside it one the tree never had or one the build made for its output (out/gen, next to
            // the out/obj its -o writes to). A command still runs; a header only such a directory held fails it.
            var outside = Path.Combine(Path.GetTempPath(), "cc-ppt-gone-" + Guid.NewGuid().ToString("N"), "sysroot", "include");
            File.WriteAllText(Path.Combine(dir, "g.c"), "#include \"gen.h\"\nint g;\n");
            var paths = PpTrace.Run(exe!, new[]
            {
                Cmd(dir, "a.c", "clang", "-Iinc", "-isystem", outside, "-c"),
                Cmd(dir, "a.c", "clang", "-Iinc", "-Ignone", "-Iout/gen", "-c", "-o", "out/a.o"),
                Cmd(dir, "g.c", "clang", "-Iout/gen", "-c", "-o", "out/g.o"),
                Cmd(dir, "a.c", "clang", "-include", "gone.h", "-c"),
                Cmd(Path.Combine(dir, "obj"), "../a.c", "clang", "-c"),
                Cmd(dir, "nosuch.c", "clang", "-c"),
            }, parallel: 2, root: dir);
            Assert.Equal(2, paths.Succeeded);
            Assert.Equal(1, paths.RanWithoutOutsideDirs);
            Assert.Equal(2, paths.RanWithoutDirsInRoot);
            Assert.Equal((2, 1), (paths.MissingDirsInRoot, paths.MissingDirsInRootFromBuild));
            Assert.Equal(3, paths.MissingPaths);
            Assert.Equal((1, 1, 1), (paths.MissingForcedInclude, paths.MissingWorkDir, paths.MissingSource));
            Assert.Equal(6, paths.Commands);   // g.c ran and failed: gen.h was only in out/gen
            Assert.Equal((1, 0, 1), (paths.FailedHeaderNotFound, paths.FailedHeaderNotFoundCLibrary, paths.HeadersNotFound));

            // A bare-metal target with no C library here: the build's came from its toolchain's folders.
            File.WriteAllText(Path.Combine(dir, "lib.c"), "#include <string.h>\nint l;\n");
            var lib = PpTrace.Run(exe!, new[] { Cmd(dir, "lib.c", "aarch64-none-elf-clang", "-c"), Cmd(dir, "g.c", "clang", "-c") }, parallel: 2, root: dir);
            Assert.Equal((2, 1, 2), (lib.FailedHeaderNotFound, lib.FailedHeaderNotFoundCLibrary, lib.HeadersNotFound));

            // ppTraceArgs point it at a C library here. An option the driver doesn't know is an error pp-trace prints and
            // goes on from: the run still exits 0 (optionErrors). ppTraceDropOptions takes it out.
            Directory.CreateDirectory(Path.Combine(dir, "libc"));
            File.WriteAllText(Path.Combine(dir, "libc", "string.h"), "#define KQ_STRING_H 1\n");
            var vendor = new[] { Cmd(dir, "lib.c", "aarch64-none-elf-clang", "-fkq-vendor", "-c"), Cmd(dir, "a.c", "clang", "-Iinc", "-fkq-vendor", "-c") };
            var withLib = PpTrace.Run(exe!, vendor, parallel: 2, root: dir, extraArgs: new[] { "-isystem", Path.Combine(dir, "libc") });
            Assert.True(withLib.Complete, $"{withLib.Succeeded} of {withLib.Commands}");
            Assert.Equal(2, withLib.OptionErrors);
            var dropped = PpTrace.Run(exe!, vendor, parallel: 2, root: dir, extraArgs: new[] { "-isystem", Path.Combine(dir, "libc") },
                                      dropOptions: new[] { "-fkq-*", "-Xnever *" });
            Assert.True(dropped.Complete, $"{dropped.Succeeded} of {dropped.Commands}");
            Assert.Equal((0, 2, 2, 1), (dropped.OptionErrors, dropped.CommandsWithDropped, dropped.OptionsDropped, dropped.DropPatternsUnused));

            // With the rejected option on every command, what failed them is still told apart: an #error that fired,
            // Clang's error limit, an error the parse after preprocessing reports.
            File.WriteAllText(Path.Combine(dir, "h.c"), "#ifndef KQ_VENDOR\n#error \"kq needs the vendor compiler\"\n#endif\nint h;\n");
            File.WriteAllText(Path.Combine(dir, "lim.c"), string.Concat(Enumerable.Range(0, 25).Select(i => $"kq_t v{i};\n")));
            File.WriteAllText(Path.Combine(dir, "sem.c"), "int f(void) { return __builtin_kq_magic(1); }\n");
            var errs = PpTrace.Run(exe!, new[] { "h.c", "lim.c", "sem.c" }.Select(f => Cmd(dir, f, "clang", "-fkq-vendor", "-c")).ToList(), parallel: 2, root: dir);
            Assert.Equal((3, 0), (errs.FailedError, errs.FailedOption));
            Assert.Equal((1, 1, 1), (errs.FailedHashError, errs.FailedErrorLimit, errs.FailedOtherError));
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
