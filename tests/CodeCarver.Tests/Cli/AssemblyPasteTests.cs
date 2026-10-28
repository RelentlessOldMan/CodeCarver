using CodeCarver.Core.Frontend;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Startup assembly the T2 firmware corpus (CodeSpawner 1.5) carries: an entry point only assembly defines, handler
/// names a .macro or a #define pastes together, and assembly or a linker script past maxParseBytes that is the only
/// thing naming a function. Each was a function the carve dropped from a from-the-entry-point carve.
/// </summary>
public class AssemblyPasteTests
{
    const string Main = "int kq_init(void);\nint main(void){ return kq_init(); }\n";

    static TreeCarve Tree(string startup, string? extraRel = null, string? extraText = null)
    {
        var t = new TreeCarve()
            .W("main.c", Main)
            .W("init.c", "int kq_init(void){ return 1; }\n")
            .W("handlers.c", "void KQ_UART_IRQHandler(void){}\nvoid KQ_TIM_Handler(void){}\n")
            .W("dead.c", "int kq_dead(void){ return 2; }\n")
            .W("startup.S", startup);
        if (extraRel is not null) t.W(extraRel, extraText!);
        t.Compile("main.c").Compile("init.c").Compile("handlers.c").Compile("dead.c").Compile("startup.S");
        return t;
    }

    const string Reset = "    .text\n    .global Reset_Handler\nReset_Handler:\n    bl main\n    b .\n";

    [Fact]
    public void EntryPointDefinedOnlyInAssembly_RootsWhatItCalls()
    {
        using var t = Tree(Reset);
        var r = t.Carve(entry: "Reset_Handler");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("init.c"), r.Out + r.Err);
        Assert.False(t.Kept("dead.c"), r.Out + r.Err);
        Assert.Contains("roots.entryPointsDefinedInAssembly = 1", t.Summary);
    }

    [Fact]
    public void MacroPastedHandlerName_IsKept()
    {
        using var t = Tree("    .macro def_vec name\n    .word \\name\\()_IRQHandler\n    .endm\n    .section .isr_vector\n"
                           + "    def_vec KQ_UART\n" + Reset);
        var r = t.Carve(entry: "Reset_Handler");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("handlers.c"), r.Out + r.Err);
        Assert.False(t.Kept("dead.c"), r.Out + r.Err);
    }

    [Fact]
    public void MacroFromAnIncludedFile_IsExpanded()
    {
        using var t = Tree(".include \"vec.inc\"\n    def_vec KQ_UART\n" + Reset,
                           "vec.inc", "    .macro def_vec name\n    .word \\name\\()_IRQHandler\n    .endm\n");
        var r = t.Carve(entry: "Reset_Handler");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("handlers.c"), r.Out + r.Err);
    }

    [Fact]
    public void PreprocessorPastedHandlerName_IsKept()
    {
        using var t = Tree("#define H(x) x##_Handler\n    .section .isr_vector\n    .word H(KQ_TIM)\n" + Reset);
        var r = t.Carve(entry: "Reset_Handler");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("handlers.c"), r.Out + r.Err);
    }

    [Fact]
    public void AssemblyPastMaxParseBytes_StillRootsItsCalls()
    {
        using var t = Tree(Reset + "    bl KQ_TIM_Handler\n/*" + new string('-', 6000) + "*/\n");
        var r = t.Carve(entry: "Reset_Handler", extraToml: "[advanced]\nmaxParseBytes = 4000\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("handlers.c"), r.Out + r.Err);
        Assert.False(t.Kept("dead.c"), r.Out + r.Err);
    }

    [Fact]
    public void LinkerScriptPastMaxParseBytes_StillKeepsItsSections()
    {
        using var t = Tree(Reset, "fw.ld",
            "SECTIONS\n{\n  .kqtab : { KEEP(*(.kqtab)) }\n}\n/*" + new string('-', 6000) + "*/\n");
        t.W("tab.c", "__attribute__((section(\".kqtab\"))) int kq_tab_entry(void){ return 3; }\n").Compile("tab.c");
        var r = t.Carve(entry: "Reset_Handler", extraToml: "[advanced]\nmaxParseBytes = 4000\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("tab.c"), r.Out + r.Err);
    }

    [Fact]
    public void Expander_HandlesDefaultsKeywordsNestingAndObjectMacros()
    {
        var m = new AsmMacros();
        var text = m.Feed(".macro outer a, b=KQ_DEF\n  inner \\a\\()_X, \\b\n.endm\n"
                          + ".macro inner p q\n  .word \\p \\q\\()_Y\n.endm\n"
                          + "  outer KQ_ONE\n  outer b=KQ_KW, a=KQ_TWO\n"
                          + "#define CAT(a, b) a ## b\n#define V CAT(KQ_O, BJ)\n  .word CAT(KQ_P, Q)\n");
        // inner is defined after outer, but before outer is used: expanded through it.
        Assert.Contains("KQ_ONE_X", text);
        Assert.Contains("KQ_DEF_Y", text);
        Assert.Contains("KQ_TWO_X", text);
        Assert.Contains("KQ_KW_Y", text);
        Assert.Contains("KQ_PQ", text);
        Assert.Contains("KQ_OBJ", m.Feed(string.Join("\n", m.ObjectBodies(new[] { "V" }))));
    }

    [Fact]
    public void Chunks_DropBlockCommentsAcrossLinesAndKeepCode()
    {
        var chunks = TextChunks.WithoutBlockComments(new[] { "a /* x", "y */ b", "/* z */ c /* w", "*/" }, chunkChars: 4).ToList();
        Assert.Equal("a \n  b\n  c \n \n", string.Concat(chunks));
        Assert.True(chunks.Count > 1);
    }
}
