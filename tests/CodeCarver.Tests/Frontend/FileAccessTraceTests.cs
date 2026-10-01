using System.Text.RegularExpressions;
using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// The file-access trace reader is deliberately format-TOLERANT: it extracts candidate paths from ProcMon CSV,
/// strace, or a plain path-per-line list without per-tool parsers, and leaves "is this a real repo file" to the
/// caller's under-root + existence filter. These pin that extraction (quoted tokens, whole-line fallback, a
/// custom regex) and the de-dup.
/// </summary>
public sealed class FileAccessTraceTests
{
    [Fact]
    public void Paths_PlainList_OnePerLine()
    {
        var text = "src/main.c\r\ninc/types.h\r\n\r\nflash.ld\r\n";
        var paths = FileAccessTrace.Paths(text);
        Assert.Contains("src/main.c", paths);
        Assert.Contains("inc/types.h", paths);
        Assert.Contains("flash.ld", paths);
        Assert.Equal(3, paths.Count);   // blank line ignored
    }

    [Fact]
    public void Paths_ProcMonCsv_ExtractsQuotedPathColumn()
    {
        // A ProcMon CSV row: several quoted fields; we grab every quoted token and let the caller filter.
        var text =
            "\"09:00:00.1\",\"trace32.exe\",\"1234\",\"ReadFile\",\"C:\\proj\\boot\\loader.bin\",\"SUCCESS\",\"Offset: 0\"\n" +
            "\"09:00:00.2\",\"trace32.exe\",\"1234\",\"CreateFile\",\"C:\\proj\\scripts\\load_app.cmm\",\"SUCCESS\",\"\"\n";
        var paths = FileAccessTrace.Paths(text);
        Assert.Contains(@"C:\proj\boot\loader.bin", paths);
        Assert.Contains(@"C:\proj\scripts\load_app.cmm", paths);
        Assert.Contains("trace32.exe", paths);   // noise — the caller's under-root filter drops it
    }

    [Fact]
    public void Paths_Strace_Openat_ExtractsQuotedPath()
    {
        var text =
            "openat(AT_FDCWD, \"src/main.c\", O_RDONLY) = 3\n" +
            "openat(AT_FDCWD, \"/usr/include/stdio.h\", O_RDONLY) = 4\n";
        var paths = FileAccessTrace.Paths(text);
        Assert.Contains("src/main.c", paths);
        Assert.Contains("/usr/include/stdio.h", paths);
    }

    [Fact]
    public void Paths_CustomRegex_NamedPathGroup()
    {
        var text = "READ|src/a.c|ok\nREAD|data/tab.bin|ok\n";
        var pat = new Regex(@"READ\|(?<path>[^|]+)\|", RegexOptions.Compiled);
        var paths = FileAccessTrace.Paths(text, pat);
        Assert.Equal(new[] { "data/tab.bin", "src/a.c" }, paths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Paths_Dedupes()
    {
        var text = "src/main.c\nsrc/main.c\nsrc/main.c\n";
        Assert.Single(FileAccessTrace.Paths(text));
    }

    [Fact]
    public void Paths_Empty_ReturnsEmpty()
        => Assert.Empty(FileAccessTrace.Paths(""));
}
