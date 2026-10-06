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

    [Theory]
    // Empty bodies: with a call in the body, tree-sitter's recovery happens to find the function (content-dependent).
    [InlineData("tbl.cpp", "static int tbl[] = {\n#ifdef A\n 1,\n#endif\n 2,\n};\nvoid f ()\n{\n}\n")]
    [InlineData("tbl.cpp", "static int tbl[] = {\n#if A\n 1,\n#elif B\n 3,\n#else\n 2,\n#endif\n};\nvoid f ()\n{\n}\n")]
    [InlineData("tbl.c", "static int tbl[] = {\n#ifdef A\n 1,\n#else\n 2,\n#endif\n};\nvoid f ()\n{\n}\n")]   // .c in a mixed tree
    public void ConditionalInAnInitializer_DoesNotSwallowTheNextFunction_CppGrammar(string file, string src)
    {
        // Work eval, 1.0.165: three plain `void name ()` definitions after a table with #ifdef rows, in a mixed tree.
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("main.cpp", "void f();\nint main() { f(); return 0; }\n"), (file, src) });
        var main = graph.Nodes.First(n => n.Name == "main").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(main, RootKind.ExplicitSymbol) });
        var f = graph.Nodes.SingleOrDefault(n => n.Kind == NodeKind.Function && n.Name == "f");
        Assert.NotNull(f);
        Assert.True(plan.IsKept(f!.Id));
    }

    [Fact]
    public void Initializers_AreOnlyTouchedWhenAsked()
    {
        const string src = "static int tbl[] = {\n#ifdef A\n 1,\n#else\n 2,\n#endif\n};\n";
        Assert.Same(src, ParamListConditionals.Blank(src, out _));
        var parse = ParamListConditionals.Blank(src, initializers: true, out var removed);
        Assert.DoesNotContain("#", parse);
        Assert.Contains("1,", parse);
        Assert.DoesNotContain("2,", parse);
        Assert.Contains("2,", removed);
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
