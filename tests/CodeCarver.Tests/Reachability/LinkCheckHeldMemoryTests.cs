using System.Text;
using CodeCarver.Core.Reachability;
using CodeCarver.Tests.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Reachability;

/// <summary>
/// The link check holds past its header pass only what its budgets allow, never a part of the tree. 1.0.317 scanned
/// each small dropped header in the header pass, under the read budget (16 times the scan budget at once), and held
/// every scan until the dropped loop: a work run went from 13 GB to a stop at 21. A tree of small register-map
/// headers, the budgets made small: every scan must run under the scan budget, and when the header pass ends (the
/// check's report) a full collection measures what is held for the scans.
/// The bound is on what more headers add, not the total: the process holds about 10 MB the first check sets up whatever
/// the tree (buffers, caches, the 2 MB handed on), so a total bound failed on every run, the right code included.
/// </summary>
[Collection(nameof(TemplateWalkMemoryTests))]
public class LinkCheckHeldMemoryTests
{
    static readonly string[] Budgets = { "CODECARVER_CHECK_HEADER_MB", "CODECARVER_CHECK_HANDON_MB" };

    [Theory]
    [InlineData(true)]    // dropped: the 1.0.317 shape
    [InlineData(false)]   // emitted
    public void LinkCheck_HoldsBetweenLoops_ItsBudgets_NotTheTree(bool dropped)
    {
        var was = Budgets.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            // Little held past the header pass: no header text (HEADER_MB 0), 2 MB handed on.
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB", "0");
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_HANDON_MB", "2");
            Held(dropped, 200);   // what the first check sets up, whatever the tree
            // The live heap is the process's, so it is measured up to three times. Held scans add about 14 MB for the 200
            // more on every try, so the best of three still fails them. (Through 1.0.334 each check kept the one before it
            // live until it began, and freed it under this one's baseline: -2,582 KB at 200 on CI. See below.)
            var tries = new List<string>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (small, _, _) = Held(dropped, 200);
                var (large, chars, _) = Held(dropped, 400);
                // The per-header tables (names, sizes, hashes: a few hundred bytes a header) for the 200 more. The scans of
                // these headers keep about a byte a char of definitions (about 70 KB a header); 1.0.317 held those from here on.
                var limit = chars / 2 / 20;
                if (large - small <= limit) return;
                tries.Add($"{(large - small) >> 10} KB more (limit {limit >> 10} KB; {small >> 10} KB at 200, {large >> 10} KB at 400)");
            }
            Assert.Fail("200 more headers held more past the header pass on every try:\n" + string.Join("\n", tries));
        }
        finally
        {
            for (var i = 0; i < Budgets.Length; i++) Environment.SetEnvironmentVariable(Budgets[i], was[i]);
        }
    }

    /// <summary>
    /// The check holds nothing once it returns. A memory stop reads the last check's shape numbers, and the delegate that
    /// gave them shared the check's closure: it kept every table of the check live until the next check (5 MB more after
    /// 400 dropped register headers than after 200, all of it the dropped names). The caller's report is in that closure
    /// too, so a report that holds an object tells whether anything still holds the closure, with no heap noise.
    /// </summary>
    [Fact]
    public void LinkCheck_HoldsNothing_AfterItReturns()
    {
        var gone = CheckOnce();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(gone.IsAlive, "the check's closure is still live after it returned");
        Assert.NotEmpty(EmittedLinkCheck.ShapeSoFar());   // a memory stop still has the last check's shape
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference CheckOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string, string) W(string rel, string text) { var p = Path.Combine(root, rel); File.WriteAllText(p, text); return (rel, p); }
            var emitted = new List<(string, string)> { W("main.c", "#include \"r.h\"\nint main(void) { return 0; }\n") };
            var dropped = new List<(string, string)> { W("r.h", "#define HM_REG_A 1\nint hm_get(void) { return 0; }\n") };
            var sentinel = new object();
            EmittedLinkCheck.Run(emitted, dropped, report: (_, _) => GC.KeepAlive(sentinel));
            return new WeakReference(sentinel);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>What a check of <paramref name="headers"/> register-map headers holds past its header pass, and their chars.</summary>
    static (long Held, long Chars, long After) Held(bool dropped, int headers)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "regs"));
        try
        {
            (string, string) W(string rel, string text) { var p = Path.Combine(root, rel); File.WriteAllText(p, text); return (rel, p); }
            // Register-map headers (~70 K chars each): each under the hand-on size.
            var regs = new List<(string, string)>();
            long chars = 0;
            for (var h = 0; h < headers; h++)
            {
                var sb = new StringBuilder($"#ifndef HM_R{h}_H\n#define HM_R{h}_H\n");
                for (var k = 0; k < 1000; k++) sb.Append($"#define HM{h}_REG_{k:D5}_ADDR (HM{h}_BASE + 0x{k * 4:X}u)\n");
                // ...and accessors, which a scan keeps as definitions (a held scan of these is what 1.0.317 held).
                for (var k = 0; k < 200; k++)
                    sb.Append($"extern volatile unsigned hm{h}_shadow_{k:D4};\nstatic inline unsigned hm{h}_get_{k:D4}(void) {{ return hm{h}_shadow_{k:D4}; }}\n");
                sb.Append("#endif\n");
                chars += sb.Length;
                regs.Add(W($"regs/r{h}.h", sb.ToString()));
            }
            var emitted = new List<(string, string)> { W("main.c", "int main(void) { return 0; }\n") };
            if (!dropped) emitted.AddRange(regs);
            var droppedFiles = dropped ? regs : new List<(string, string)>();

            static long Live() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            var baseline = Live();
            long afterHeaders = -1;
            Dictionary<string, long> heldNow = new();
            var r = EmittedLinkCheck.Run(emitted, droppedFiles, report: (k, _) =>
            {
                // The header pass is done (its workers idle) and no scan has run: what is live now is held for the scans.
                if (k != "headersMs") return;
                afterHeaders = Live() - baseline;
                // A memory stop and the heartbeat read what the check says it holds while it runs.
                heldNow = CodeCarver.Core.Util.HeldMeter.Now().ToDictionary(kv => kv.Key, kv => kv.Value);
            });
            Assert.Equal(0, r.Stats["scansOutsideBudget"]);
            Assert.Equal(headers, heldNow["held.check.headerInfos"]);
            Assert.InRange(heldNow["held.check.handedOnMb"], 0, 2);
            Assert.DoesNotContain(CodeCarver.Core.Util.HeldMeter.Now(), kv => kv.Key.StartsWith("held.check.", StringComparison.Ordinal));   // gone when it ends
            Assert.InRange(r.Stats["held.handedOnMaxMb"], 0, 2);
            GC.KeepAlive(regs);
            r = null;
            return (afterHeaders, chars, Live() - baseline);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
