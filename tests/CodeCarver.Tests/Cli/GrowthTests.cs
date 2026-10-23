using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Tests.Preprocess;
using Xunit;
using Xunit.Abstractions;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// T1 growth (docs/TESTING.md): every step on one generated tree at N and 4N. A step whose time or memory grows
/// more than 6x is worse than linear (linear is 4x, quadratic 16x). Two header-carve bugs took hours on a real tree
/// and seconds on ours: a time limit at one size can't see how a step grows; this can, for every step at once.
/// Every dimension grows with N: C files, functions, calls, include chain, assembly, the big header's defines,
/// paste macros and forwarding macros, so a step that is a product of any two of them shows as 16x.
/// </summary>
[Collection(nameof(TemplateWalkMemoryTests))]   // measures time and memory: no other memory test at once
public sealed class GrowthTests(ITestOutputHelper output)
{
    const double MaxGrowth = 6.0;
    const long MsFloor = 300;    // a step under this at 4N is noise, not growth
    const long MbFloor = 64;
    // CPU time is counted in scheduler ticks over every thread: a step of 60-90 ms at N reads 30 ms either way on a
    // shared runner (CI: 60 -> 380, 94 -> 578, both linear locally). A quadratic step still fails: 16x of 60 is 960.
    const long MsSlack = 150;

    [Fact]
    public void EveryStep_GrowsAtMostLinearly()
    {
        var small = Measure(1);
        var big = Measure(4);
        var checkedKeys = 0;
        var fails = new List<string>();
        foreach (var (key, b) in big)
        {
            var ms = key.EndsWith("ms", StringComparison.OrdinalIgnoreCase);
            var floor = ms ? MsFloor : MbFloor;
            if (b < floor) continue;
            checkedKeys++;
            var s = Math.Max(small.GetValueOrDefault(key), floor / 8);   // a step near 0 at N: its 4N value is what counts
            var g = (double)b / s;
            output.WriteLine($"{key}: {s} -> {b} ({g:N1}x)");
            if (b > MaxGrowth * s + (ms ? MsSlack : 0)) fails.Add($"{key}: {s} -> {b} ({g:N1}x)");
        }
        Assert.True(checkedKeys >= 3, $"only {checkedKeys} steps were big enough to measure: grow the tree");
        Assert.True(fails.Count == 0, "worse than linear from N to 4N:\n" + string.Join("\n", fails));
    }

    /// <summary>Each step's time (ms) and allocated and live memory (MB), the smaller of two carves.</summary>
    static Dictionary<string, long> Measure(int n)
    {
        Dictionary<string, long>? best = null;
        for (var run = 0; run < 2; run++)
        {
            var got = Carve(n);
            if (best is null) { best = got; continue; }
            foreach (var (k, v) in got) best[k] = best.TryGetValue(k, out var o) ? Math.Min(o, v) : v;
        }
        return best!;
    }

    // CPU time per step and per part of the header carve (all threads: wall time on a busy machine, or summed over waiting
    // threads, is noise), and each step's (and part's) allocated, live and peak memory. The carve runs in a process of its
    // own, so these are its alone.
    static readonly Regex Measured = new(@"(?m)^((?:stage\d+\.)?(?:(?:roots\.)?cpu\.[\w.+-]+\.ms|headerCarve\.\w+\.(?:cpuMs|liveMb)|mem\.[\w.+-]+\.(?:allocatedMb|liveMb|peakMb))) = (\d+)\r?$");

    static Dictionary<string, long> Carve(int n)
    {
        using var t = new TreeCarve();
        Generate(t, n);
        var r = t.CarveInProcessOfItsOwn("[stages.safe]\n[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n"
            + "[advanced]\nmaxParseBytes = 100000\nmemoryStopMb = 4000\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(Path.Combine(t.Root, "out")))
        {
            var p = Path.Combine(dir, "codecarver", "summary.txt");
            if (!File.Exists(p)) continue;
            foreach (Match m in Measured.Matches(File.ReadAllText(p)))
                d[m.Groups[1].Value] = Math.Max(d.GetValueOrDefault(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
        }
        return d;
    }

    /// <summary>A firmware-shaped tree, every dimension times <paramref name="n"/>.</summary>
    static void Generate(TreeCarve t, int n)
    {
        int files = 250 * n, funcs = 12, chain = 16 * n, regs = 40_000 * n, pasters = 4_000 * n, forwarders = 4_000 * n;

        // An include chain every file pulls in.
        for (var i = 0; i < chain; i++)
            t.W($"inc/c{i}.h", (i + 1 < chain ? $"#include \"c{i + 1}.h\"\n" : "") + $"#define CHAIN_{i} {i}\nint chain_fn_{i}(int);\n");

        // The big generated header: register defines, paste macros, forwarders to them, defines built by pastes.
        var h = new StringBuilder("#ifndef REGS_H\n#define REGS_H\n");
        for (var i = 0; i < regs; i++) h.Append($"#define BLK{i % 97}_R{i}_OFS 0x{i * 4:X}u\n");
        for (var i = 0; i < pasters; i++) h.Append($"#define PST{i}(a, b) a##_##b##_P{i}\n");
        for (var i = 0; i < forwarders; i++) h.Append($"#define FWD{i}(x) PST{i}(x, 1)\n");
        for (var i = 0; i < pasters; i++) h.Append($"#define U{i}_1_P{i} {i}\n#define V{i}_2_P{i} {i}\n");
        h.Append("#endif\n");
        t.W("inc/regs.h", h.ToString());

        // C files calling each other in a ring, each using a register, a paste through a forwarder, the chain.
        for (var f = 0; f < files; f++)
        {
            var c = new StringBuilder("#include \"../inc/c0.h\"\n#include \"../inc/regs.h\"\n");
            for (var j = 0; j < funcs; j++) c.Append($"int f{(f + 1) % files}_{j}(int);\n");
            for (var j = 0; j < funcs; j++)
            {
                var r = (f * funcs + j) % regs;
                c.Append($"int f{f}_{j}(int x)\n{{\n    if (x <= 0) return BLK{r % 97}_R{r}_OFS + CHAIN_{f % chain};\n");
                if (j % 3 == 0) c.Append($"    x += FWD{(f * funcs + j) % forwarders}(U{(f * funcs + j) % forwarders});\n");
                c.Append($"    return f{(f + 1) % files}_{j}(x - 1) + chain_fn_{j % chain}(x);\n}}\n");
                c.Append($"static int unused_{f}_{j}(int x) {{ return x * {j}; }}\n");
            }
            t.W($"drv/f{f}.c", c.ToString()).Compile($"drv/f{f}.c");
        }
        var cm = new StringBuilder("#include \"inc/c0.h\"\n");
        for (var i = 0; i < chain; i++) cm.Append($"int chain_fn_{i}(int x) {{ return x + CHAIN_{i}; }}\n");
        t.W("chain.c", cm.ToString()).Compile("chain.c");

        // Assembly calling into C.
        var s = new StringBuilder("    .text\n    .global start\nstart:\n");
        for (var f = 0; f < files; f++) s.Append($"    bl f{f}_0\n");
        t.W("startup.S", s.ToString()).Compile("startup.S");

        t.W("main.c", "int f0_0(int);\nint main(void) { return f0_0(3); }\n").Compile("main.c");
    }
}
