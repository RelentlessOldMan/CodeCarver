using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Cli;

public class ComputedIncludeTests
{
    const string Main = "#include <stdio.h>\n#define STR(x) #x\n#define XSTR(x) STR(x)\n#define IMPL_FILE impl/IMPL_NAME.c\n"
                      + "#include XSTR(IMPL_FILE)\nint main(void){ printf(\"%d\n\", impl_helper()); return 0; }\n";

    // Review E3: the .c a computed #include names was dropped.
    [Fact]
    public void ComputedIncludeOfAC_File_NamedByADashD_IsKept()
    {
        using var t = new TreeCarve()
            .W("main.c", Main)
            .W("impl/engine.c", "int impl_helper(void){ return 3; }\n")
            .W("impl/other.c", "int other_helper(void){ return 4; }\n");
        t.Compile("main.c", "-DIMPL_NAME=engine");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("impl/engine.c"), r.Out + r.Err);
        Assert.False(t.Kept("impl/other.c"), r.Out + r.Err);
    }

    // A tree macro whose value isn't followed stands for any name: every file it could be stays.
    [Fact]
    public void ComputedInclude_ThroughAMacroDefinedElsewhere_KeepsEveryMatch()
    {
        using var t = new TreeCarve()
            .W("config.h", "#define IMPL_NAME engine\n")
            .W("main.c", "#include \"config.h\"\n" + Main)
            .W("impl/engine.c", "int impl_helper(void){ return 3; }\n")
            .W("lib/unrelated.c", "int unrelated(void){ return 4; }\n");
        t.Compile("main.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("impl/engine.c"), r.Out + r.Err);
        Assert.False(t.Kept("lib/unrelated.c"), r.Out + r.Err);
    }

    // The same at graph level, where the verify's fallback (which finds impl_helper's file by name) can't hide it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComputedInclude_IsAnIncludeEdge(bool dashD)
    {
        var files = new List<(string, string)>
        {
            ("main.c", (dashD ? "" : "#include \"config.h\"\n") + Main),
            ("impl/engine.c", "int impl_helper(void){ return 3; }\n"),
            ("lib/unrelated.c", "int unrelated(void){ return 4; }\n"),
        };
        if (!dashD) files.Add(("config.h", "#define IMPL_NAME engine\n"));
        using var fe = new CodeCarver.Frontend.CFrontEnd();
        if (dashD) fe.CommandLineMacros = CommandLineMacros.FromSpecs(new[] { "IMPL_NAME=engine" });
        var graph = fe.BuildGraph(files);
        var main = graph.Nodes.Single(n => n.Kind == CodeCarver.Core.Graph.NodeKind.File && n.Name == "main.c").Id;
        var included = graph.OutEdges(main).Where(e => e.Kind == CodeCarver.Core.Graph.EdgeKind.Includes)
                            .Select(e => graph.GetNode(e.To).Name).ToList();
        Assert.Contains("impl/engine.c", included);
        Assert.DoesNotContain("lib/unrelated.c", included);
    }

    // A header named only by a -D string value (escaped in one command, bare in another): it was cut, and the build failed.
    [Fact]
    public void ComputedInclude_OfAHeaderNamedByADashDString_IsKept()
    {
        using var t = new TreeCarve()
            .W("main.c", "#include KQ_BOARD_HDR\nint main(void){ return KQ_BOARD_ID; }\n")
            .W("other.c", "int kq_other(void){ return 1; }\n")
            .W("inc/kq_board_a.h", "#define KQ_BOARD_ID 7\n")
            .W("inc/kq_board_b.h", "#define KQ_BOARD_OTHER 9\n");
        t.Compile("main.c", $"-I{t.S("inc")} -DKQ_BOARD_HDR=\\\"kq_board_a.h\\\"");
        t.Compile("other.c", $"-I{t.S("inc")} -DKQ_BOARD_HDR=\"kq_board_a.h\"");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("inc/kq_board_a.h"), r.Out + r.Err);
        Assert.False(t.Kept("inc/kq_board_b.h"), r.Out + r.Err);
        Assert.Contains("includes.computedUnresolved = 0", t.Summary);
    }

    // A macro the carve sees no value for: the include may be any file, so it no longer drops the one the build opened.
    [Fact]
    public void ComputedInclude_ThatNamesNoHeader_KeepsWhatItMayBe_AndSaysSo()
    {
        using var t = new TreeCarve()
            .W("main.c", "#include KQ_UNSEEN_HDR\nint main(void){ return 0; }\n")
            .W("inc/needed.h", "#define KQ_NEEDED 1\n");
        t.Compile("main.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("inc/needed.h"), r.Out + r.Err);
        Assert.Contains("includes.computedUnresolved = 1", t.Summary);
        Assert.Contains("computed #include that names no header", r.Err);
    }

    // languages = ["c"] and the log compiles a .cpp: it is code, so the header only it includes stays.
    [Fact]
    public void ACompiledCpp_UnderLanguagesC_IsCarvedAsCode()
    {
        using var t = new TreeCarve()
            .W("main.c", "int kq_util(void);\nint main(void){ return kq_util(); }\n")
            .W("util.cpp", "#include \"only_cpp.h\"\nextern \"C\" int kq_util(void){ return KQ_ONLY; }\n")
            .W("only_cpp.h", "#define KQ_ONLY 3\n");
        t.Compile("main.c");
        t.Compile("util.cpp");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("util.cpp"), r.Out + r.Err);
        Assert.True(t.Kept("only_cpp.h"), r.Out + r.Err);
        Assert.Contains("languages.addedFromBuildLog = 1", t.Summary);
        Assert.Contains("lang = cpp", t.Summary);
    }

    [Theory]
    [InlineData("XSTR(IMPL_FILE)", "impl/engine.c")]
    [InlineData("HDR", "cfg/board.h")]
    [InlineData("<plat/CAT(name_, X).h>", "plat/name_X.h")]   // an argument of ## is not expanded first
    [InlineData("XSTR(UNKNOWN_ONE.c)", "\u0001.c")]
    [InlineData("STR(UNKNOWN_ONE.c)", "UNKNOWN_ONE.c")]
    public void Expand_FollowsObjectAndFunctionLikeMacros(string operand, string expected)
    {
        var macros = new Dictionary<string, (IReadOnlyList<string>? Params, string Body)>
        {
            ["STR"] = (new[] { "x" }, "#x"),
            ["XSTR"] = (new[] { "x" }, "STR(x)"),
            ["IMPL_FILE"] = (null, "impl/IMPL_NAME.c"),
            ["IMPL_NAME"] = (null, "engine"),
            ["HDR"] = (null, "\"cfg/board.h\""),
            ["CAT"] = (new[] { "a", "b" }, "a##b"),
            ["X"] = (null, "x"),
        };
        var got = ComputedInclude.Expand(operand, n => macros.TryGetValue(n, out var m) ? new[] { m } : null, n => n == "UNKNOWN_ONE");
        Assert.Equal(expected, got);
    }
}
