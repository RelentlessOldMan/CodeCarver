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
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
