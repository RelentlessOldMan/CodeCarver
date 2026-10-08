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

    static string CarveOne(string appC, string chipH, IEnumerable<string>? extraNames = null, System.Text.Encoding? appEncoding = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (appEncoding is null) File.WriteAllText(Path.Combine(dir, "app.c"), appC);
            else File.WriteAllText(Path.Combine(dir, "app.c"), appC, appEncoding);
            File.WriteAllText(Path.Combine(dir, "chip.h"), chipH);
            HeaderCarver.Carve(dir, new[] { "chip.h" }, extraNames);
            return File.ReadAllText(Path.Combine(dir, "chip.h")).Replace("\r\n", "\n");
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

    /// <summary>A UTF-16 source (with a byte-order mark) is text: the macros it uses are counted.</summary>
    [Fact]
    public void Carve_CountsUsesInUtf16Sources()
    {
        var carved = CarveOne("#include \"chip.h\"\nint u(void){ return WIDE_ONLY; }\n",
                              "#define WIDE_ONLY 1\n#define WIDE_OTHER 2\n", appEncoding: System.Text.Encoding.Unicode);
        Assert.Contains("#define WIDE_ONLY 1", carved);
        Assert.DoesNotContain("WIDE_OTHER", carved);
    }
}
