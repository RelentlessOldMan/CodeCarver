using System.Text;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// The link check holds a header for the template walks with only the defines of names a walk looked up
/// (TemplateInstances.KeepDefines): a walk that looks up a name a header dropped is walked again with it. What the walks
/// find is what they found over the whole headers - through chains of macros across headers, pasted names, computed
/// includes and include guards.
/// </summary>
public sealed class HeaderFilterTests
{
    static readonly Dictionary<string, string> Tree = new(StringComparer.Ordinal)
    {
        ["cfg.h"] = "#ifndef CFG_H\n#define CFG_H\n#define CAT_(a, b) a##_##b\n#define CAT(a, b) CAT_(a, b)\n#endif\n",
        // A chain: the head's macro to the next, header to header, and a name made by pasting.
        ["a.h"] = "#ifndef A_H\n#define A_H\n#define A_UNUSED 1\n#define HIDE(n) VIA(n)\n#define A_OTHER 2\n#endif\n",
        ["b.h"] = "#ifndef B_H\n#define B_H\n#define B_UNUSED 1\n#define VIA(n) CAT(PREFIX, n)\n#define B_MORE 3\n#endif\n",
        ["c.h"] = "#ifndef C_H\n#define C_H\n#define C_UNUSED 1\n#define PREFIX MODE\n#define MODE_den MODE_den_fast\n#define C_MORE 4\n#endif\n",
        // A computed include, its name defined among unused ones.
        ["sel.h"] = "#ifndef SEL_H\n#define SEL_H\n#define SEL_A 1\n#define TMPL_HEADER \"t.h\"\n#define SEL_B 2\n#endif\n",
        ["t.h"] = "int HIDE(den)(void) { return 0; }\n",
    };

    const string Unit = "#include \"cfg.h\"\n#include \"a.h\"\n#include \"b.h\"\n#include \"c.h\"\n#include \"sel.h\"\n#include TMPL_HEADER\n";

    static List<(string Name, int Line)> Walk(string unit, Func<string, string?> read, List<string>? lookedUp = null)
        => TemplateInstances.Find(unit, read, lookedUp: lookedUp).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    [Fact]
    public void AWalkOfTheKeptDefines_IsTheWalkOfTheWhole_WhenItLooksUpNoneDropped()
    {
        var lookedUp = new List<string>();
        var whole = Walk(Unit, raw => Tree.TryGetValue(raw, out var t) ? t : null, lookedUp);
        Assert.Equal(new[] { ("MODE_den_fast", 6) }, whole);
        var keep = lookedUp.Select(n => string.GetHashCode(n.AsSpan())).ToHashSet();
        var dropped = new List<int>();
        var cut = Tree.ToDictionary(kv => kv.Key, kv => kv.Key == "t.h" ? kv.Value : TemplateInstances.KeepDefines(kv.Value, keep.Contains, dropped));
        Assert.DoesNotContain("A_UNUSED", cut["a.h"]);
        Assert.DoesNotContain("SEL_B", cut["sel.h"]);
        Assert.Contains("#define VIA(n)", cut["b.h"]);
        Assert.Contains(string.GetHashCode("C_MORE".AsSpan()), dropped);
        Assert.Equal(whole, Walk(Unit, raw => cut.TryGetValue(raw, out var t) ? t : null));
    }

    [Fact]
    public void KeepDefines_KeepsWhatTheGuardIsReadFrom_AndCommentEdges()
    {
        static string Keep(string text) => TemplateInstances.KeepDefines(text, _ => false, new List<int>());
        // The first two directives and those after the last #endif, whatever their names: dropping X here made the
        // second one a #define G and the text guarded (G's own define may go: a walk looking G up is walked again);
        // dropping Z made the #endif the last directive.
        Assert.Equal("#ifndef G\n#define X 1\n#endif\n", Keep("#ifndef G\n#define X 1\n#define G\n#endif\n"));
        Assert.Equal("#ifndef G\n#define G\n#endif\n#define Z 1\n", Keep("#ifndef G\n#define G\n#define Y 2\n#endif\n#define Z 1\n"));
        // A define that opens a block comment: the lines after it are in the comment.
        Assert.Equal("#ifndef G\n#define G\n#define W 1 /* opens\n*/\n#endif\n", Keep("#ifndef G\n#define G\n#define W 1 /* opens\n*/\n#define V 2\n#endif\n"));
        // No #endif at all: no guard to keep.
        Assert.Equal("#if 1\n#include \"x.h\"\n", Keep("#if 1\n#include \"x.h\"\n#define Q 1\n#undef Q\n"));
        var same = "#ifndef G\n#define G\n#endif\n";
        Assert.Same(same, Keep(same));
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(0)]
    public void TheLinkCheck_FindsTheInstance_ThroughAChainOfDroppedDefines(int registers)
    {
        // The chain's links among thousands of register defines in each header: the first walk has none of them, and
        // walks again until it has each (a header held without a define the walk looked up is read again with it). Without
        // the registers each header is small, held whole, and walked once.
        var root = Path.Combine(Path.GetTempPath(), "cc-hflt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string, string) W(string rel, string text)
            {
                var p = Path.Combine(root, rel);
                File.WriteAllText(p, text);
                return (rel, p);
            }
            string Pad(string text, string tag)
            {
                var sb = new StringBuilder();
                for (var k = 0; k < registers; k++) sb.Append($"#define {tag}_R{k} (0x{k * 4:X}u)\n");
                return text.Replace("#endif\n", sb + "#endif\n");
            }
            var emitted = Tree.Select(kv => W(kv.Key, kv.Key == "t.h" ? kv.Value : Pad(kv.Value, "HF" + kv.Key[0]))).ToList();
            emitted.Add(W("main.c", "int MODE_den_fast(void);\nint main(void) { return MODE_den_fast(); }\n"));
            var dropped = new[] { W("gone.c", Unit) };
            var r = EmittedLinkCheck.Run(emitted, dropped);
            Assert.Equal(new[] { "MODE_den_fast" }, r.Violations.Select(v => v.Name).Distinct().ToArray());
            Assert.True(registers == 0 ? r.Stats["templateWalksRedone"] == 0 : r.Stats["templateWalksRedone"] > 0, $"redone {r.Stats["templateWalksRedone"]}");
            Assert.Equal(0, r.Stats["templateWalksWhole"]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
