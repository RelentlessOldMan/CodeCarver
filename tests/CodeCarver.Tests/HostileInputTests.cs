using System;
using System.Linq;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// Local version of the work machine's hostile-input sweep (eval #2/#7): feed the front-end malformed,
/// degenerate, and adversarial sources and assert it always returns — never throws, hangs, or dies with a
/// native AccessViolation. The tool's contract is warn-and-skip, never crash. These are the cases past
/// evals used to break it; keeping them here makes "12/12 graceful" a permanent CI gate instead of a
/// number only the work machine can produce.
/// </summary>
public class HostileInputTests
{
    // BuildGraph must complete and return a graph for any input. If a case AV'd (native stack overflow),
    // the whole test process would die — so a green run IS the proof the 64MB parse-worker stack holds.
    private static void MustNotCrash(string name, string text, string lang = "c")
    {
        using ICarveFrontEnd fe = lang == "cpp" ? new CppFrontEnd() : new CFrontEnd();
        var graph = fe.BuildGraph(new[] { (name, text) });
        Assert.NotNull(graph);
    }

    [Fact] public void TruncatedMidFunction() => MustNotCrash("t.c", "int f(void) { int x = 1; if (x) { return x");
    [Fact] public void UnbalancedBraces() => MustNotCrash("t.c", "int f(void) { { { } int g(void){return 0;}");
    [Fact] public void ZeroByteSource() => MustNotCrash("t.c", "");
    [Fact] public void OnlyWhitespaceAndComments() => MustNotCrash("t.c", "   \n\t\n/* nothing */\n// x\n");
    [Fact] public void DeepIfdefNesting() => MustNotCrash("t.c", string.Concat(Enumerable.Repeat("#if 1\n", 400)) + "int f(void){return 0;}\n" + string.Concat(Enumerable.Repeat("#endif\n", 400)));
    [Fact] public void DeepBracesSmall() => MustNotCrash("t.c", "int f(void)" + new string('{', 2000) + new string('}', 2000));
    [Fact] public void SelfInclude() => MustNotCrash("self.c", "#include \"self.c\"\nint f(void){return 0;}");
    [Fact] public void HugeSingleLine() => MustNotCrash("t.c", "int f(void){return " + string.Concat(Enumerable.Repeat("1+", 200000)) + "1;}");

    [Fact]
    public void RandomBinaryInCFile()
    {
        var rng = new Random(1337);
        var buf = new char[8192];
        for (var i = 0; i < buf.Length; i++) buf[i] = (char)rng.Next(1, 256); // control chars, high bytes, no NUL-term games
        MustNotCrash("bin.c", new string(buf));
    }

    [Fact]
    public void IncludeCycle()
    {
        // a includes b, b includes a — the include-closure must terminate, not loop forever.
        using var fe = new CFrontEnd();
        var g = fe.BuildGraph(new[]
        {
            ("a.c", "#include \"b.h\"\nint a(void){return 0;}"),
            ("b.h", "#include \"a.h\"\nint b(void);"),
            ("a.h", "#include \"b.h\"\nint a(void);"),
        });
        Assert.NotNull(g);
    }

    [Fact]
    public void DeeplyNested_OnParseWorkerPath_DoesNotAccessViolate()
    {
        // THE eval-#2 AccessViolation repro, local at last. The file is >256KB so it takes the budgeted
        // WORKER thread (not the inline main-thread path), and it is ~150k-deep nesting so tree-sitter's
        // native recursion is enormous. On the old 1MB worker stack this overflowed -> native AV (0xC0000005)
        // that kills the process with no diagnostic. On the 64MB stack it either parses or hits the parse
        // budget and is kept whole -- either way it must RETURN, not crash. Reaching the assert = fix holds.
        var text = "int f(void){return " + new string('(', 150000) + "1" + new string(')', 150000) + ";}";
        Assert.True(text.Length > 256 * 1024, "must exceed the worker-path threshold");
        using var fe = new CFrontEnd { ParseBudgetMs = 15000 };
        var graph = fe.BuildGraph(new[] { ("deep.c", text) });
        Assert.NotNull(graph);
    }
}
