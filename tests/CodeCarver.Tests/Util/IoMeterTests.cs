using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>
/// The I/O meter behind the watchdog: it must count and time each wait, show the ones still going with their age, and
/// say all of it with no path or name. It is one meter for the process and other tests use it at the same time, so
/// these look at differences and lower bounds only.
/// </summary>
public sealed class IoMeterTests
{
    [Fact]
    public void AWaitStillGoing_IsShownWithItsAge_ThenCounted()
    {
        var before = IoMeter.Now();
        var op = IoMeter.Start(IoMeter.Kind.Launch);
        Thread.Sleep(1100);
        var during = IoMeter.Now();
        Assert.True(during.Waiting[(int)IoMeter.Kind.Launch] >= 1);
        Assert.True(IoMeter.LongestWaitSeconds(during) >= 1);
        Assert.Contains(IoMeter.Describe(during, before), l => l.StartsWith("compiler runs:") && l.Contains("waiting now"));
        op.Dispose();
        var after = IoMeter.Now();
        Assert.True(after.Count[(int)IoMeter.Kind.Launch] - before.Count[(int)IoMeter.Kind.Launch] >= 1);
        Assert.True(after.MaxTicks[(int)IoMeter.Kind.Launch] > 0);
        Assert.True(after.DoneSince(before) >= 1);
    }

    [Fact]
    public void ReadLines_ThrowsAtTheCall_AndCountsTheRead()
    {
        var dir = Directory.CreateTempSubdirectory("iometer-");
        try
        {
            // As File.ReadLines: a missing file throws where it is called, inside the caller's try, not later at the loop.
            Assert.Throws<FileNotFoundException>(() => IoMeter.ReadLines(Path.Combine(dir.FullName, "absent.h")));
            var path = Path.Combine(dir.FullName, "lines.h");
            File.WriteAllText(path, "a\nb\nc\n");
            var before = IoMeter.Now();
            Assert.Equal(3, IoMeter.ReadLines(path).Count());
            var after = IoMeter.Now();
            Assert.True(after.Count[(int)IoMeter.Kind.Read] - before.Count[(int)IoMeter.Kind.Read] >= 1);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Reads_AreCounted_AndDescribedWithoutTheirPaths()
    {
        var dir = Directory.CreateTempSubdirectory("iometer-");
        try
        {
            var path = Path.Combine(dir.FullName, "secret_name.h");
            File.WriteAllText(path, "int secret_symbol;");
            var before = IoMeter.Now();
            Assert.Equal("int secret_symbol;", IoMeter.ReadAllText(path));
            IoMeter.Copy(path, path + ".copy", overwrite: true);
            foreach (var _ in SourceWalk.Entries(dir.FullName, null)) { }
            var now = IoMeter.Now();
            Assert.True(now.Count[(int)IoMeter.Kind.Read] - before.Count[(int)IoMeter.Kind.Read] >= 1);
            Assert.True(now.Count[(int)IoMeter.Kind.Copy] - before.Count[(int)IoMeter.Kind.Copy] >= 1);
            Assert.True(now.Count[(int)IoMeter.Kind.List] - before.Count[(int)IoMeter.Kind.List] >= 1);
            var text = string.Join("\n", IoMeter.Describe(now, before));
            Assert.Contains("file reads:", text);
            Assert.Contains("directory listings:", text);
            Assert.DoesNotContain("secret", text);
            Assert.DoesNotContain(dir.FullName, text);
            var totals = IoMeter.Totals(now, before).ToDictionary(t => t.Key, t => t.Value);
            Assert.True(totals["io.read.count"] >= 1);
            Assert.True(totals.ContainsKey("io.launch.maxMs"));
        }
        finally { dir.Delete(recursive: true); }
    }
}
