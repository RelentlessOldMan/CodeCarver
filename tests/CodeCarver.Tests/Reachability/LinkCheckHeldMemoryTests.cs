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
        var root = Path.Combine(Path.GetTempPath(), "cc-held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "regs"));
        var was = Budgets.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            (string, string) W(string rel, string text) { var p = Path.Combine(root, rel); File.WriteAllText(p, text); return (rel, p); }
            // 400 register-map headers (~70 K chars each): each under the hand-on size.
            var regs = new List<(string, string)>();
            long chars = 0;
            for (var h = 0; h < 400; h++)
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

            // Little held past the header pass: no header text (HEADER_MB 0), 2 MB handed on.
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB", "0");
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_HANDON_MB", "2");
            static long Live() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            var baseline = Live();
            long afterHeaders = -1;
            var r = EmittedLinkCheck.Run(emitted, droppedFiles, report: (k, _) =>
            {
                // The header pass is done (its workers idle) and no scan has run: what is live now is held for the scans.
                if (k == "headersMs") afterHeaders = Live() - baseline;
            });
            Assert.Equal(0, r.Stats["scansOutsideBudget"]);
            Assert.InRange(r.Stats["held.handedOnMaxMb"], 0, 2);
            // The budgets (2 MB) and the per-header tables (names, sizes, hashes: a few hundred bytes a header). The scans
            // of these headers keep about a byte a char of definitions; 1.0.317 held those from here on.
            var limit = (2L << 20) + chars / 20;
            Assert.True(afterHeaders <= limit, $"{afterHeaders >> 10} KB held past the header pass (limit {limit >> 10} KB)");
            GC.KeepAlive(regs);
        }
        finally
        {
            for (var i = 0; i < Budgets.Length; i++) Environment.SetEnvironmentVariable(Budgets[i], was[i]);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
