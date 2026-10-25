using System.Text;
using CodeCarver.Core.Emit;
using CodeCarver.Core.Preprocess;
using Xunit;
using Xunit.Abstractions;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The used-macros header carve streams what it reads: the compiles' files, the kept non-C files and the big headers.
/// What it holds is the distinct names, not the text. Here 2,000 compile files of 2,000 names each, a 16 MB register
/// header and a 6 MB debugger script.
/// </summary>
[Collection(nameof(CodeCarver.Tests.Preprocess.TemplateWalkMemoryTests))]   // measures memory: no other memory test at once
public sealed class HeaderCarverUsedMemoryTests(ITestOutputHelper output) : IDisposable
{
    const double MaxAllocatedPerInputByte = 6;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-hcum-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void AllocatesLittlePerInputByte()
    {
        var tree = Path.Combine(_dir, "tree");
        var du = Path.Combine(_dir, "du");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(du);
        const int Defines = 300_000;
        var h = new StringBuilder("#ifndef KQ_BIG_H\n#define KQ_BIG_H\n");
        for (var i = 0; i < Defines; i++) h.Append($"#define KQ_BLK{i % 97}_REG{i}_FIELD_SHFT (KQ_BASE_{i % 1000} + 0x{i:x})\n");
        h.Append("#endif\n");
        File.WriteAllText(Path.Combine(tree, "big.h"), h.ToString());
        var cmm = new StringBuilder();
        for (var i = 0; i < 150_000; i++) cmm.Append($"Data.Set KQ_DBG_SYM{i} %Long 0x{i:x}\n");
        File.WriteAllText(Path.Combine(tree, "probe.cmm"), cmm.ToString());
        var rnd = new Random(7);
        for (var f = 0; f < 2_000; f++)
        {
            var sb = new StringBuilder($"{UsedMacros.CwdPrefix}/build\n# 0 \"u{f}.c\"\n");
            for (var n = 0; n < 2_000; n++) sb.Append($"#define KQ_BLK{n % 97}_REG{rnd.Next(Defines / 10)}_FIELD_SHFT x\n");
            sb.Append(UsedMacros.OkLine).Append('\n');
            File.WriteAllText(Path.Combine(du, $"{f}.du"), sb.ToString());
        }
        var inputBytes = Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var at = GC.GetTotalAllocatedBytes(precise: true);
        var um = UsedMacros.Read(new[] { du });
        var res = HeaderCarver.CarveFromUsed(tree, new[] { "big.h" }, um.Names);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - at;
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        var perByte = allocated / (double)inputBytes;
        output.WriteLine($"{allocated >> 20} MB allocated for {inputBytes >> 20} MB of input ({perByte:N1} per byte); "
            + $"{um.Names.Count:N0} names, {res.DefinesKept:N0} kept, {res.DefinesDropped:N0} dropped");
        Assert.True(res.DefinesDropped > Defines / 2, $"only {res.DefinesDropped:N0} dropped");
        Assert.True(perByte <= MaxAllocatedPerInputByte, $"{perByte:N1} bytes allocated per input byte (at most {MaxAllocatedPerInputByte})");
    }
}
