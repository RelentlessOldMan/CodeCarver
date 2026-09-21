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
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
