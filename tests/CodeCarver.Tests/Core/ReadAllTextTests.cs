using System.Text;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Core;

/// <summary>
/// <see cref="IoMeter.ReadAllText(string, Encoding?)"/> reads a small file through a pooled buffer and decodes it once:
/// it gives the text the StreamReader it replaces gave (byte-order marks of every kind, invalid UTF-8, a mark cut short,
/// empty files, an encoding passed in), and a file over the pooled size still reads whole.
/// </summary>
public sealed class ReadAllTextTests
{
    static string Reference(string path, Encoding? encoding)
    {
        using var r = new StreamReader(path, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    static readonly byte[][] Marks =
    {
        new byte[0], new byte[] { 0xEF, 0xBB, 0xBF }, new byte[] { 0xFF, 0xFE }, new byte[] { 0xFE, 0xFF },
        new byte[] { 0xFF, 0xFE, 0, 0 }, new byte[] { 0, 0, 0xFE, 0xFF }, new byte[] { 0xEF, 0xBB }, new byte[] { 0xFF },
    };

    [Fact]
    public void RandomFiles_ReadAsTheStreamReaderReadThem()
    {
        var rng = new Random(7);
        var dir = Directory.CreateTempSubdirectory("cc-read-").FullName;
        try
        {
            var path = Path.Combine(dir, "f");
            for (var i = 0; i < 3000; i++)
            {
                var mark = Marks[rng.Next(Marks.Length)];
                var body = new byte[rng.Next(i % 50 == 0 ? 300_000 : 64)];
                for (var k = 0; k < body.Length; k++)
                    body[k] = (byte)(rng.Next(4) switch { 0 => rng.Next(256), 1 => 0x0A, _ => 0x61 + rng.Next(26) });
                File.WriteAllBytes(path, mark.Concat(body).ToArray());
                var enc = (i % 3) switch { 0 => null, 1 => Encoding.Latin1, _ => Encoding.Unicode };
                Assert.Equal(Reference(path, enc), IoMeter.ReadAllText(path, enc));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LargeFile_ReadsWhole()
    {
        var dir = Directory.CreateTempSubdirectory("cc-read-").FullName;
        try
        {
            var path = Path.Combine(dir, "big.h");
            var line = "#define REG_" + new string((char)0x41, 60) + " 1\n";
            File.WriteAllText(path, string.Concat(Enumerable.Repeat(line, 6 << 20 >> 6)));
            Assert.Equal(Reference(path, null), IoMeter.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
