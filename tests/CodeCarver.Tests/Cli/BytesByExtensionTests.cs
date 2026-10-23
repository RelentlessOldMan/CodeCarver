using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A stage's output bytes, split by what they are: code the build needs and infrastructure kept whole, each by
/// extension. Work's max stage came to 47 GB with no key to say which kind of file held it.
/// </summary>
public sealed class BytesByExtensionTests
{
    [Fact]
    public void BytesAfter_SplitByKindAndExtension()
    {
        using var t = new TreeCarve();
        t.W("main.c", "#include \"inc/a.h\"\nint main(void){ return A_ONE; }\n")
         .W("inc/a.h", "#define A_ONE 1\n")
         .W("dead.c", "int modd_dead(void){ return 2; }\n")
         .W("tools/run.cmm", new string('x', 3000) + "\n")
         .W("tools/two.cmm", "y\n")
         .W("tools/Makefile", "all:\n")
         .W("data/blob.Weird_Ext", "z\n");
        var (code, o, e) = t.Carve();
        Assert.True(code == 0, o + e);
        var s = t.Summary.Replace("\r", "");
        Has(s, $"stage0.bytesAfter.code.c.bytes = {new FileInfo(Path.Combine(t.Src, "main.c")).Length}\n");
        Has(s, "stage0.bytesAfter.code.c.files = 1\n");
        Has(s, $"stage0.bytesAfter.code.h.bytes = {new FileInfo(Path.Combine(t.Src, "inc", "a.h")).Length}\n");
        Has(s, "stage0.bytesAfter.infrastructure.cmm.bytes = 3003\n");
        Has(s, "stage0.bytesAfter.infrastructure.cmm.files = 2\n");
        Has(s, "stage0.bytesAfter.infrastructure.none.files = 1\n");   // Makefile
        Has(s, "stage0.bytesAfter.infrastructure.other.files = 1\n");  // not a short lower-case extension
        Assert.DoesNotContain("weird", s, StringComparison.OrdinalIgnoreCase);
    }

    static void Has(string summary, string line) =>
        Assert.True(summary.Contains(line), $"no \"{line.Trim()}\" in:\n"
            + string.Join("\n", summary.Split('\n').Where(l => l.Contains(".bytesAfter"))));
}
