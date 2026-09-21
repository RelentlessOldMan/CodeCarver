using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Proves the front-end resolves #ifdef branches against a config, so platform-specific code
/// in dead branches is not extracted (a tighter, config-specific carve).</summary>
public class FrontendPreprocessTests
{
    private const string PlatformC = """
        #if defined(USE_LINUX)
        int linux_impl(void) { return 1; }
        #elif defined(USE_WINDOWS)
        int windows_impl(void) { return 2; }
        #else
        int fallback_impl(void) { return 0; }
        #endif

        int common(void) { return 42; }
        """;

    [Fact]
    public void WithConfig_OnlyLiveBranchFunctionsAreExtracted()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("plat.c", PlatformC) },
            MacroTable.FromDefines(new[] { "USE_LINUX" }));

        var fns = graph.Nodes.Where(n => n.Kind == NodeKind.Function).Select(n => n.Name).ToHashSet();
        Assert.Contains("linux_impl", fns);      // live branch
        Assert.Contains("common", fns);
        Assert.DoesNotContain("windows_impl", fns); // dead branch
        Assert.DoesNotContain("fallback_impl", fns);
    }

    [Fact]
    public void WithoutConfig_AllBranchesKept_Conservative()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("plat.c", PlatformC) }); // no defines

        var fns = graph.Nodes.Where(n => n.Kind == NodeKind.Function).Select(n => n.Name).ToHashSet();
        Assert.Contains("linux_impl", fns);
        Assert.Contains("windows_impl", fns);
        Assert.Contains("fallback_impl", fns);
    }
}
