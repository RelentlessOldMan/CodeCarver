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
    public void LinkerSectionRoots_OtherSpellingsAndFunctionPointerTables()
    {
        var b = new GraphBuilder();
        var vectors = b.Global("vectors", "vec.c");     // leading attribute on a function-pointer array
        var hooks = b.Global("hook_tab", "hook.c");     // trailing attribute on a function-pointer array
        var cmd = b.Global("cmd_a", "cmd.c");           // __section__ spelling
        var cmd2 = b.Global("cmd_b", "cmd.c");          // [[gnu::section]]
        var ent = b.Global("ent_a", "reg.c");           // KEEP body with nested parentheses
        var sources = new[]
        {
            "__attribute__((section(\".isr_vector\"))) void (* const vectors[])(void) = { 0 };",
            "void (* const hook_tab[])(void) __attribute__((section(\".hooks\"))) = { 0 };",
            "const int cmd_a __attribute__((__section__(\".hooks\"))) = 1;",
            "[[gnu::section(\".hooks\")]] const int cmd_b = 2;",
            "const int ent_a __attribute__((section(\".mytab.a\"))) = 3;",
        };
        const string linker = """
            SECTIONS {
              .vec : { KEEP(*(.isr_vector)) }
              .h : { KEEP(*(.hooks)) }
              .t : { KEEP(*(EXCLUDE_FILE(*crt0.o) .mytab.*)) }
            }
            """;
        var rooted = new LinkerSectionRootProvider(sources, new[] { linker }).Discover(b.Graph).Select(r => r.Node).ToHashSet();
        foreach (var id in new[] { vectors, hooks, cmd, cmd2, ent })
            Assert.Contains(id, rooted);
    }

    // Review S4: a section walked between two symbols the script defines around it is a table, KEEP or not: linked
    // without --gc-sections nothing removes its entries, and nothing names them.
    [Theory]
    [InlineData("__drv_start = .;\n    *(.drivers)\n    __drv_end = .;")]
    [InlineData("PROVIDE(__drv_start = .);\n    *(SORT(.drivers*))\n    PROVIDE(__drv_end = .);")]
    [InlineData("__drv_start = . ;\n    main.o(.drivers) *(.drivers)\n    __drv_end = . ;")]
    public void LinkerSectionRoots_SectionBracketedBySymbols(string body)
    {
        var b = new GraphBuilder();
        var drv = b.Global("uart_drv", "drv.c");
        var ram = b.Global("buf", "ram.c");
        var sources = new[]
        {
            "__attribute__((section(\".drivers\"))) const struct drv uart_drv = { 0 };",
            "__attribute__((section(\".data.ram\"))) int buf;",
        };
        var linker = "SECTIONS {\n  .data : {\n    " + body + "\n    *(.data.ram)\n  }\n}\n";
        var rooted = new LinkerSectionRootProvider(sources, new[] { linker }).Discover(b.Graph).Select(r => r.Node).ToHashSet();
        Assert.Contains(drv, rooted);
        Assert.DoesNotContain(ram, rooted);
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
    public void LinkerSectionRootProvider_StreamsSourcesLazily_NeverBuffersWholeTree()
    {
        // Regression for the death-corpus OOM: the CLI feeds this provider a file-reading sequence spanning
        // the WHOLE tree. If the provider (or the CLI) materialises it into a List, ~100 GB of source becomes
        // ~1.6 GB of UTF-16 strings held at once — OOM after the graph is already built. The contract is:
        //   (a) constructing the provider reads NOTHING (no eager .ToList() of the tree), and
        //   (b) Discover consumes the sequence exactly ONCE, lazily.
        var b = new GraphBuilder();
        var early = b.Func("early_init", "init.c");

        var enumerations = 0;
        System.Collections.Generic.IEnumerable<string> Sources()
        {
            enumerations++;
            yield return "__attribute__((section(\".initcall1.init\"))) void early_init(void){}";
        }
        const string linker = "SECTIONS { .init : { KEEP(*(.initcall1.init)) } }";

        var provider = new LinkerSectionRootProvider(Sources(), new[] { linker });
        Assert.Equal(0, enumerations); // construction must not touch the source sequence

        var rooted = provider.Discover(b.Graph).Select(r => r.Node).ToHashSet();
        Assert.Contains(early, rooted);
        Assert.Equal(1, enumerations); // consumed exactly once — streamed, not buffered or re-read
    }

    [Fact]
    public void AsmReferenceRootProvider_StreamsAsmLazily_NeverBuffersUpFront()
    {
        // Same streaming contract as the linker-section provider (the .s/.asm set is smaller, but the CLI
        // hands it a file-reading sequence too): construction reads nothing; Discover consumes it once.
        var b = new GraphBuilder();
        var nmi = b.Func("NMI_Handler", "app.c");

        var enumerations = 0;
        System.Collections.Generic.IEnumerable<string> Asm()
        {
            enumerations++;
            yield return ".word NMI_Handler\n";
        }

        var provider = new AsmReferenceRootProvider(Asm());
        Assert.Equal(0, enumerations);

        var rooted = provider.Discover(b.Graph).Select(r => r.Node).ToHashSet();
        Assert.Contains(nmi, rooted);
        Assert.Equal(1, enumerations);
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
