using System.Linq;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;
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

    [Fact]
    public void LinkerSectionRootProvider_RootsSymbolsInKeptSections_WildcardAndExact()
    {
        var b = new GraphBuilder();
        var early = b.Func("early_init", "init.c");   // leading attribute, wildcard section
        var cmd = b.Global("cmd_reboot", "cmd.c");    // trailing attribute, exact section
        var normal = b.Func("helper", "cmd.c");       // no section attribute
        var otherSec = b.Func("in_ram_fn", "cmd.c");  // in a section the linker does NOT KEEP

        var sources = new[]
        {
            "__attribute__((section(\".initcall1.init\"))) void early_init(void) { }",
            "const cmd_t cmd_reboot __attribute__((used, section(\".commands\"))) = { };",
            "__attribute__((section(\".data.ram\"))) int in_ram_fn(void) { return 0; }",
        };
        const string linker = """
            SECTIONS {
              .init : { KEEP(*(SORT(.initcall*.init))) }
              .cmd  : { KEEP(*(.commands)) }
              .ram  : { *(.data.ram) }   /* referenced but NOT KEEP()'d */
            }
            """;

        var rooted = new LinkerSectionRootProvider(sources, new[] { linker }).Discover(b.Graph)
            .Select(r => r.Node).ToHashSet();

        Assert.Contains(early, rooted);       // .initcall1.init matches KEEP(*(SORT(.initcall*.init)))
        Assert.Contains(cmd, rooted);         // .commands matches KEEP(*(.commands))
        Assert.DoesNotContain(normal, rooted);   // no section attribute at all
        Assert.DoesNotContain(otherSec, rooted); // .data.ram is placed but not KEEP()'d
    }

    [Fact]
    public void ExplicitRootProvider_ResolvesOnlyRealNames_TypoAmongValidIsDetectable()
    {
        // A firmware root set is a long hand-maintained list of ISRs/exported API. A SINGLE typo among
        // valid names must NOT be silently absorbed (it would carve the real symbol away and look like a
        // clean success + bigger win). Each resolved root carries its name in .Note, so the CLI can
        // subtract to find exactly which requested names resolved to nothing -> per-root warnings, and
        // --strict-roots turns any unresolved into a non-zero exit. (Program.cs)
        var b = new GraphBuilder();
        b.Func("main", "main.c");
        b.Func("USART1_IRQHandler", "isr.c");

        var requested = new[] { "main", "USART1_IRQHandler", "USART1_IRQHandlerTYPO", "MissingISR" };
        var roots = new ExplicitRootProvider(symbols: requested).Discover(b.Graph).ToList();
        var resolved = roots.Where(r => r.Kind == RootKind.ExplicitSymbol && r.Note is not null)
                            .Select(r => r.Note!).ToHashSet();
        var unresolved = requested.Where(r => !resolved.Contains(r)).ToList();

        Assert.Equal(new[] { "main", "USART1_IRQHandler" }.ToHashSet(), resolved);
        Assert.Equal(new[] { "USART1_IRQHandlerTYPO", "MissingISR" }, unresolved); // typos surfaced, not swallowed
    }

    [Fact]
    public void LinkerSectionRootProvider_NoLinkerScript_RootsNothing()
    {
        var b = new GraphBuilder();
        _ = b.Func("early_init", "init.c");
        var sources = new[] { "__attribute__((section(\".initcall1.init\"))) void early_init(void){}" };

        // Without any KEEP()'d section it cannot know a section is retained → adds nothing (sound: the
        // baseline roots/attributes still hold; this provider only ever *adds* keeps).
        Assert.Empty(new LinkerSectionRootProvider(sources, System.Array.Empty<string>()).Discover(b.Graph));
    }
}
