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
