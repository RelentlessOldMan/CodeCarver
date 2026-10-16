using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Review T1: keeps and interrupt handlers of toolchains other than GNU. Nothing calls the handler; the
/// toolchain keeps it or places it in the vector table.</summary>
public class ToolchainKeepTests
{
    [Theory]
    [InlineData("__root void kept_fn(void) { helper(); }\n")]                                           // IAR
    [InlineData("__root const int kept_tbl[] = { 1, 2 };\nvoid unused_fn(void) { helper(); }\n")]   // IAR data: its unit
    [InlineData("#pragma RETAIN(kept_fn)\nvoid kept_fn(void) { helper(); }\n")]                         // TI
    [InlineData("#pragma RETAIN\nvoid kept_fn(void) { helper(); }\n")]                                  // TI, next declaration
    [InlineData("#pragma required=kept_fn\nvoid kept_fn(void) { helper(); }\n")]                       // IAR
    [InlineData("#pragma comment(linker, \"/include:kept_fn\")\nvoid kept_fn(void) { helper(); }\n")]   // MSVC
    [InlineData("ISR(TIMER0_OVF_vect)\n{\n    helper();\n}\n")]                                          // AVR
    [InlineData("void __attribute__((interrupt(TIMER0_A0_VECTOR))) kept_isr(void) { helper(); }\n")]    // msp430 gcc
    [InlineData("#pragma vector=TIMER0_A0_VECTOR\n__interrupt void kept_isr(void) { helper(); }\n")]    // IAR/TI msp430
    [InlineData("void __ISR(_TIMER_1_VECTOR, IPL2SOFT) kept_isr(void) { helper(); }\n")]                // XC32
    [InlineData("void kept_isr(void) interrupt 1 { helper(); }\n")]                                     // Keil C51
    public void HandlerTheToolchainKeeps_IsKeptWithWhatItCalls(string handler)
    {
        var data = handler.Contains("kept_tbl");
        using var t = new TreeCarve()
            .W("main.c", "int main(void) { return 0; }\n")
            .W("isr.c", "void helper(void);\n" + handler + (data ? "" : "void unused_other(void) { }\n"))
            .W("helper.c", "void helper(void) { }\n");
        t.Compile("main.c").Compile("isr.c").Compile("helper.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("isr.c"), r.Out + r.Err);
        if (!data) Assert.True(t.Kept("helper.c"), r.Out + r.Err);
    }
}
