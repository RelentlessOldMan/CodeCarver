using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Core;

/// <summary>
/// <see cref="ByteGate"/>: work running at once never holds more than the budget, except one piece bigger than the whole
/// budget, which runs alone; small pieces don't wait.
/// </summary>
public sealed class ByteGateTests
{
    [Fact]
    public void HeldNeverPassesTheBudget_AndAnOversizedPieceRunsAlone()
    {
        var gate = new ByteGate(100);
        long held = 0, most = 0, oversizedWithOthers = 0;
        var sizes = Enumerable.Range(0, 400).Select(i => i % 50 == 0 ? 250L : 10L + i % 40).ToArray();
        Parallel.ForEach(sizes, new ParallelOptions { MaxDegreeOfParallelism = 32 }, size =>
        {
            using (gate.Take(size))
            {
                var now = Interlocked.Add(ref held, size);
                if (size > 100 && now != size) Interlocked.Increment(ref oversizedWithOthers);
                if (size <= 100) { long m; while ((m = Interlocked.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, m) != m) { } }
                Thread.SpinWait(2000);
                Interlocked.Add(ref held, -size);
            }
        });
        Assert.Equal(0, oversizedWithOthers);
        Assert.InRange(most, 1, 100);
        Assert.Equal(0, held);
    }

    [Fact]
    public void SmallPieces_DoNotWait()
    {
        var gate = new ByteGate(1000);
        Parallel.For(0, 50, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => { using (gate.Take(10)) Thread.SpinWait(1000); });
        Assert.Equal(0, gate.Waits);
        using (gate.Take(0)) { }   // nothing taken: nothing to give back
    }

    [Fact]
    public void AWaitingPiece_IsSizedAgainWhenItGoes()
    {
        // The template walks: before the first one ends the size is unknown and each takes the whole budget; one sized once
        // would then hold it all too, and the walks waiting for the first ran one at a time.
        var gate = new ByteGate(100);
        long size = -1, peers = 0, most = 0;
        var first = gate.Take(() => Interlocked.Read(ref size) is var s and >= 0 ? s : 100);
        var waiting = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            using (gate.Take(() => Interlocked.Read(ref size) is var s and >= 0 ? s : 100))
            {
                var now = Interlocked.Increment(ref peers);
                for (long m; (m = Interlocked.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, m) != m;) { }
                Thread.Sleep(200);
                Interlocked.Decrement(ref peers);
            }
        })).ToArray();
        foreach (var t in waiting) t.Start();
        SpinWait.SpinUntil(() => gate.Waits == 8, 5000);
        Interlocked.Exchange(ref size, 10);   // the first one measured itself
        first.Dispose();
        foreach (var t in waiting) t.Join();
        Assert.Equal(8, gate.Waits);
        Assert.True(most > 1, most.ToString());   // sized again: they ran together, not one at a time
    }
}
