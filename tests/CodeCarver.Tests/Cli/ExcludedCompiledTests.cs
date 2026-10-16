using Xunit;

namespace CodeCarver.Tests.Cli;

public class ExcludedCompiledTests
{
    // Review E10: a file the build log compiles, excluded, while kept code calls into it: the carved tree can't link.
    [Fact]
    public void ExcludingAFileTheBuildCompiles_Warns()
    {
        using var t = new TreeCarve()
            .W("main.c", "int helper(void);\nint main(void){ return helper(); }\n")
            .W("extra/helper.c", "int helper(void){ return 1; }\n")
            .W("tests/t.c", "int t(void){ return 2; }\n");
        t.Compile("main.c").Compile("extra/helper.c");
        var r = t.Carve(common: "excludeDirectories = [\"extra\", \"tests\"]\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("excludes 1 source file(s) the build log compiles, e.g. extra/helper.c", r.Err);
    }

    [Fact]
    public void ExcludingOnlyWhatTheBuildDoesNotCompile_DoesNotWarn()
    {
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("tests/t.c", "int t(void){ return 2; }\n");
        t.Compile("main.c");
        var r = t.Carve(common: "excludeDirectories = [\"tests\"]\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.DoesNotContain("the build log compiles", r.Err);
    }
}
