using CodeCarver.Core.Emit;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The header carve from what the compiles used (<c>-E -dU</c>, docs/USED-MACROS.md): a big header keeps a define only if
/// a compile used its name, or a kept non-C text file names it (with the names that define's body holds). No model of
/// #if or paste macros is needed: the compiler has already evaluated them.
/// </summary>
public sealed class HeaderCarverUsedTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-hcu-" + Guid.NewGuid().ToString("N"));
    public HeaderCarverUsedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    void W(string rel, string text) => File.WriteAllText(Path.Combine(_dir, rel), text);
    string R(string rel) => File.ReadAllText(Path.Combine(_dir, rel));

    const string Chip = """
        #ifndef KQ_CHIP_H
        #define KQ_CHIP_H
        #define KQ_MODE 1
        #define KQ_CAT(a, b) a##b
        #define KQ_REG(n) KQ_CAT(KQ_REG_, n)
        #define KQ_REG_0 0x100
        #define KQ_REG_1 0x104
        #define KQ_REG_2 0x108
        #define KQ_TWO_ONLY 0x200
        #define KQ_LD_BASE (KQ_LD_INNER + 0x10)
        #define KQ_LD_INNER 0x8000
        #define KQ_DBG_ONLY 0x9000
        #define KQ_SPLIT (1 + \
                          2)
        typedef struct { int kq_field; } kq_rec;
        #endif
        """;

    [Fact]
    public void KeepsWhatTheCompilesUsed_AndWhatOtherFilesName()
    {
        W("chip.h", Chip);
        W("link.ld", "SECTIONS { . = KQ_LD_BASE; }\n");      // a linker script names a define directly
        W("probe.cmm", "Data.Set KQ_DBG_ONLY %Long 1\n");    // a debugger script can evaluate a macro from the debug info
        // What one compile of `#if KQ_MODE == 2 ... KQ_TWO_ONLY #else KQ_REG(1) #endif` printed with -dU.
        var used = new[] { "KQ_CHIP_H", "KQ_MODE", "KQ_REG", "KQ_CAT", "KQ_REG_1" }.Select(n => NameKey.Short(n)).ToHashSet();
        var keys = new Dictionary<string, long>();

        var res = HeaderCarver.CarveFromUsed(_dir, new[] { "chip.h" }, used, (k, v) => keys[k] = v);
        var carved = R("chip.h");

        foreach (var kept in new[] { "KQ_CHIP_H", "KQ_MODE 1", "KQ_CAT(a, b)", "KQ_REG(n)", "KQ_REG_1 ", "KQ_LD_BASE", "KQ_LD_INNER", "KQ_DBG_ONLY" })
            Assert.Contains("#define " + kept, carved);
        foreach (var dropped in new[] { "KQ_REG_0", "KQ_REG_2", "KQ_TWO_ONLY", "KQ_SPLIT" })
            Assert.DoesNotContain(dropped, carved);
        Assert.DoesNotContain("2)", carved);                                // no dangling continuation
        Assert.Contains("typedef struct { int kq_field; } kq_rec;", carved);  // non-define lines verbatim
        Assert.Contains("#ifndef KQ_CHIP_H", carved);
        Assert.Equal(4, res.DefinesDropped);
        Assert.Equal(5, keys["keptByUsed"]);
        Assert.Equal(3, keys["keptByOther"]);                              // KQ_LD_BASE, KQ_LD_INNER from its body, KQ_DBG_ONLY
    }

    [Fact]
    public void ADefineAnotherFileNames_KeepsWhatDecidesItsBlock()
    {
        // No compile enters the linker-only block, so -dU never records KQ_BOOT_FROM_SRAM; the linker script needs it.
        W("chip.h", """
            #define KQ_UNUSED 1
            #ifdef KQ_FOR_LINKER
            #if KQ_BOOT_FROM_SRAM
            #define KQ_TEXT_REGION SRAM
            #else
            #define KQ_TEXT_REGION FLASH
            #endif
            #endif
            #define KQ_BOOT_FROM_SRAM 1
            #define KQ_FOR_LINKER_NOT 2
            """);
        W("link.ld", "SECTIONS { .text : { *(.text) } > KQ_TEXT_REGION }\n");
        HeaderCarver.CarveFromUsed(_dir, new[] { "chip.h" }, new HashSet<ulong>());
        var carved = R("chip.h");
        Assert.Contains("#define KQ_BOOT_FROM_SRAM 1", carved);
        Assert.Contains("#define KQ_TEXT_REGION FLASH", carved);
        Assert.DoesNotContain("KQ_UNUSED", carved);
        Assert.DoesNotContain("KQ_FOR_LINKER_NOT", carved);
    }

    [Fact]
    public void ANamePassingThroughASmallHeader_KeepsTheBigHeadersDefine()
    {
        W("chip.h", "#define KQ_SRAM_BASE 0x20000000\n#define KQ_OTHER_BASE 0x30000000\n");
        W("memmap.h", "#define KQ_RAM_ORIGIN KQ_SRAM_BASE\n");   // small: kept whole, not carved
        W("link.ld", "#include \"memmap.h\"\nMEMORY { RAM : ORIGIN = KQ_RAM_ORIGIN }\n");
        var keys = new Dictionary<string, long>();
        HeaderCarver.CarveFromUsed(_dir, new[] { "chip.h" }, new HashSet<ulong>(), (k, v) => keys[k] = v);
        Assert.Contains("#define KQ_SRAM_BASE", R("chip.h"));
        Assert.DoesNotContain("KQ_OTHER_BASE", R("chip.h"));
        Assert.Equal("#define KQ_RAM_ORIGIN KQ_SRAM_BASE\n", R("memmap.h"));
        Assert.Equal(1, keys["used.smallHeaders"]);
    }

    [Fact]
    public void NamesToFollow_AreKeptWithWhatTheirBodiesName()
    {
        W("chip.h", "#define KQ_FAST_TIMER (KQ_TIMER_BASE + 4)\n#define KQ_TIMER_BASE 0x4000\n#define KQ_SLOW 1\n");
        var follow = new HashSet<ulong> { NameKey.Short("KQ_FAST_TIMER") };
        HeaderCarver.CarveFromUsed(_dir, new[] { "chip.h" }, new HashSet<ulong>(), follow: follow);
        Assert.Contains("#define KQ_FAST_TIMER", R("chip.h"));
        Assert.Contains("#define KQ_TIMER_BASE", R("chip.h"));
        Assert.DoesNotContain("KQ_SLOW", R("chip.h"));
    }

    [Fact]
    public void NoUsedNames_AndNoOtherFiles_KeepsOnlyNonDefineLines()
    {
        W("chip.h", Chip);
        var res = HeaderCarver.CarveFromUsed(_dir, new[] { "chip.h" }, new HashSet<ulong>());
        Assert.Equal(0, res.DefinesKept);
        Assert.Contains("kq_rec", R("chip.h"));
        Assert.DoesNotContain("#define", R("chip.h"));
    }
}
