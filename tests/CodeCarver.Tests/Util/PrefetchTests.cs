using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>ReadAhead.Prefetch: results in order, and no more held ahead of the caller than the weight bound allows.</summary>
public class PrefetchTests
{
    [Fact]
    public void InOrder_AndHoldsNoMoreThanItsWeight()
    {
        var items = Enumerable.Range(0, 500).ToList();
        long held = 0, maxHeld = 0;
        var gate = new object();
        var seen = new List<int>();
        foreach (var (item, value) in ReadAhead.Prefetch(items, i =>
                 {
                     lock (gate) { held += 10; maxHeld = Math.Max(maxHeld, held); }
                     return i * 2;
                 }, _ => 10, maxWeight: 100))
        {
            Assert.Equal(item * 2, value);
            seen.Add(item);
            Thread.Sleep(item % 50 == 0 ? 5 : 0);
            lock (gate) held -= 10;
        }
        Assert.Equal(items, seen);
        // Past the next item, reads start only while under 100 held; each of the Window threads may have one in hand.
        Assert.True(maxHeld <= 100 + 10 * ReadAhead.Window, $"held {maxHeld}");
    }

    [Fact]
    public void AnItemHeavierThanTheBound_StillComesThrough()
    {
        var items = new[] { 1, 2, 3 };
        var got = ReadAhead.Prefetch(items, i => i, _ => 1_000, maxWeight: 10).Select(x => x.Value).ToList();
        Assert.Equal(items, got);
    }

    [Fact]
    public void AFailedRead_Throws()
    {
        var items = Enumerable.Range(0, 20).ToList();
        Assert.Throws<InvalidOperationException>(() =>
            ReadAhead.Prefetch(items, i => i == 7 ? throw new InvalidOperationException() : i, _ => 1, 100).ToList());
    }
}
