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
    public void NoUsedNames_AndNoOtherFiles_KeepsOnlyNonDefineLines()
    {
        W("chip.h", Chip);
        var res = HeaderCarver.CarveFromUsed(_dir, new[] { "chip.h" }, new HashSet<ulong>());
        Assert.Equal(0, res.DefinesKept);
        Assert.Contains("kq_rec", R("chip.h"));
        Assert.DoesNotContain("#define", R("chip.h"));
    }
}
