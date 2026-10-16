using Xunit;

namespace CodeCarver.Tests.Cli;

public class AssemblySymbolTests
{
    // Review S1: the vector table sits in a file the startup code includes; its handler was dropped.
    [Theory]
    [InlineData("#include \"handlers.inc\"\n")]
    [InlineData("    .include \"tables/handlers.inc\"\n")]
    public void HandlerNamedInAFileStartupIncludes_IsKept(string include)
    {
        using var t = new TreeCarve()
            .W("main.c", "extern void *vec_table[];\nint main(void){ return vec_table[0] != 0; }\n")
            .W("startup.S", "    .data\n    .globl vec_table\nvec_table:\n" + include)
            .W(include.Contains("tables/") ? "tables/handlers.inc" : "handlers.inc", "    .quad uart_isr\n")
            .W("uart.c", "int uart_isr(void){ return 99; }\n");
        t.Compile("main.c").Compile("uart.c").Compile("startup.S");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("uart.c"), r.Out + r.Err);
    }

    // Review S1 (D10): a header only assembly includes is still needed to assemble it.
    [Fact]
    public void HeaderOnlyAssemblyIncludes_IsKept()
    {
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("startup.S", "#include \"regs.h\"\n    .text\n    .globl reset\nreset:\n    ret\n")
            .W("regs.h", "#define STACK_TOP 0x20000\n")
            .W("other.h", "int unused_decl(void);\n");
        t.Compile("main.c").Compile("startup.S");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("startup.S"), r.Out + r.Err);
        Assert.True(t.Kept("regs.h"), r.Out + r.Err);
    }

    // Review S2: top-level asm in C defining a symbol, in one literal with \n escapes or one literal a line.
    [Theory]
    [InlineData("__asm__(\".text\\n.globl asm_defined\\nasm_defined:\\n\\tret\\n\");\n")]
    [InlineData("__asm__(\n  \".text\\n\"\n  \".globl asm_defined\\n\"\n  \"asm_defined:\\n\"\n  \"\\tret\\n\");\n")]
    public void SymbolDefinedByTopLevelAsm_KeepsItsFile(string asm)
    {
        using var t = new TreeCarve()
            .W("main.c", "int asm_defined(void);\nint main(void){ return asm_defined(); }\n")
            .W("host.c", asm + "int host_other(void){ return 1; }\n");
        t.Compile("main.c").Compile("host.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("host.c"), r.Out + r.Err);
    }
}
