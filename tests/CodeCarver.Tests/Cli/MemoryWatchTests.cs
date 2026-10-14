using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Memory over a run: always measured (summary mem.run.*), and with <c>[advanced] memoryStopMb</c> the run stops when
/// its private memory passes the limit, leaving what it counted in codecarver/stopped-summary.txt (exit 4). A work run
/// ran its machine out of memory and left no numbers at all.
/// </summary>
[Collection("MemoryStop")]   // swaps CarveCommand.ExitProcess
public sealed class MemoryWatchTests
{
    static TreeCarve Tree() => new TreeCarve()
        .W("main.c", "int helper(void);\nint main(void) { return helper(); }\n")
        .W("helper.c", "int helper(void) { return 0; }\n")
        .Compile("main.c").Compile("helper.c");

    static string StopFile(TreeCarve t) => Path.Combine(t.Root, "out", "codecarver", CarveCommand.StoppedSummaryFile);

    [Fact]
    public void ByDefault_MemoryIsMeasured_AndTheRunIsNeverStopped()
    {
        using var t = Tree();
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Matches(@"mem\.run\.peakPrivateMb = [1-9]", t.Summary);
        Assert.Contains("mem.stopMb = 0", t.Summary);
        Assert.False(File.Exists(StopFile(t)));
    }

    [Fact]
    public void PastMemoryStopMb_TheRunStops_AndLeavesItsNumbers()
    {
        using var t = Tree();
        var exits = new List<int>();
        var was = CarveCommand.ExitProcess;
        CarveCommand.ExitProcess = code => { lock (exits) exits.Add(code); };
        try
        {
            var r = t.Carve(extraToml: "[advanced]\nmemoryStopMb = 1\n");
            Assert.Equal(new[] { CarveCommand.StoppedForMemoryExit }, exits);   // once, however many looks pass it
            Assert.Contains("STOPPED - private memory", r.Err);
            var stop = File.ReadAllText(StopFile(t));
            Assert.Contains("stop.reason = memoryStopMb", stop);
            Assert.Matches(@"stop\.privateMb = [1-9]", stop);
            Assert.Contains("stop.afterStep = ", stop);
            Assert.Contains("mem.stopMb = 1", stop);
            Assert.Contains("exitCode = 4", stop);
            Assert.DoesNotContain(t.Src.Replace('\\', '/'), stop.Replace('\\', '/'));   // numbers only
        }
        finally { CarveCommand.ExitProcess = was; }
    }

    [Fact]
    public void AStopFileFromAnEarlierRun_IsGoneWhenTheNextRunStarts()
    {
        using var t = Tree();
        Directory.CreateDirectory(Path.GetDirectoryName(StopFile(t))!);
        File.WriteAllText(Path.Combine(t.Root, "out", ".codecarver-output"), "CodeCarver output area.\n");
        File.WriteAllText(StopFile(t), "stop.reason = memoryStopMb\n");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.False(File.Exists(StopFile(t)));
    }

    [Fact]
    public void MemoryStopMb_Negative_IsAConfigError()
    {
        using var t = Tree();
        var r = t.Carve(extraToml: "[advanced]\nmemoryStopMb = -5\n");
        Assert.Equal(2, r.Code);
        Assert.Contains("memoryStopMb", r.Err);
    }
}
