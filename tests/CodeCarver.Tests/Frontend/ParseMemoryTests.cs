using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// Parses wait for room in the memory budget: the parses running at once never count more than the budget, a file
/// bigger than the whole budget still runs (alone), and small files are never held.
/// </summary>
public sealed class ParseMemoryTests
{
    const int Big = 1_000_000;   // chars: counted as Big * BytesPerChar bytes

    [Fact]
    public void SecondBigParse_WaitsUntilTheFirstGivesBack()
    {
        var gate = new ParseMemory(Big * ParseMemory.BytesPerChar * 3 / 2);
        var first = gate.Acquire(Big);
        var second = Task.Run(() => gate.Acquire(Big));
        Assert.False(second.Wait(200));
        first.Release();
        Assert.True(second.Wait(5000));
        second.Result.Release();
    }

    [Fact]
    public void FileBiggerThanTheBudget_RunsAlone()
    {
        var gate = new ParseMemory(Big * ParseMemory.BytesPerChar / 10);
        var huge = gate.Acquire(Big);              // more than the budget: still runs
        var next = Task.Run(() => gate.Acquire(Big));
        Assert.False(next.Wait(200));
        huge.Release();
        Assert.True(next.Wait(5000));
        next.Result.Release();
    }

    [Fact]
    public void SmallFiles_AreNeverHeld()
    {
        var gate = new ParseMemory(1);
        var big = gate.Acquire(Big);
        var small = Task.Run(() => gate.Acquire(ParseMemory.MinChars - 1));
        Assert.True(small.Wait(5000));
        big.Release();
    }

    [Fact]
    public void ReleaseTwice_GivesBackOnce()
    {
        var gate = new ParseMemory(Big * ParseMemory.BytesPerChar * 3 / 2);
        var a = gate.Acquire(Big);
        a.Release();
        a.Release();                               // would leave room for two if counted twice
        var b = gate.Acquire(Big);
        var c = Task.Run(() => gate.Acquire(Big));
        Assert.False(c.Wait(200));
        b.Release();
        Assert.True(c.Wait(5000));
        c.Result.Release();
    }
}
