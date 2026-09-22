using System.Linq;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>Implicit-root discovery: symbols referenced only from a standalone .s startup (vector table
/// `.word Handler`, `bl func`) must be rooted, or the carve drops the handlers and the image breaks.</summary>
public class RootProviderTests
{
    [Fact]
    public void AsmReferenceRootProvider_RootsSymbolsNamedInAsm_IgnoresUnmatchedTokens()
    {
        var b = new GraphBuilder();
        var nmi = b.Func("NMI_Handler", "app.c");
        var sys = b.Func("SysTick_Handler", "app.c");
        var dead = b.Func("never_used", "app.c");

        const string startup = """
                .section .isr_vector
                .word Reset_Handler
                .word NMI_Handler
            Reset_Handler:
                bl SysTick_Handler
                mov r0, r1
            """;
        var rooted = new AsmReferenceRootProvider(new[] { startup }).Discover(b.Graph)
            .Select(r => r.Node).ToHashSet();

        Assert.Contains(nmi, rooted);   // `.word NMI_Handler`
        Assert.Contains(sys, rooted);   // `bl SysTick_Handler`
        Assert.DoesNotContain(dead, rooted); // not named in asm
        // `mov`, `r0`, `r1`, `.section` etc. match no symbol, so they contribute no roots.
    }
}
