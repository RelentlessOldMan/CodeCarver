using System.Text;
using CodeCarver.Tests.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// The roots step lets go of the assembly it reads. It held every assembly file's text (and what they include) in a
/// list and a cache that lived to the end of the run: a work run kept 9 GB from the roots step to its memory stop in
/// the check. A tree of 120 MB of assembly text (as .NET strings) must leave the roots step holding none of it.
/// </summary>
[Collection(nameof(TemplateWalkMemoryTests))]   // measures live memory: no other memory test at once
public sealed class RootsHeldMemoryTests
{
    [Fact]
    public void TheRootsStep_KeepsNoAssemblyText()
    {
        using var t = new TreeCarve()
            .W("main.c", "int handler(void);\nint main(void) { return handler(); }\nint handler(void) { return 0; }\n")
            .Compile("main.c");
        var line = new StringBuilder();
        for (var k = 0; k < 200; k++) line.Append($"    .word hr_{k}_handler\n");
        for (var i = 0; i < 60; i++)
        {
            var sb = new StringBuilder($".include \"common{i % 3}.inc\"\n");
            while (sb.Length < 1_000_000) sb.Append(line);
            t.W($"startup/s{i}.s", sb.ToString());
        }
        for (var i = 0; i < 3; i++) t.W($"startup/common{i}.inc", $"    .equ C{i}, 1\n");

        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        long Key(string k) => long.Parse(System.Text.RegularExpressions.Regex.Match(t.Summary, $@"(?m)^{System.Text.RegularExpressions.Regex.Escape(k)} = (\d+)").Groups[1].Value);
        var kept = Key("mem.roots.asmAndForceKeep.liveMb") - Key("mem.roots.outsideHeaders.liveMb");
        Assert.True(kept < 40, $"the assembly part of the roots step kept {kept} MB (120 MB of text read)");
        Assert.True(Key("mem.roots.liveMb") - Key("mem.roots.outsideHeaders.liveMb") < 40);
    }
}
