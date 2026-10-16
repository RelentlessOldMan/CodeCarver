using Xunit;

namespace CodeCarver.Tests.Cli;

public class RenamedDefinitionTests
{
    // Review B9: the definition is spelled through a macro; the caller uses the real symbol, without the header.
    [Theory]
    [InlineData("#define spi_xfer spi1_xfer\n", "int spi_xfer(void) { return 7; }\n", "spi1_xfer")]
    [InlineData("#define DRV_NAME(n) drv_##n\n", "int DRV_NAME(uart)(void) { return 7; }\n", "drv_uart")]
    [InlineData("#define CAT(a, b) a##b\n#define NAMED(n) CAT(port_, n)\n", "int NAMED(spi)(void) { return 7; }\n", "port_spi")]
    public void DefinitionSpelledThroughAMacro_IsFoundByItsRealName(string define, string definition, string real)
    {
        using var t = new TreeCarve()
            .W("drv.h", define)
            .W("drv.c", "#include \"drv.h\"\n" + definition)
            .W("main.c", "int " + real + "(void);\nint main(void) { return " + real + "(); }\n");
        t.Compile("drv.c").Compile("main.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("drv.c"), r.Out + r.Err);
    }
}
