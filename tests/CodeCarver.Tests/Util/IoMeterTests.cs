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
    public void BufferedReads_DecodeExactlyAsFileDoes()
    {
        var dir = Directory.CreateTempSubdirectory("iometer-");
        try
        {
            var big = string.Concat(Enumerable.Range(0, 60_000).Select(i => $"#define MODK_R{i} {i}\r\n")) + "last\rlone\nend";
            var cases = new (string Name, byte[] Bytes)[]
            {
                ("utf8.h", System.Text.Encoding.UTF8.GetBytes("int a; /* é */\nint b;\n")),
                ("bom8.h", System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("int c;\r\n")).ToArray()),
                ("utf16.h", System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("int d;\n")).ToArray()),
                ("latin.h", new byte[] { (byte)'x', 0xE9, (byte)'\n' }),
                ("empty.h", Array.Empty<byte>()),
                ("big.h", System.Text.Encoding.UTF8.GetBytes(big)),   // over the 1 MB buffer
            };
            foreach (var (name, bytes) in cases)
            {
                var p = Path.Combine(dir.FullName, name);
                File.WriteAllBytes(p, bytes);
                Assert.Equal(File.ReadAllText(p), IoMeter.ReadAllText(p));
                Assert.Equal(File.ReadLines(p).ToList(), IoMeter.ReadLines(p).ToList());
                Assert.Equal(File.ReadLines(p, System.Text.Encoding.Latin1).ToList(), IoMeter.ReadLines(p, System.Text.Encoding.Latin1).ToList());
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void LargeLatin1Reads_DecodeExactlyAsFileDoes()
    {
        var dir = Directory.CreateTempSubdirectory("iometer-");
        try
        {
            // Over the 4 MB pooled read: decoded straight into the string, unless a byte-order mark says otherwise.
            var body = Enumerable.Range(0, 5_000_000).Select(i => (byte)(i % 7 == 0 ? 0xE9 : i % 50 == 0 ? '\n' : 'a' + i % 26)).ToArray();
            var cases = new (string Name, byte[] Bytes)[]
            {
                ("plain.h", body),
                ("bom8.h", System.Text.Encoding.UTF8.GetPreamble().Concat(body).ToArray()),
                ("utf16.h", System.Text.Encoding.Unicode.GetPreamble().Concat(body).ToArray()),
            };
            foreach (var (name, bytes) in cases)
            {
                var p = Path.Combine(dir.FullName, name);
                File.WriteAllBytes(p, bytes);
                Assert.Equal(File.ReadAllText(p, System.Text.Encoding.Latin1), IoMeter.ReadAllText(p, System.Text.Encoding.Latin1));
            }
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

    [Fact]
    public void AFileReadAgain_IsCountedAsARepeat_WithItsBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-repeat-" + Guid.NewGuid().ToString("N") + ".c");
        File.WriteAllText(path, new string('x', 3 << 20));
        try
        {
            var before = IoMeter.Now();
            IoMeter.ReadAllText(path);
            var once = IoMeter.Now();
            IoMeter.ReadAllText(path);
            var twice = IoMeter.Now();
            var first = IoMeter.ReadSince(once, before).ToDictionary(x => x.Key, x => x.Value);
            var second = IoMeter.ReadSince(twice, once).ToDictionary(x => x.Key, x => x.Value);
            // Other tests read at the same time: lower bounds only.
            Assert.True(first["read.mb"] >= 3);
            Assert.True(second["read.repeat.count"] >= 1);
            Assert.True(second["read.repeat.mb"] >= 3);
        }
        finally { File.Delete(path); }
    }
}
