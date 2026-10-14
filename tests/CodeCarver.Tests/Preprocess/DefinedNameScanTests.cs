using System.Text;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The big-header scan reads a file in pieces at once and matches lines by hand: it must find exactly the names the
/// regex finds reading the file line by line, wherever the pieces break and whatever the line endings and encoding.
/// </summary>
public sealed class DefinedNameScanTests
{
    static HashSet<string> ByLines(string path, HashSet<string> wanted)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var m = DefinedNameScan.DirectiveLine.Match(line);
            if (m.Success && wanted.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    static void AssertSame(byte[] content, HashSet<string> wanted)
    {
        var path = Path.Combine(Path.GetTempPath(), "codecarver-dns-" + Guid.NewGuid().ToString("N") + ".h");
        File.WriteAllBytes(path, content);
        try
        {
            var expected = ByLines(path, wanted);
            foreach (var piece in new[] { 1 << 20, 7, 16, 61, 256 })
            {
                var got = new DefinedNameScan(wanted) { PieceBytes = piece, PiecesAtOnce = 4 }.Read(path);
                Assert.NotNull(got);
                Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), got!.Distinct().OrderBy(x => x, StringComparer.Ordinal));
            }
        }
        finally { File.Delete(path); }
    }

    static readonly HashSet<string> Wanted = new(StringComparer.Ordinal)
    {
        "MODK_A", "MODK_B", "MODK_C", "MODK_D", "MODK_E", "MODK_F", "MODK_G", "MODK_H", "MODK_I", "MODK_J",
        "MODK_LONG_NAME_THAT_CROSSES_A_PIECE_BOUNDARY_0123456789", "MODK_É", "MODK_NBSP", "MODK_LAST",
    };

    const string Lines =
        "#define MODK_A 1\n"
        + "  #  define\tMODK_B(x) x\n"
        + "#undef MODK_C\n"
        + "%:define MODK_D\n"
        + "??=define MODK_E 2\n"
        + "#defineMODK_F\n"               // no space: not a define
        + "#definex MODK_F\n"
        + "#undefine MODK_F\n"
        + "// #define MODK_F\n"
        + "#define MODK_Gx 1\n"           // a longer name: not MODK_G
        + "\v\f#define MODK_H\n"
        + "#define MODK_I\r\n"
        + "#define MODK_J\r"              // CR alone ends a line
        + "#define MODK_LONG_NAME_THAT_CROSSES_A_PIECE_BOUNDARY_0123456789 1\n"
        + "#define MODK_É 1\n"            // a non-ASCII name: the regex's \w decides
        + "#define MODK_NBSP 1\n"    // a non-ASCII space
        + "#define MODK_NOTWANTED 1\n"
        + "#\n%\n??\n#def\n"
        + "#define MODK_LAST";            // no newline at the end

    [Fact]
    public void FindsWhatTheRegexFinds_LineByLine_WhereverThePiecesBreak()
    {
        AssertSame(Encoding.UTF8.GetBytes(Lines), Wanted);
        AssertSame(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Lines + "\n", 50))), Wanted);
    }

    [Fact]
    public void ByteOrderMarks_AreReadAsTheLineReaderReadsThem()
    {
        AssertSame(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Lines)).ToArray(), Wanted);
        AssertSame(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Lines)).ToArray(), Wanted);
        AssertSame(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Lines)).ToArray(), Wanted);
    }

    [Fact]
    public void RandomLines_MatchTheRegex()
    {
        var rng = new Random(7);
        string[] parts = { "#", "%:", "??=", " ", "\t", "define", "undef", "MODK_A", "MODK_B", "x", "\r", "\n", "\r\n", "é", " ", "(", "1" };
        for (var round = 0; round < 40; round++)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 400; i++) sb.Append(parts[rng.Next(parts.Length)]);
            AssertSame(Encoding.UTF8.GetBytes(sb.ToString()), Wanted);
        }
    }

    [Fact]
    public void AMissingFile_IsNull()
        => Assert.Null(new DefinedNameScan(Wanted).Read(Path.Combine(Path.GetTempPath(), "codecarver-none-" + Guid.NewGuid().ToString("N"))));
}
