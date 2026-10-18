using System.Text;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// <see cref="TemplateInstances.DefinesAny"/>: the check asks it by name when a name a walk looked up has a hash a
/// filtered header dropped, and walks again only on a yes. It must say yes exactly where
/// <see cref="TemplateInstances.KeepDefines"/> drops a line of that name: a no where a define went is a walk that missed it.
/// </summary>
public sealed class KeepDefinesTests
{
    static int H(string n) => string.GetHashCode(n.AsSpan());

    static bool Drops(string text, string name)
    {
        var dropped = new List<int>();
        TemplateInstances.KeepDefines(text, h => h != H(name), dropped);
        return dropped.Count > 0;
    }

    const string Guarded = "#ifndef G_H\n#define G_H\n#define A 1\n#undef B\n#if X\n#define C 2\n#endif\n#endif\n#define AFTER 3\n";

    [Theory]
    [InlineData("A", true)]
    [InlineData("B", true)]       // an #undef goes too
    [InlineData("C", true)]
    [InlineData("G_H", false)]    // the guard: the first two directives stay
    [InlineData("AFTER", false)]  // after the last #endif
    [InlineData("X", false)]      // only looked at, never defined
    [InlineData("AB", false)]
    public void SaysWhatKeepDefinesDrops(string name, bool drops)
    {
        Assert.Equal(drops, Drops(Guarded, name));
        Assert.Equal(drops, TemplateInstances.DefinesAny(Guarded, new[] { name }));
    }

    [Fact]
    public void ALineThatOpensAComment_IsKept_AndSoNotDefined()
    {
        const string text = "#ifndef G\n#define G\n#define A 1 /* opens\n#define B 2\n*/\n#define C 3\n#endif\n";
        foreach (var n in new[] { "A", "B", "C" })
            Assert.Equal(Drops(text, n), TemplateInstances.DefinesAny(text, new[] { n }));
        Assert.False(TemplateInstances.DefinesAny(text, new[] { "A" }));
    }

    [Fact]
    public void AgreesWithKeepDefines_OnRandomHeaders()
    {
        var rnd = new Random(5);
        var names = new[] { "A", "B", "C", "A_1", "_b", "TNAME", "WEB_X" };
        var lines = new[] { "#define {0} 1", "#define {0}(x) (x)", "#undef {0}", "# define {0}", "#if {0}", "#ifdef {0}", "#endif",
                            "/* {0}", "*/", "#define {0} /* c */ 2", "int {0};", "#  undef  {0}" };
        for (var n = 0; n < 5_000; n++)
        {
            var sb = new StringBuilder("#ifndef G\n#define G\n");
            for (var k = rnd.Next(0, 12); k > 0; k--)
                sb.Append(string.Format(lines[rnd.Next(lines.Length)], names[rnd.Next(names.Length)])).Append('\n');
            sb.Append("#endif\n");
            var text = sb.ToString();
            foreach (var name in names)
                Assert.True(Drops(text, name) == TemplateInstances.DefinesAny(text, new[] { name }), $"{name} in [{text.Replace("\n", "\\n")}]");
        }
    }

    [Fact]
    public void AHashHit_ThatIsAnotherName_IsNotDefined()
    {
        // Two names of one hash (found by brute force: the hash is per process): the header drops the first, a walk
        // looks up the second. By hash it was dropped; by name it was not.
        var seen = new Dictionary<int, string>();
        string a = "", b = "";
        for (var i = 0; ; i++)
        {
            var n = "n" + i.ToString("x");
            if (seen.TryGetValue(H(n), out var other)) { (a, b) = (other, n); break; }
            seen[H(n)] = n;
        }
        var text = $"#ifndef G\n#define G\n#define {a} 1\n#endif\n";
        Assert.True(Drops(text, b));   // KeepDefines, asked by hash
        Assert.False(TemplateInstances.DefinesAny(text, new[] { b }));
        Assert.True(TemplateInstances.DefinesAny(text, new[] { b, a }));
    }
}
