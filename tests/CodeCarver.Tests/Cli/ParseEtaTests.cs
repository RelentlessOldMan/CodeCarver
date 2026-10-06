using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>The parse ETA (work eval, 1.0.159: "ETA ~4406h" at file 1 of a ~1-2 h carve).</summary>
public sealed class ParseEtaTests
{
    // A run that opens with 40 s of slow big parses (10 files, 2 MB) and then parses steadily: 120 files and
    // 3 MB per second. 12,500 files / 150 MB in total, so the true remaining time after the opening is ~100 s.
    static (int Files, long Bytes) At(double t) => t <= 40
        ? ((int)(t / 4), (long)(t / 40 * 2_000_000))
        : (10 + (int)((t - 40) * 120), 2_000_000 + (long)((t - 40) * 3_000_000));

    const int Files = 12_500;
    const long Bytes = 150_000_000;

    [Fact]
    public void NoNumber_DuringWarmup()
    {
        var eta = new ParseEta();
        for (var t = 3.0; t < ParseEta.WarmupSeconds; t += 3)
        {
            var (f, b) = At(t);
            Assert.Equal(-1, eta.Update(t, f, Files, b, Bytes).EtaSeconds);
        }
    }

    [Fact]
    public void SlowOpening_StopsSkewingTheEstimate_OnceItLeavesTheWindow()
    {
        var eta = new ParseEta();
        double last = -1;
        for (var t = 3.0; t <= 40 + ParseEta.WindowSeconds + 3; t += 3)
        {
            var (f, b) = At(t);
            if (f >= Files || b >= Bytes) break;
            last = eta.Update(t, f, Files, b, Bytes).EtaSeconds;
        }
        // The window now holds only steady parsing; the truth is whichever of files and bytes runs out last.
        var (fNow, bNow) = At(103);
        var trueRemaining = Math.Max((Files - fNow) / 120.0, (Bytes - bNow) / 3_000_000.0);
        Assert.InRange(last, trueRemaining * 0.5, trueRemaining * 2);
    }

    [Fact]
    public void NeverPrintsAnAbsurdEstimate_AfterWarmup()
    {
        // The old estimator printed thousands of hours here; any shown number must stay within 10x of the truth.
        var eta = new ParseEta();
        for (var t = 3.0; t < 140; t += 3)
        {
            var (f, b) = At(t);
            if (f >= Files || b >= Bytes) break;
            var e = eta.Update(t, f, Files, b, Bytes).EtaSeconds;
            if (e < 0) continue;
            var trueRemaining = Math.Max((Files - f) / 120.0, (Bytes - b) / 3_000_000.0) + Math.Max(0, 40 - t);
            Assert.True(e <= trueRemaining * 10 + 60, $"t={t}: ETA {e:N0}s vs ~{trueRemaining:N0}s");
        }
    }

    [Fact]
    public void FinalUpdate_ReportsZero()
    {
        var eta = new ParseEta();
        Assert.Equal(0, eta.Update(1, 10, 10, 100, 100).EtaSeconds);
    }
}
