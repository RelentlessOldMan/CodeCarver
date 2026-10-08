using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>summary.txt says what content carving removed in each stage, so stages that barely differ in size
/// can be explained from numbers alone.</summary>
public sealed class StageCarveSummaryTests
{
    [Fact]
    public void Summary_SaysWhatSourceAndHeaderCarvingRemoved()
    {
        using var t = new TreeCarve();
        t.W("main.c", "#include \"api.h\"\nint main(void){ return used(); }\n")
         .W("api.h", "int used(void);\nstatic inline int unused_inline(void){ return 0; }\n")
         .W("lib.c", "#include \"api.h\"\nint used(void){ return 1; }\nint unused(void){ return 2; }\n");
        t.Compile("main.c").Compile("lib.c");
        var (code, o, e) = t.Carve(extraToml: "[stages.safe]\ncarveSourceFileContents = false\ncarveHeaderFileContents = false\n"
            + "[stages.max]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n");
        Assert.True(code == 0, o + e);
        var p = Path.Combine(t.Root, "out", "max", "codecarver", "summary.txt");
        Assert.True(File.Exists(p), string.Join("\n", Directory.GetFiles(Path.Combine(t.Root, "out"), "summary.txt", SearchOption.AllDirectories)));
        var s = File.ReadAllText(p);                                     // stages run least aggressive first: max is stage1
        Assert.Contains("stage1.sourceCarve.filesPruned = 1", s);
        Assert.Matches(@"stage1\.sourceCarve\.bytesRemoved = [1-9]", s);
        Assert.Contains("stage1.headerCarve.bigHeadersKept = 0", s);   // only big generated headers are carved
        Assert.DoesNotContain("stage0.sourceCarve", s);
        Assert.DoesNotContain("unused(void)", File.ReadAllText(Path.Combine(t.Root, "out", "max", "carved", "lib.c")));
    }
}
