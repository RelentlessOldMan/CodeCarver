using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Tests.Preprocess;
using Xunit;
using Xunit.Abstractions;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// The header carve's fixpoint reads the big headers once a pass. Work's 1.0.325 max stage stopped at its 20 GB memory
/// stop in pass 1: every line was copied four times as it was read, every wanted define was split up for paste-macro
/// calls it couldn't make, and each define's name and edge names were held once more beside the names already needed.
/// On a 161 MB paste-macro tree the fixpoint allocated 6.1 GB for two passes (19 MB per MB of header per pass); now
/// about 9. On this tree 11.1 before, 5.1 now. The carve runs in a process of its own, so what it allocates is its alone.
/// </summary>
[Collection(nameof(TemplateWalkMemoryTests))]   // measures memory: no other memory test at once
public sealed class HeaderCarveMemoryTests(ITestOutputHelper output)
{
    const double MaxAllocatedPerHeaderMbPerPass = 8;

    static readonly Regex Key = new(@"(?m)^stage\d+\.headerCarve\.(fixpoint\.allocatedMb|bytesBefore|passes) = (\d+)\r?$");

    [Fact]
    public void Fixpoint_AllocatesLittlePerHeaderByte()
    {
        using var t = new TreeCarve();
        Generate(t);
        var r = t.CarveInProcessOfItsOwn("[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n"
            + "[advanced]\nmemoryStopMb = 4000\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(Path.Combine(t.Root, "out")))
        {
            var p = Path.Combine(dir, "codecarver", "summary.txt");
            if (!File.Exists(p)) continue;
            foreach (Match m in Key.Matches(File.ReadAllText(p))) d[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
        }
        Assert.True(d.Count == 3, "no header carve keys:\n" + string.Join("\n", d.Select(kv => $"{kv.Key} = {kv.Value}")));
        var headerMb = d["bytesBefore"] / (double)(1 << 20);
        var perPass = d["fixpoint.allocatedMb"] / headerMb / d["passes"];
        output.WriteLine($"{d["fixpoint.allocatedMb"]} MB allocated, {headerMb:N1} MB of headers, {d["passes"]} passes: {perPass:N1} MB per MB per pass");
        Assert.True(headerMb >= 16, $"only {headerMb:N1} MB of big headers: grow the tree");
        Assert.True(perPass <= MaxAllocatedPerHeaderMbPerPass,
            $"the fixpoint allocated {perPass:N1} MB per MB of header per pass (at most {MaxAllocatedPerHeaderMbPerPass})");
    }

    /// <summary>Big headers of paste macros, forwarders to them, register defines (some whose names a paste argument
    /// expands) and struct lines; C files calling the paste macros.</summary>
    static void Generate(TreeCarve t)
    {
        const int headers = 4, pasters = 24_000, forwarders = 24_000, regs = 220_000, structs = 40_000, files = 60;
        for (var hd = 0; hd < headers; hd++)
        {
            var h = new StringBuilder($"#ifndef GEN{hd}_H\n#define GEN{hd}_H\n");
            for (var i = hd; i < pasters; i += headers) h.Append($"#define HWP_{i}_FIELD(blk, n) HWB_##blk##_R##n##_P{i}_VAL\n");
            for (var i = hd; i < forwarders; i += headers) h.Append($"#define HWF_{i}_GET(base, n) HWP_{i}_FIELD(base, n) | HWX_{i}_MASK\n");
            for (var i = hd; i < regs; i += headers) h.Append($"#define HWR_BLOCK{i % 509}_REG{i}_OFFSET (HWR_BASE{i % 61} + 0x{i * 4:x}u)\n");
            for (var i = hd; i < pasters; i += headers) h.Append($"#define UNIT{i % 97} UNIT_ALIAS{i % 97}\n");
            for (var i = hd; i < structs; i += headers) h.Append($"    unsigned hws_{i}_fld_a, hws_{i}_fld_b; /* field {i} */\n");
            h.Append("#endif\n");
            t.W($"inc/gen{hd}.h", h.ToString());
        }
        var inc = string.Concat(Enumerable.Range(0, headers).Select(hd => $"#include \"../inc/gen{hd}.h\"\n"));
        var main = new StringBuilder();
        for (var f = 0; f < files; f++)
        {
            var c = new StringBuilder(inc).Append($"int fn_{f}(void)\n{{\n    int s = 0;\n");
            for (var j = 0; j < 30; j++) c.Append($"    s += HWF_{(f * 30 + j) % forwarders}_GET(UNIT{(f + j) % 97}, {j % 8});\n");
            c.Append($"    s += HWR_BLOCK{f % 509}_REG{f}_OFFSET;\n    return s;\n}}\n");
            t.W($"drv/f{f}.c", c.ToString()).Compile($"drv/f{f}.c");
            main.Append($"int fn_{f}(void);\n");
        }
        main.Append("int main(void)\n{\n    int s = 0;\n")
            .Append(string.Concat(Enumerable.Range(0, files).Select(f => $"    s += fn_{f}();\n"))).Append("    return s;\n}\n");
        t.W("main.c", main.ToString()).Compile("main.c");
    }
}
