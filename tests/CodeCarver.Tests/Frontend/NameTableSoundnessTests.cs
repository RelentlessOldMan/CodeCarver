using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Review step 4: a name used differently by different targets (N1) and registration through a macro
/// (R1) must not lose code. Each case carves through the CLI and checks the file is emitted and verify passes.</summary>
public sealed class NameTableSoundnessTests
{
    static (int Code, string Out, Func<string, bool> Emitted, Action Cleanup) Carve(Dictionary<string, string> files)
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-nt-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        foreach (var (rel, text) in files)
        {
            var p = Path.Combine(src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        var cfg = Path.Combine(work, "carve.toml");
        File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n"
            + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n");
        var so = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, new StringWriter());
        return (code, so.ToString(), rel => File.Exists(Path.Combine(work, "out", "carved", rel)),
                () => { try { Directory.Delete(work, true); } catch { } });
    }

    [Fact]
    public void FunctionLikeMacroInOtherTarget_DoesNotHideRealFunction_N1()
    {
        var (code, o, emitted, clean) = Carve(new()
        {
            ["a/uart.h"] = "#define uart_write(x) hal_uart_write(x)\n",
            ["b/main.c"] = "void uart_write(int x);\nint main(void){ uart_write(1); return 0; }\n",
            ["b/uart.c"] = "void uart_write(int x){ (void)x; }\n",
        });
        try { Assert.Equal(0, code); Assert.True(emitted("b/uart.c"), o); }
        finally { clean(); }
    }

    [Fact]
    public void MacroAndGlobalWithOneName_BothFollowed()
    {
        var (code, o, emitted, clean) = Carve(new()
        {
            ["a/cfg.h"] = "#define table other_table\n",
            ["b/main.c"] = "extern int table[];\nint main(void){ return table[0]; }\n",
            ["b/table.c"] = "int table[1] = {0};\n",
        });
        try { Assert.Equal(0, code); Assert.True(emitted("b/table.c"), o); }
        finally { clean(); }
    }

    [Fact]
    public void ValuelessMacroAndFunctionWithOneName_CallSurvives()
    {
        var (code, o, emitted, clean) = Carve(new()
        {
            ["a/compat.h"] = "#define flush_cache\n",
            ["b/main.c"] = "void flush_cache(void);\nint main(void){ flush_cache(); return 0; }\n",
            ["b/cache.c"] = "void flush_cache(void){}\n",
        });
        try { Assert.Equal(0, code); Assert.True(emitted("b/cache.c"), o); }
        finally { clean(); }
    }

    [Fact]
    public void MacroWrappedSectionRegistration_IsRooted_R1()
    {
        var (code, o, emitted, clean) = Carve(new()
        {
            ["init.h"] = "#define INITCALL(fn) static void (*__init_##fn)(void) \\\n    __attribute__((section(\".initcalls\"), used)) = fn\n",
            ["driver.c"] = "#include \"init.h\"\nstatic void drv_init(void){}\nINITCALL(drv_init);\n",
            ["main.c"] = "int main(void){ return 0; }\n",
            ["link.ld"] = "SECTIONS { .initcalls : { KEEP(*(.initcalls)) } }\n",
        });
        try { Assert.Equal(0, code); Assert.True(emitted("driver.c"), o); Assert.True(emitted("init.h"), o); }
        finally { clean(); }
    }

    [Fact]
    public void TwoLevelKeepMacro_IsRooted()
    {
        var (code, o, emitted, clean) = Carve(new()
        {
            ["init.h"] = "#define KEEP_IN(s) __attribute__((section(s), used))\n#define DRIVER(fn) static void (*p_##fn)(void) KEEP_IN(\".drv\") = fn\n",
            ["driver.c"] = "#include \"init.h\"\nstatic void probe(void){}\nDRIVER(probe);\n",
            ["main.c"] = "int main(void){ return 0; }\n",
        });
        try { Assert.Equal(0, code); Assert.True(emitted("driver.c"), o); }
        finally { clean(); }
    }

    [Fact]
    public void Cxx11GnuUsedAttribute_IsRooted()
    {
        var (code, o, emitted, clean) = Carve(new()
        {
            ["hook.c"] = "[[gnu::used]] static void hook(void){}\n",
            ["main.c"] = "int main(void){ return 0; }\n",
        });
        try { Assert.Equal(0, code); Assert.True(emitted("hook.c"), o); }
        finally { clean(); }
    }
}
