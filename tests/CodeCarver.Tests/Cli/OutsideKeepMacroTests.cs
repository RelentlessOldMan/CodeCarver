using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Review M1: a keep or registration macro the tree uses but defines outside it, on the command line or in an SDK
/// header under -I. What it marks is kept by the linker although nothing calls it.
/// </summary>
public sealed class OutsideKeepMacroTests
{
    [Fact]
    public void KeepMacro_FromTheCommandLine()
    {
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("hook.c", "AUTO_INIT void boot_hook(void){}\n");
        const string flag = "'-DAUTO_INIT=__attribute__((constructor))'";
        t.Compile("main.c", flag).Compile("hook.c", flag);
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("hook.c"), r.Out + r.Err);
    }

    [Fact]
    public void RegistrationMacro_FromAHeaderOutsideTheRoot()
    {
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("drv.c", "#include \"sys_init.h\"\nstatic void drv_init(void){}\nSYS_INIT(drv_init);\n");
        t.WOut("sdk/inc/sys_init.h",
            "#define SYS_INIT(fn) static void (*const init_##fn)(void) __attribute__((section(\".initcalls\"), used)) = fn\n");
        var inc = "-I" + t.Outside("sdk/inc");
        t.Compile("main.c", inc).Compile("drv.c", inc);
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("drv.c"), r.Out + r.Err);
    }
}
