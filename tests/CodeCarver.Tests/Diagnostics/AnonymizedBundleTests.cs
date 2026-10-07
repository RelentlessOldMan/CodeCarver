using CodeCarver.Cli;
using CodeCarver.Core.Diagnostics;
using CodeCarver.Core.Frontend;
using CodeCarver.Tests.Cli;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

/// <summary>
/// The anonymized bundle must carve exactly like the original: same files kept, same verify verdict. Each tree here
/// leans on a different thing the anonymization could break (pasted names, macro-named includes, -D flags, strings
/// that name functions, case-sensitive macro heuristics, numbers in #if).
/// </summary>
public sealed class AnonymizedBundleTests
{
    static List<string> Files(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .Where(r => !File.ReadAllText(Path.Combine(dir, r)).Contains("Placeholder written by CodeCarver"))
                .OrderBy(r => r, StringComparer.Ordinal).ToList()
            : new List<string>();

    /// <summary>Carves <paramref name="t"/>, then its anonymized bundle, and compares.</summary>
    static void SameCarve(TreeCarve t, bool trace = false, string entry = "main")
    {
        if (trace) t.TraceCompiled();
        var (code, so, se) = t.Carve(entry);
        Assert.True(code is 0 or 3, so + se);
        var kept = Files(Path.Combine(t.Root, "out", "carved"));
        Assert.NotEmpty(kept);

        var logText = File.Exists(Path.Combine(t.Root, "build.log")) ? File.ReadAllText(Path.Combine(t.Root, "build.log")) : "";
        var cmds = logText.Length == 0 ? new List<CompileCommand>() : BuildLogScraper.Parse(logText).ToList();
        var input = new AnonymizedBundle.Input
        {
            Root = t.Src,
            Files = Directory.EnumerateFiles(t.Src, "*", SearchOption.AllDirectories).ToList(),
            Commands = cmds,
            TraceOpened = t.TracePaths?.Select(Path.GetFullPath).ToList(),
            EntryPoints = new[] { entry },
        };
        var a = new Anonymizer();
        AnonymizedBundle.Learn(input, a, File.ReadAllText);
        var bundleDir = Path.Combine(t.Root, "bundle");
        var r = AnonymizedBundle.Write(input, bundleDir, a, File.ReadAllText);
        Assert.Equal(0, r.FilesWithheld);

        // Nothing of the original anywhere in the bundle.
        foreach (var f in Directory.EnumerateFiles(bundleDir, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            // carve.toml: its keys are CodeCarver's own words; only the values come from the original.
            if (f.EndsWith(".toml")) text = string.Join(" ", System.Text.RegularExpressions.Regex.Matches(text, "\"[^\"]*\"").Select(m => m.Value));
            Assert.Empty(a.Leaks(text + " " + f[bundleDir.Length..]));
        }

        var so2 = new StringWriter(); var se2 = new StringWriter();
        var code2 = CarveCommand.Run(new[] { "carve", r.SourceRoot, "--config", r.ConfigPath }, so2, se2);
        Assert.True(code == code2, $"original exit {code}, bundle exit {code2}\n{so2}\n{se2}\n--- original\n{so}");
        var keptAnon = Files(Path.Combine(bundleDir, "out", r.StageName, "carved"));
        var expected = kept.Select(k => a.Path(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(expected.SequenceEqual(keptAnon),
            $"kept differs\nexpected: {string.Join(", ", expected)}\nactual:   {string.Join(", ", keptAnon)}\n{so2}\n{se2}");
    }

    [Fact]
    public void PlainCallsAcrossHeaders()
    {
        using var t = new TreeCarve();
        t.W("inc/uart_hal.h", "#ifndef UART_HAL_H\n#define UART_HAL_H\nint uart_send(int b);\n#endif\n");
        t.W("main.c", "#include \"inc/uart_hal.h\"\n/* the board bring-up */\nint main(void) { return uart_send(0x41); }\n");
        t.W("drv/uart.c", "#include \"../inc/uart_hal.h\"\nint uart_send(int b) { return b; }\n");
        t.W("drv/spi.c", "int spi_send(int b) { return b; }\n");
        t.Compile("main.c").Compile("drv/uart.c").Compile("drv/spi.c");
        SameCarve(t);
    }

    [Fact]
    public void PastedNames_DefineFlags_AndIfNumbers()
    {
        using var t = new TreeCarve();
        t.W("reg.h", "#define CAT(a,b) a##b\n#define INIT(mod) CAT(mod, _init)\n");
        t.W("main.c", "#include \"reg.h\"\nint uart_init(void); int spi_init(void);\n"
            + "int main(void) {\n#if BOARD_REV >= 300\n return INIT(uart)();\n#else\n return INIT(spi)();\n#endif\n}\n");
        t.W("uart.c", "int uart_init(void) { return 1; }\n");
        t.W("spi.c", "int spi_init(void) { return 2; }\n");
        t.Compile("main.c", "-DBOARD_REV=310").Compile("uart.c").Compile("spi.c");
        SameCarve(t, trace: true);
    }

    [Fact]
    public void StringNamedFunction_AndMacroNamedInclude()
    {
        using var t = new TreeCarve();
        t.W("cfg/board_cfg.h", "#define PLUGIN_COUNT 2\n");
        t.W("main.c", "#define CFG_HEADER \"cfg/board_cfg.h\"\n#include CFG_HEADER\n#include <dlfcn.h>\n"
            + "int main(void) { void *f = dlsym(RTLD_DEFAULT, \"plugin_entry\"); return f != 0 && PLUGIN_COUNT; }\n");
        t.W("plugin.c", "int plugin_entry(void) { return 0; }\n");
        t.W("other.c", "int other_entry(void) { return 0; }\n");
        t.Compile("main.c").Compile("plugin.c").Compile("other.c");
        SameCarve(t);
    }

    [Fact]
    public void AllCapsHeads_KnR_AndWeak()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int CHECK(int); int add(); void hook(void) __attribute__((weak));\n"
            + "int main(void) { if (hook) hook(); return CHECK(1) + add(1, 2); }\n");
        t.W("caps.c", "CHECK(int x) { return x; }\n");
        t.W("add.c", "int add(a, b)\n    int a;\n    int b;\n{\n    return a + b;\n}\n");
        t.W("hook.c", "void hook(void) { }\n");
        t.Compile("main.c").Compile("caps.c").Compile("add.c");
        SameCarve(t, trace: true);
    }

    [Fact]
    public void VerifyFailure_ReproducesInTheBundle()
    {
        using var t = new TreeCarve();
        // A definition shape the parser can't read; whatever verify says about it, the bundle must say too.
        t.W("main.c", "int odd_fn(void);\nint main(void) { return odd_fn(); }\n");
        t.W("odd.c", "#define HEAD(n) int n(void)\nHEAD(odd_fn) { return 0; }\n");
        t.Compile("main.c").Compile("odd.c");
        SameCarve(t);
    }
}
