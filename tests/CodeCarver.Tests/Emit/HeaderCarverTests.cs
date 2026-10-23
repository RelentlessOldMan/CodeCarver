using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// Soundness of experimental header carving: dropping unused <c>#define</c>s from a giant register header
/// must keep the transitive closure of what the code needs (offsets built from a base, masks from shifts),
/// keep every non-#define line verbatim (guards, #if, types), and never leave a dangling continuation.
/// </summary>
public class HeaderCarverTests
{
    [Fact]
    public void Carve_KeepsNeededClosure_DropsUnused_PreservesStructure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The consumer uses REG_A, MASK, and gates on FEATURE.
            File.WriteAllText(Path.Combine(dir, "app.c"), """
                #include "chip.h"
                #if FEATURE
                int use(void){ return REG_A | MASK; }
                #endif
                """);

            // The "big" register header (stand-in — carved regardless of size when named explicitly).
            File.WriteAllText(Path.Combine(dir, "chip.h"), """
                #ifndef CHIP_H
                #define CHIP_H
                #define BASE 0x1000
                #define REG_A (BASE + 4)
                #define REG_B (BASE + 8)
                #define SHIFT 2
                #define MASK (1u << SHIFT)
                #define UNUSED_MASK (7u << SHIFT)
                #define FEATURE 1
                #define BIG_UNUSED (0x1 + \
                                    0x2 + \
                                    0x3)
                #endif
                """);

            var res = HeaderCarver.Carve(dir, new[] { "chip.h" });
            var carved = File.ReadAllText(Path.Combine(dir, "chip.h"));

            // Needed closure kept: REG_A -> BASE, MASK -> SHIFT, and FEATURE (used in an #if condition).
            Assert.Contains("#define REG_A", carved);
            Assert.Contains("#define BASE", carved);   // pulled in by REG_A's body
            Assert.Contains("#define MASK", carved);
            Assert.Contains("#define SHIFT", carved);  // pulled in by MASK's body
            Assert.Contains("#define FEATURE", carved); // #if FEATURE forces it kept (branch selection)

            // Unused defines dropped.
            Assert.DoesNotContain("#define REG_B", carved);
            Assert.DoesNotContain("#define UNUSED_MASK", carved);

            // A dropped multi-line define leaves NO dangling continuation.
            Assert.DoesNotContain("BIG_UNUSED", carved);
            Assert.DoesNotContain("0x2 +", carved);

            // Structure preserved verbatim.
            Assert.Contains("#ifndef CHIP_H", carved);
            Assert.Contains("#endif", carved);

            Assert.True(res.DefinesDropped >= 3, $"expected >=3 drops, got {res.DefinesDropped}");
            Assert.True(res.BytesAfter < res.BytesBefore);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void Carve_ANumberNamesNothing()
    {
        // 0x1Fu is one token, not 0 then the name x1Fu: every register value was read as a name, millions of them in a
        // big header's needed set.
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdrn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), """
                #include "chip.h"
                int use(void){ return REG_A + (int)(1.5e+3f) + 0b101; }
                """);
            File.WriteAllText(Path.Combine(dir, "chip.h"), """
                #define REG_A (0x1Fu + OFS)
                #define OFS 4
                #define x1Fu 1
                #define e3f 2
                #define b101 3
                """);

            HeaderCarver.Carve(dir, new[] { "chip.h" });
            var carved = File.ReadAllText(Path.Combine(dir, "chip.h"));

            Assert.Contains("#define REG_A", carved);
            Assert.Contains("#define OFS", carved);   // the name after the number
            Assert.DoesNotContain("#define x1Fu", carved);
            Assert.DoesNotContain("#define e3f", carved);
            Assert.DoesNotContain("#define b101", carved);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void Carve_KeepsTokenPasteTargets_TheParseBlindSpot()
    {
        // If code builds a define name with ## (REG_##n##_BASE), the concrete define (REG_1_BASE) never
        // appears literally, so a literal-identifier seed would wrongly drop it and break the build. The
        // paste fragments "REG_"/"_BASE" must keep the whole candidate family (sound over-approximation).
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdrp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), """
                #include "chip.h"
                #define MAKE_REG(n) REG_##n##_BASE
                int use(void){ return MAKE_REG(1); }
                """);
            File.WriteAllText(Path.Combine(dir, "chip.h"), """
                #define REG_0_BASE 0x1000
                #define REG_1_BASE 0x2000
                #define REG_2_BASE 0x3000
                #define UNRELATED 0xDEAD
                """);

            HeaderCarver.Carve(dir, new[] { "chip.h" });
            var carved = File.ReadAllText(Path.Combine(dir, "chip.h"));

            Assert.Contains("#define REG_1_BASE", carved); // the actual paste target — must survive
            Assert.Contains("#define REG_0_BASE", carved); // siblings could also be targets (can't know n)
            Assert.Contains("#define REG_2_BASE", carved);
            Assert.DoesNotContain("#define UNRELATED", carved); // no fragment match, unreferenced -> dropped
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void Carve_KeptMultiLineDefine_KeepsAllContinuationLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), "#include \"m.h\"\nint use(void){ return COMBO; }\n");
            File.WriteAllText(Path.Combine(dir, "m.h"), """
                #define PART_A 1
                #define PART_B 2
                #define COMBO (PART_A + \
                               PART_B)
                #define UNUSED 9
                """);

            HeaderCarver.Carve(dir, new[] { "m.h" });
            var carved = File.ReadAllText(Path.Combine(dir, "m.h"));

            // COMBO is needed -> it AND its continuation line AND its body deps (PART_A/PART_B) are kept.
            Assert.Contains("#define COMBO", carved);
            Assert.Contains("PART_B)", carved);          // the continuation line survived
            Assert.Contains("#define PART_A", carved);
            Assert.Contains("#define PART_B", carved);
            Assert.DoesNotContain("#define UNUSED", carved);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    static string CarveOne(string appC, string chipH, IEnumerable<string>? extraNames = null, System.Text.Encoding? appEncoding = null) =>
        CarveOneWithResult(appC, chipH, extraNames, appEncoding).Carved;

    static (string Carved, HeaderCarveResult Result) CarveOneWithResult(string appC, string chipH, IEnumerable<string>? extraNames = null, System.Text.Encoding? appEncoding = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (appEncoding is null) File.WriteAllText(Path.Combine(dir, "app.c"), appC);
            else File.WriteAllText(Path.Combine(dir, "app.c"), appC, appEncoding);
            File.WriteAllText(Path.Combine(dir, "chip.h"), chipH);
            var res = HeaderCarver.Carve(dir, new[] { "chip.h" }, extraNames);
            return (File.ReadAllText(Path.Combine(dir, "chip.h")).Replace("\r\n", "\n"), res);
        }
        finally { TempDir.Delete(dir); }
    }

    /// <summary>A generic paste macro builds the name from its call's arguments, not from text next to the ##,
    /// and the call can go through a forwarding macro.</summary>
    [Fact]
    public void Carve_KeepsNamesAGenericPasteBuildsFromItsArguments()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define CAT(a, b) a##b
            #define XCAT(a, b) CAT(a, b)
            #define PIN(n) P##n
            int u(void){ return XCAT(UART, 2) + PIN(5); }
            """, """
            #define UART2 40
            #define P5 2
            #define SPI3 7
            #define Q5 3
            """);
        Assert.Contains("#define UART2 40", carved);
        Assert.Contains("#define P5 2", carved);        // a one-letter literal next to ## is a real prefix
        Assert.DoesNotContain("SPI3", carved);
        Assert.DoesNotContain("Q5", carved);
    }

    /// <summary>Forwarding macros read before the paste macro they reach become paste macros when it is learned, however
    /// long the chain; one redefined to stop forwarding still is one: any definition makes it one, in any order (the
    /// other definition may be the one an #if picks).</summary>
    [Fact]
    public void Carve_ForwardingChainsLearnedBeforeThePaster()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define A3(x) A2(x)
            #define A2(x) A1(x)
            #define A1(x) CAT(x, 7)
            #define B(x) CAT(x, 1)
            #define B(x) other(x)
            #define CAT(a, b) a##b
            int u(void){ return A3(TIMER) + B(QSPI); }
            """, """
            #define TIMER7 1
            #define QSPI1 2
            #define SPI3 3
            """);
        Assert.Contains("#define TIMER7 1", carved);
        Assert.Contains("#define QSPI1 2", carved);
        Assert.DoesNotContain("SPI3", carved);
    }

    /// <summary>Thousands of paste macros among tens of thousands of forwarding macros: each new paste macro rescanned
    /// every forwarding macro learned before it, and a work tree's header carve stopped moving for hours.</summary>
    [Fact]
    public void Carve_ManyPasteAndForwardingMacros_FinishInSeconds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdrm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var f = 0; f < 200; f++)
            {
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < 200; i++) sb.Append($"#define FWD_{f}_{i}(x) helper_{f}_{i}(x, 1)\n");
                for (var i = 0; i < 15; i++) sb.Append($"#define PST_{f}_{i}(a, b) a##b##_{f}_{i}\n");
                File.WriteAllText(Path.Combine(dir, $"m{f}.h"), sb.ToString());
            }
            File.WriteAllText(Path.Combine(dir, "app.c"), "#include \"chip.h\"\nint u(void){ return REG_A; }\n");
            File.WriteAllText(Path.Combine(dir, "chip.h"), "#define REG_A 1\n#define REG_B 2\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = HeaderCarver.Carve(dir, new[] { "chip.h" });
            // The old rescan took over 25 min here (stopped); a throttled one-core debug run takes seconds.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
                $"took {sw.Elapsed.TotalSeconds:N0} s: {string.Join(", ", res.PartMs.Select(p => $"{p.Part} {p.Ms} ms"))}");
            Assert.Equal(0, res.PasteMacros);         // counted only where defined in a carved header
            Assert.Equal(0, res.ForwardingMacros);
            Assert.Contains(res.PartMs, p => p.Part == "seed");
            var carved = File.ReadAllText(Path.Combine(dir, "chip.h"));
            Assert.Contains("REG_A", carved);
            Assert.DoesNotContain("REG_B", carved);
        }
        finally { TempDir.Delete(dir); }
    }

    /// <summary>A big header of tens of thousands of paste macros and forwarding macros: each forwarding macro learned
    /// was checked against every paste macro known (a set overlap that walks the other set), and a work tree's big
    /// headers, over a million of each, took hours.</summary>
    [Fact]
    public void Carve_BigHeaderOfPasteAndForwardingMacros_FinishesInSeconds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdrb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 60000; i++) sb.Append($"#define PST_{i}(a, b) a##b##_{i}\n");
            for (var i = 0; i < 60000; i++) sb.Append($"#define FWD_{i}(x) helper_{i}(x, 1)\n");
            sb.Append("#define REG_A 1\n#define REG_B 2\n");
            File.WriteAllText(Path.Combine(dir, "chip.h"), sb.ToString());
            File.WriteAllText(Path.Combine(dir, "app.c"), "#include \"chip.h\"\nint u(void){ return REG_A; }\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = HeaderCarver.Carve(dir, new[] { "chip.h" });
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
                $"took {sw.Elapsed.TotalSeconds:N0} s: {string.Join(", ", res.PartMs.Select(p => $"{p.Part} {p.Ms} ms"))}");
            Assert.Equal(60000, res.PasteMacros);
            Assert.Equal(60000, res.ForwardingMacros);
            var carved = File.ReadAllText(Path.Combine(dir, "chip.h"));
            Assert.Contains("REG_A", carved);
            Assert.DoesNotContain("REG_B", carved);
        }
        finally { TempDir.Delete(dir); }
    }

    /// <summary>A dropped #define must not take half a comment with it, and a #define inside a comment is text.</summary>
    [Fact]
    public void Carve_RespectsBlockComments()
    {
        var carved = CarveOne("""
            #include "chip.h"
            int u(void){ return KEEP_ME; }
            """, """
            #define REG_A 0x10 /* desc
               continues */
            /* legacy:
            #define OLD_REG 1 */
            #define REG_B 0x20 // fine
            #define KEEP_ME 1
            #endif
            """);
        Assert.Contains("#define REG_A 0x10 /* desc\n   continues */", carved);
        Assert.Contains("/* legacy:\n#define OLD_REG 1 */", carved);
        Assert.DoesNotContain("REG_B", carved);
        Assert.Contains("#define KEEP_ME 1", carved);
    }

    /// <summary>Names the carved tree can't show: tested by an SDK header outside it, or named on the build's
    /// command line.</summary>
    [Fact]
    public void Carve_KeepsNamesTheCallerSaysAreUsedElsewhere()
    {
        var carved = CarveOne("int u(void){ return 0; }\n", "#define CFG_HAS_FPU 1\n#define CFG_OTHER 2\n", new[] { "CFG_HAS_FPU" });
        Assert.Contains("CFG_HAS_FPU", carved);
        Assert.DoesNotContain("CFG_OTHER", carved);
    }

    /// <summary>A line comment ending in a backslash continues onto the next line: the define there is comment
    /// text. Dropping it would splice the comment into the define after it, which then vanishes.</summary>
    [Fact]
    public void Carve_LineCommentEndingInBackslash_ContinuesOntoTheNextLine()
    {
        var carved = CarveOne("""
            #include "chip.h"
            int u(void){ return USED_B; }
            """, """
            // legacy path: C:\vendor\regs\
            #define UNUSED_A 1
            #define USED_B 2
            #define UNUSED_C 3
            """);
        Assert.Contains("// legacy path: C:\\vendor\\regs\\\n#define UNUSED_A 1\n#define USED_B 2", carved);
        Assert.DoesNotContain("UNUSED_C", carved);
    }

    /// <summary>A forwarding macro expands its arguments before the paste: <c>CAT(UART_PREFIX, UART_NUM)</c> builds
    /// USART2 when UART_PREFIX is USART, whether that define is in the kept code or in the carved header itself.</summary>
    [Fact]
    public void Carve_KeepsNamesAPasteBuildsFromExpandedMacroArguments()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define _CAT(a, b) a##b
            #define CAT(a, b) _CAT(a, b)
            #define UART_PREFIX USART
            #define UART_NUM 2
            int u(void){ return CAT(UART_PREFIX, UART_NUM) + CAT(DBG_PORT, 1); }
            """, """
            #define DBG_PORT LPUART
            #define USART2 40
            #define LPUART1 9
            #define SPI3 7
            #define I2C1 3
            """);
        Assert.Contains("#define USART2 40", carved);
        Assert.Contains("#define LPUART1 9", carved);
        Assert.DoesNotContain("SPI3", carved);
        Assert.DoesNotContain("I2C1", carved);
    }

    /// <summary>An argument macro defined differently per #if branch builds either name: both stay.</summary>
    [Fact]
    public void Carve_KeepsEveryBranchOfAnArgumentMacro()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define _CAT(a, b) a##b
            #define CAT(a, b) _CAT(a, b)
            #if BOARD == 1
            #define UART_PREFIX USART
            #else
            #define UART_PREFIX LPUART
            #endif
            int u(void){ return CAT(UART_PREFIX, 2); }
            """, """
            #define USART2 40
            #define LPUART2 41
            #define SPI3 7
            """);
        Assert.Contains("#define USART2 40", carved);
        Assert.Contains("#define LPUART2 41", carved);
        Assert.DoesNotContain("SPI3", carved);
    }

    /// <summary>GNU's <c>, ##__VA_ARGS__</c> only swallows a comma: a logging macro is not a paste, and the short
    /// names passed to it don't keep every define that starts or ends with them.</summary>
    [Fact]
    public void Carve_VariadicCommaPaste_IsNotAPaste()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define LOG(fmt, ...) printf(fmt, ##__VA_ARGS__)
            #define LOGN(fmt, args...) printf(fmt, ## args)
            int u(int n){ LOG("x %d", n); LOGN("y %d", n); return USED; }
            """, """
            #define USED 1
            #define CH_n 5
            #define nVIC 2
            """);
        Assert.Contains("#define USED 1", carved);
        Assert.DoesNotContain("CH_n", carved);
        Assert.DoesNotContain("nVIC", carved);
    }

    /// <summary>An object-like define whose body ends in a paste macro is one (<c>F(a, b)</c> calls it), whichever is read
    /// first; one whose body only names it inside parentheses, or doesn't end in it, is not.</summary>
    [Fact]
    public void Carve_ObjectLikeAliasOfAPasteMacro_IsOne()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define JOIN JOIN_IMPL
            #define WRAP (CAT)
            #define AFTER CAT(AQ, BQ) + 1
            int u(void){ return JOIN(UART, 3) + ALIAS(TIMER, 4) + WRAP(SPI, 5) + AFTER(QSPI, 6); }
            """, """
            #define JOIN_IMPL(a, b) a##b
            #define ALIAS prefix CAT
            #define CAT(a, b) a##b
            #define UART3 1
            #define TIMER4 2
            #define SPI5 3
            #define QSPI6 4
            #define AQBQ 5
            #define AQ_REG 6
            """);
        Assert.Contains("#define UART3 1", carved);
        Assert.Contains("#define TIMER4 2", carved);
        Assert.DoesNotContain("SPI5", carved);
        Assert.DoesNotContain("QSPI6", carved);
        Assert.Contains("#define AQBQ 5", carved);   // the call in AFTER's own body passes AQ and BQ
        Assert.DoesNotContain("AQ_REG", carved);     // a piece starts it, none ends it
    }

    /// <summary>An argument list runs to its matching ')' over lines and #if branches: every branch's arguments count.</summary>
    [Fact]
    public void Carve_PasteCallSpanningLinesAndBranches_PassesEveryArgument()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define CAT(a, b) a##b
            int u(void){ return CAT(PFX,
            #if BOARD
                MIDA) +
            #else
                MIDB) +
            #endif
                0; }
            int v(void){ return f(
                LONE); }
            """, """
            #define PFXMIDA 2
            #define PFXMIDB 3
            #define PFXLONE 4
            """);
        Assert.Contains("#define PFXMIDA 2", carved);
        Assert.Contains("#define PFXMIDB 3", carved);
        Assert.DoesNotContain("PFXLONE", carved);
    }

    /// <summary>A paste macro passed as an argument (<c>APPLY(CAT, HOA, 2)</c>) pastes the call's other arguments; a call
    /// of what a paste builds (<c>CAT(FWD_, 7)(CMP)</c>) passes its arguments to it.</summary>
    [Fact]
    public void Carve_HigherOrderAndComputedPasteCalls_PassTheirArguments()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define CAT(a, b) a##b
            #define APPLY(m, a, b) m(a, b)
            #define FWD_7(x) x##_SEL
            int u(void){ return APPLY(CAT, HOA, 2) + CAT(FWD_, 7)(CMP) + APPLY(g, NOPE, 3); }
            """, """
            #define HOA2 1
            #define CMP_SEL 2
            #define NOPE3 3
            """);
        Assert.Contains("#define HOA2 1", carved);
        Assert.Contains("#define CMP_SEL 2", carved);
        Assert.DoesNotContain("NOPE3", carved);
    }

    /// <summary>Any identifier next to a ## (or a digraph %:%:) or passed to a paste macro is a piece, one letter
    /// included, and so is a number of identifier characters (<c>0x1F</c>, <c>2u</c>; not <c>1.5</c>). A define is kept
    /// when a piece starts it and one ends it; a piece at one end only keeps nothing.</summary>
    [Fact]
    public void Carve_PastePieces_AnyIdentifierLengthAndNumbers_BothEnds()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define CAT(a, b) a##b
            %:define DCAT(a, b) a %:%: b
            #define ONE Q##W
            #define TWO R##7
            int u(void){ return ONE + TWO + CAT(V, 0x1F) + CAT(S, 2u) + CAT(T, 1.5) + DCAT(DG, _X); }
            """, """
            #define QW 1
            #define R7 2
            #define V0x1F 3
            #define S2u 4
            #define DG_X 5
            #define Q1 11
            #define VREG 12
            #define x1F_REG 13
            #define W_REG 14
            #define T1 15
            """);
        Assert.Contains("#define QW 1", carved);
        Assert.Contains("#define R7 2", carved);
        Assert.Contains("#define V0x1F 3", carved);
        Assert.Contains("#define S2u 4", carved);
        Assert.Contains("#define DG_X 5", carved);
        Assert.DoesNotContain("Q1", carved);
        Assert.DoesNotContain("VREG", carved);
        Assert.DoesNotContain("x1F_REG", carved);
        Assert.DoesNotContain("W_REG", carved);
        Assert.DoesNotContain("T1", carved);
    }

    /// <summary>A pasted name ends with what its last argument expands to: a number an object define stands for
    /// (<c>#define N 2</c>), or the edges of a function-like define's body (<c>#define H() FOO</c>), nested ones
    /// followed, a define's own parameters excepted; the define in a kept file or the carved header. An edge that
    /// may expand to nothing moves inward (<c>A B E</c> can end in B), and a call at an edge gives what it is passed
    /// (<c>H(FOO)</c> with <c>#define H(x) x</c>).</summary>
    [Fact]
    public void Carve_ArgumentExpansion_NumbersAndFunctionLikeEdgesArePieces()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define CAT(a, b) a##b
            #define XCAT(a, b) CAT(a, b)
            #define N 2
            #define H() FOO
            #define J() K()
            #define K() BAR
            #define P(x) x
            #define E
            #define EMP A B E
            #define K2 P(BAZ)
            int u(void){ return XCAT(REG_, N) + XCAT(H(), _1) + XCAT(J(), _2) + XCAT(P(Z), _3) + XCAT(G(), _4) + XCAT(PIN_, M)
                + XCAT(EMP, _5) + XCAT(K2, _6); }
            """, """
            #define G() QUX
            #define M 5u
            #define REG_2 1
            #define FOO_1 2
            #define BAR_2 3
            #define Z_3 4
            #define QUX_4 5
            #define PIN_5u 6
            #define B_5 7
            #define BAZ_6 8
            #define x_3 9
            #define REG_X 10
            """);
        Assert.Contains("#define REG_2 1", carved);
        Assert.Contains("#define FOO_1 2", carved);
        Assert.Contains("#define BAR_2 3", carved);
        Assert.Contains("#define Z_3 4", carved);
        Assert.Contains("#define QUX_4 5", carved);
        Assert.Contains("#define PIN_5u 6", carved);
        Assert.Contains("#define B_5 7", carved);
        Assert.Contains("#define BAZ_6 8", carved);
        Assert.DoesNotContain("x_3", carved);
        Assert.DoesNotContain("REG_X", carved);
    }

    /// <summary>A define the carved tree doesn't hold, the build's -D or an outside header's, expands the same way and
    /// can be a paste macro; a paste of __LINE__ ends a name in any digits, and of a compiler macro nothing defines,
    /// in anything (counted).</summary>
    [Fact]
    public void Carve_ElsewhereDefinesAndCompilerMacros_GiveEnds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), """
                #include "chip.h"
                #define CAT(a, b) a##b
                #define XCAT(a, b) CAT(a, b)
                int u(void){ return XCAT(REG_, DN) + SDKCAT(SDK, _A) + XCAT(LN_, __LINE__); }
                """);
            File.WriteAllText(Path.Combine(dir, "chip.h"), """
                #define REG_9 1
                #define SDK_A 2
                #define LN_17 3
                #define REG_Q 4
                #define LN_X 5
                """);
            var keys = new Dictionary<string, long>();
            HeaderCarver.Carve(dir, new[] { "chip.h" }, partDone: (k, v) => keys[k] = v,
                               elsewhereDefines: new[] { "#define DN 9", "#define SDKCAT(a, b) a##b" });
            var carved = File.ReadAllText(Path.Combine(dir, "chip.h"));
            Assert.Contains("#define REG_9 1", carved);
            Assert.Contains("#define SDK_A 2", carved);
            Assert.Contains("#define LN_17 3", carved);
            Assert.DoesNotContain("REG_Q", carved);
            Assert.DoesNotContain("LN_X", carved);
            Assert.Equal(0, keys["pastedUnknownEnds"]);

            File.WriteAllText(Path.Combine(dir, "app.c"), """
                #include "chip.h"
                #define CAT(a, b) a##b
                int u(void){ return CAT(SZ_, __SIZEOF_WHAT__); }
                """);
            File.WriteAllText(Path.Combine(dir, "chip.h"), "#define SZ_QQ 1\n#define OTHER 2\n");
            HeaderCarver.Carve(dir, new[] { "chip.h" }, partDone: (k, v) => keys[k] = v);
            carved = File.ReadAllText(Path.Combine(dir, "chip.h"));
            Assert.Contains("#define SZ_QQ 1", carved);
            Assert.DoesNotContain("OTHER", carved);
            Assert.Equal(1, keys["pastedUnknownEnds"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>One letter passed to a paste macro keeps only the names it starts that a piece also ends: work's tree
    /// kept 109M of 122M defines for a piece at one end. The summary counts what one end alone would have kept.</summary>
    [Fact]
    public void Carve_OneLetterPiece_KeepsOnlyNamesBothEndsMatch()
    {
        var hdr = new System.Text.StringBuilder("#define A1 0\n");
        for (var i = 0; i < 1000; i++) hdr.Append($"#define A_REG{i}_X {i}\n");
        var keys = new Dictionary<string, long>();
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), "#include \"chip.h\"\n#define CAT(a, b) a##b\nint u(void){ return CAT(A, 1); }\n");
            File.WriteAllText(Path.Combine(dir, "chip.h"), hdr.ToString());
            var res = HeaderCarver.Carve(dir, new[] { "chip.h" }, partDone: (k, v) => keys[k] = v);
            Assert.Equal(1, res.DefinesKept);
            Assert.Equal(1000, res.DefinesDropped);
            Assert.Equal(1000, keys["droppedOneEndPiece"]);
            Assert.Contains("#define A1 0", File.ReadAllText(Path.Combine(dir, "chip.h")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A piece counts only at the end of a pasted name it can stand at: an argument pasted between two other
    /// operands (<c>HWB_##blk##_r##n##_VAL</c>) starts and ends nothing, nor does a literal operand there (_r), while one
    /// of several tokens, an expansion, a forwarder passing a parameter with other tokens, or __VA_ARGS__ there can be
    /// at either end. An argument pasted only at the front (<c>x##_SUF</c>) only starts a name; only at the back, ends
    /// one. Work's one-letter arguments between two operands kept every name for both ends.</summary>
    [Fact]
    public void Carve_PieceSides_OnlyWhereAPasteCanPutThem()
    {
        var carved = CarveOne("""
            #include "chip.h"
            #define HWP(blk, n) HWB_##blk##_r##n##_VAL
            #define FW(b, n) HWP(b, n)
            #define MF(x) HWP(x y, 3)
            #define VP(...) PRE_##__VA_ARGS__##_SUF
            #define SUF(x) x##_SUF
            #define PFX(x) PFX_##x
            #define BLK T x
            int u(void){ return HWP(T, 0) + HWP(Q, 1) + FW(BLK, 0) + HWP(A b, 2) + MF(C) + VP(B1, C1) + SUF(D) + PFX(E); }
            """, """
            #define HWB_T_r0_VAL 1
            #define HWB_Q_r1_VAL 2
            #define HWB_T 3
            #define x_r0_VAL 4
            #define HWB_A 5
            #define b_r2_VAL 6
            #define HWB_C 7
            #define y_r3_VAL 8
            #define PRE_B1 9
            #define C1_SUF 10
            #define D_SUF 11
            #define PFX_E 12
            #define T0 20
            #define QUQ 22
            #define _r_VAL 23
            #define PRE_D 24
            #define E_SUF 25
            """);
        foreach (var kept in new[] { "HWB_T_r0_VAL", "HWB_Q_r1_VAL", "HWB_T", "x_r0_VAL", "HWB_A", "b_r2_VAL", "HWB_C", "y_r3_VAL",
                                     "PRE_B1", "C1_SUF", "D_SUF", "PFX_E" })
            Assert.Contains($"#define {kept} ", carved);
        foreach (var dropped in new[] { "T0", "QUQ", "_r_VAL", "PRE_D", "E_SUF" })
            Assert.DoesNotContain($"#define {dropped} ", carved);
    }

    /// <summary>The summary says why each define was kept: its name is used, or the shorter of the pieces it starts and
    /// ends with, by length. A define kept for a piece counts as that, not as a name (its own line names it). One a
    /// piece starts or ends but not both is dropped, and counted.</summary>
    [Fact]
    public void Carve_CountsWhyEachDefineWasKept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), """
                #include "chip.h"
                #define CAT(a, b) a##b
                int u(void){ return USED + CAT(V, 1) + CAT(UARTX, _CR2); }
                """);
            File.WriteAllText(Path.Combine(dir, "chip.h"), """
                #define USED 1
                #define V1 (BASE_V + 1)
                #define VREG 2
                #define UARTX_CR2 3
                #define BASE_V 4
                #define OTHER 5
                """);
            var keys = new Dictionary<string, long>();
            var res = HeaderCarver.Carve(dir, new[] { "chip.h" }, partDone: (k, v) => keys[k] = v);
            Assert.Equal(2, res.DefinesDropped);   // VREG, OTHER
            Assert.Equal(2, keys["keptByName"]);   // USED, and BASE_V from V1's body
            Assert.Equal(1, keys["keptByPiece1"]); // V1, for V and 1
            Assert.Equal(1, keys["keptByPiece4plus"]); // UARTX_CR2, for UARTX and _CR2
            Assert.Equal(0, keys["keptByPiece2"] + keys["keptByPiece3"] + keys["keptBySystemHeaders"]);
            Assert.Equal(1, keys["droppedOneEndPiece"]); // VREG: V starts it, no piece ends it
            Assert.Equal(2, keys["pieces1"]);      // V, 1
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>An argument expands to its body before the paste, but only what can begin or end the body joins it:
    /// <c>#define PORT ((t)U)</c> builds no name from U, and <c>(LEFT) mid + RIGHT</c> only names ending in RIGHT. Every
    /// definition counts, however many.</summary>
    [Fact]
    public void Carve_ArgumentExpansion_OnlyBodyEdgesArePieces()
    {
        var app = new System.Text.StringBuilder("""
            #include "chip.h"
            #define CAT(a, b) a##b
            #define PORT ((t)U)
            #define EDGE (LEFT) mid + RIGHT

            """);
        for (var i = 0; i < 20; i++) app.Append($"#if B == {i}\n#define MANY BODY{i}X\n#endif\n");
        app.Append("int u(void){ return CAT(PORT, 1) + CAT(EDGE, 2) + CAT(MANY, 3); }\n");
        var carved = CarveOne(app.ToString(), """
            #define U1 1
            #define LEFT2 2
            #define RIGHT2 3
            #define mid2 4
            #define BODY19X_3 5
            """);
        Assert.DoesNotContain("#define U1", carved);
        Assert.DoesNotContain("LEFT2", carved);
        Assert.Contains("#define RIGHT2 3", carved);
        Assert.DoesNotContain("mid2", carved);
        Assert.Contains("#define BODY19X_3 5", carved);   // the 20th body (16 were kept before)
    }

    /// <summary>The counts are of names defined in the carved header; whether one is a paste macro is judged from every
    /// definition, a paste macro defined only in a kept file included.</summary>
    [Fact]
    public void Carve_CountsPasteAndForwardingMacrosDefinedInTheCarvedHeaders()
    {
        var (_, res) = CarveOneWithResult("""
            #include "chip.h"
            #define CAT(a, b) a##b
            int u(void){ return 0; }
            """, """
            #define FWD(a) CAT(a, 1)
            #define TBL_0(x) ((x) + TBL_1)
            #define OWN(a) a##_x
            #define ALIAS FWD
            #define PLAIN 3
            """);
        Assert.Equal(3, res.PasteMacros);         // FWD, OWN, ALIAS (CAT is defined outside it)
        Assert.Equal(2, res.ForwardingMacros);    // FWD, TBL_0
        Assert.Equal(5, res.DefinesKept + res.DefinesDropped);
    }

    /// <summary>Up to 64 paste pieces a define name is tried against each piece; past 64, against its prefixes and
    /// suffixes of the lengths some piece has. Both ways keep the same defines: names a piece starts and a piece ends,
    /// the two from different calls included, a piece equal to the name; never one end only, nor a piece in the
    /// middle.</summary>
    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    public void Carve_PieceLookup_SameAtTheCrossover(int pieces)
    {
        var app = new System.Text.StringBuilder("#include \"chip.h\"\n#define CAT(a, b) a##b\n");
        app.Append("int u(void){ return CAT(HEADP, 1) + CAT(TAILP, 2) + CAT(EXACTP, 2)");
        for (var i = 3; i < pieces; i++) app.Append($" + CAT(PX{i}, 0)");
        app.Append("; }\n");
        var carved = CarveOne(app.ToString(), """
            #define HEADP1 1
            #define HEADP2 2
            #define EXACTP 3
            #define MID_HEADP_X 4
            #define NONE_REG 5
            #define PX50 6
            #define HEADP_REG 7
            #define REG_TAILP 8
            #define PX5_LAST 9

            """);
        Assert.Contains("#define HEADP1 1", carved);
        Assert.Contains("#define HEADP2 2", carved);
        Assert.Contains("#define EXACTP 3", carved);
        Assert.Contains("#define PX50 6", carved);
        Assert.DoesNotContain("MID_HEADP_X", carved);
        Assert.DoesNotContain("NONE_REG", carved);
        Assert.DoesNotContain("HEADP_REG", carved);
        Assert.DoesNotContain("REG_TAILP", carved);
        Assert.DoesNotContain("PX5_LAST", carved);
    }

    /// <summary>A UTF-16 source (with a byte-order mark) is text: the macros it uses are counted.</summary>
    [Fact]
    public void Carve_CountsUsesInUtf16Sources()
    {
        var carved = CarveOne("#include \"chip.h\"\nint u(void){ return WIDE_ONLY; }\n",
                              "#define WIDE_ONLY 1\n#define WIDE_OTHER 2\n", appEncoding: System.Text.Encoding.Unicode);
        Assert.Contains("#define WIDE_ONLY 1", carved);
        Assert.DoesNotContain("WIDE_OTHER", carved);
    }

    [Fact]
    public void Carve_KeepsMacrosOnlySystemHeadersRead()
    {
        // Review H1: <assert.h> reads NDEBUG, <features.h> _GNU_SOURCE, <windows.h> WIN32_LEAN_AND_MEAN. Nothing in the
        // tree names them, and dropping one changes what the build compiles.
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), "#include \"cfg.h\"\n#include <assert.h>\nint f(int x){ assert(x); return x; }\n");
            File.WriteAllText(Path.Combine(dir, "cfg.h"),
                "#define NDEBUG\n#define _GNU_SOURCE 1\n#define WIN32_LEAN_AND_MEAN\n#define NOT_USED 7\n");
            HeaderCarver.Carve(dir, new[] { "cfg.h" });
            var text = File.ReadAllText(Path.Combine(dir, "cfg.h"));
            Assert.Contains("NDEBUG", text);
            Assert.Contains("_GNU_SOURCE", text);
            Assert.Contains("WIN32_LEAN_AND_MEAN", text);
            Assert.DoesNotContain("NOT_USED", text);
        }
        finally { Directory.Delete(dir, true); }
    }
}
