using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Work eval, 1.0.162: a definition whose parameter list has #if/#else alternatives never became a
/// node, so its file was dropped while a call still needed it.</summary>
public class ParamListConditionalsTests
{
    const string Setup = "int setup(int a,\n#if defined(BIG)\n          long x, long y,\n#else\n          short_t x,\n#endif\n          int b)\n{\n    return a + b;\n}\n";

    [Fact]
    public void Blank_RemovesTheElseBranchOfAParameterList_KeepingLines()
    {
        var parse = ParamListConditionals.Blank(Setup, out var removed);
        Assert.Equal(Setup.Split('\n').Length, parse.Split('\n').Length);
        Assert.DoesNotContain("#else", parse);
        Assert.DoesNotContain("short_t", parse);
        Assert.Contains("long x", parse);
        Assert.DoesNotContain("#", parse);   // the C++ grammar takes no directive inside a parameter list
        Assert.Contains("short_t", removed);
    }

    [Theory]
    [InlineData("int f(int a,\n#ifdef X\n  int b,\n#endif\n  int c) { }\n", true)]
    [InlineData("int f(\n#if X\n  int b\n#else\n  void\n#endif\n) { }\n", true)]
    [InlineData("int f(int a, /* first */\n\n// note\n#if X\n  int b,\n#endif\n  int c) { }\n", true)]   // looks past comments
    [InlineData("#include <a.h>\n#ifdef X\nint f(void);\n#endif\n", false)]
    [InlineData("int g(void) { return 0; }\n/* doc */\n#if X\nint f(void) { }\n#endif\n", false)]
    [InlineData("#if X\nint f(void);\n#endif\n", false)]
    public void MayHaveOne_IsTheCheapPreCheck(string src, bool expected)
    {
        Assert.Equal(expected, ParamListConditionals.MayHaveOne(src));
        if (!expected) Assert.Same(src, ParamListConditionals.Blank(src, out _));
    }

    [Fact]
    public void Blank_LeavesConditionalsOutsideParameterListsAlone()
    {
        const string src = "#if A\nint f(void) { return 1; }\n#else\nint f(void) { return 2; }\n#endif\n"
                         + "int g(int x) {\n  return h(x,\n#if A\n  1\n#else\n  2\n#endif\n  );\n}\n";
        Assert.Same(src, ParamListConditionals.Blank(src, out var removed));
        Assert.Equal("", removed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DefinitionWithAlternativeParameterSets_IsKept_AndTheOtherBranchNamesAreUses(bool cGrammar)
    {
        const string main = "int setup();\nint main(void) { return setup(1, 2); }\n";
        const string types = "typedef short short_t;\nint short_helper(void) { return 0; }\n";
        const string setupWithUse = "int setup(int a,\n#if defined(BIG)\n  long x, long y, long z, long w,\n#else\n  short_t x, int (*cb)(void) = short_helper,\n#endif\n  int b)\n{\n  return a + b;\n}\n";
        using TreeSitterFrontEnd fe = cGrammar ? new CFrontEnd() : new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("main.c", main), ("setup.c", setupWithUse), ("types.c", types) });
        var root = graph.Nodes.First(n => n.Name == "main").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(root, RootKind.ExplicitSymbol) });
        var setup = graph.Nodes.SingleOrDefault(n => n.Kind == NodeKind.Function && n.Name == "setup");
        Assert.NotNull(setup);
        Assert.True(plan.IsKept(setup!.Id));
        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "short_helper").Id));   // named only in the #else branch
    }
}
