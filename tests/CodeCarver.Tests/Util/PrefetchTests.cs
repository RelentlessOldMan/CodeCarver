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

    [Fact]
    public void OrderedStream_InOrder_AndASlowItemLeavesTheOthersWorking()
    {
        // Item 0 waits until items 1..6 are done: with a handout that waits on its oldest item it would never finish.
        var items = Enumerable.Range(0, 200).ToList();
        var others = new CountdownEvent(6);
        var waits = new ReadAhead.StreamWaits();
        var got = ReadAhead.OrderedStream(items, i =>
        {
            if (i == 0) Assert.True(others.Wait(TimeSpan.FromSeconds(30)), "the other threads sat idle behind item 0");
            else if (i <= 6) others.Signal();
            return i * 3;
        }, workers: 4, ahead: 32, _ => 1, maxWeight: 1_000, waits).ToList();
        Assert.Equal(items, got.Select(x => x.Item));
        Assert.Equal(items.Select(i => i * 3), got.Select(x => x.Value));
    }

    [Fact]
    public void OrderedStream_HoldsNoMoreThanItsBounds()
    {
        var items = Enumerable.Range(0, 300).ToList();
        int started = 0, consumed = 0, maxAhead = 0;
        foreach (var _ in ReadAhead.OrderedStream(items, i =>
                 {
                     var s = Interlocked.Increment(ref started);
                     InterlockedMax(ref maxAhead, s - Volatile.Read(ref consumed));
                     return i;
                 }, workers: 4, ahead: 8, _ => 1, maxWeight: 1_000))
        {
            Interlocked.Increment(ref consumed);
            Thread.Sleep(1);
        }
        // At most ahead past the caller, plus one item in hand per other thread.
        Assert.True(maxAhead <= 8 + 4, $"ahead {maxAhead}");
    }

    [Fact]
    public void OrderedStream_AFailedItem_Throws_AndEmptyIsEmpty()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReadAhead.OrderedStream(Enumerable.Range(0, 50), i => i == 9 ? throw new InvalidOperationException() : i,
                                    4, 16, _ => 1, 100).ToList());
        Assert.Empty(ReadAhead.OrderedStream(Enumerable.Empty<int>(), i => i, 4, 16, _ => 1, 100));
    }

    static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
