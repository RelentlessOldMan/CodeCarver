using Xunit;

namespace CodeCarver.Tests.Cli;

public class HeaderEmissionTests
{
    // Review B7: an unused static function in a header is still compiled at -O0, so what it calls must link.
    [Fact]
    public void UnusedStaticFunctionInAHeader_KeepsWhatItCalls()
    {
        using var t = new TreeCarve()
            .W("hdr.h", "int dep_b(void);\nstatic int hdr_static(void) { return dep_b(); }\n")
            .W("dep_b.c", "int dep_b(void) { return 2; }\n")
            .W("main.c", "#include \"hdr.h\"\nint main(void) { return 0; }\n");
        t.Compile("main.c", "-O0").Compile("dep_b.c", "-O0");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("dep_b.c"), r.Out + r.Err);
    }

    // Static inline is not emitted unless used: nothing it calls is needed.
    [Fact]
    public void UnusedStaticInlineFunctionInAHeader_KeepsNothing()
    {
        using var t = new TreeCarve()
            .W("hdr.h", "int dep_b(void);\nstatic inline int hdr_inline(void) { return dep_b(); }\n")
            .W("dep_b.c", "int dep_b(void) { return 2; }\n")
            .W("main.c", "#include \"hdr.h\"\nint main(void) { return 0; }\n");
        t.Compile("main.c").Compile("dep_b.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.False(t.Kept("dep_b.c"), r.Out + r.Err);
    }
}
