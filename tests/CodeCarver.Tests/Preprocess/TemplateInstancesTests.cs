using System.Text;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// A template head is expanded in every world of the macros it reaches, and only those: config headers full of macros
/// defined both ways in an #if no longer make a world each, and still every value of a macro the head does reach
/// counts, whether it names it outright or builds its name by pasting.
/// </summary>
public sealed class TemplateInstancesTests
{
    const string Cat = "#define CAT2(a,b) a##b\n#define CAT(a,b) CAT2(a,b)\n";

    // Many config macros defined both ways (more combinations than MaxWorlds) beside the two the head reaches.
    static string Config(int n)
    {
        var sb = new StringBuilder();
        for (var k = 0; k < n; k++) sb.Append($"#if MODK_FEAT_{k}\n#define MODK_OPT_{k} 1\n#else\n#define MODK_OPT_{k} 0\n#endif\n");
        return sb.ToString();
    }

    static List<string> Names(string unit, Dictionary<string, string> headers)
        => TemplateInstances.Find(unit, h => headers.TryGetValue(h, out var t) ? t : null).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Fact]
    public void EveryCombination_OfTheMacrosTheHeadReaches()
    {
        var unit = "#include \"cfg.h\"\n" + Cat
                 + "#ifdef A\n#define TNAME red\n#else\n#define TNAME blue\n#endif\n"
                 + "#ifdef B\n#define SUFFIX get\n#else\n#define SUFFIX put\n#endif\n"
                 + "int CAT(TNAME, SUFFIX)(void) { return 1; }\n";
        Assert.Equal(new[] { "blueget", "blueput", "redget", "redput" }, Names(unit, new() { ["cfg.h"] = Config(40) }));
    }

    [Fact]
    public void AMacroNamedByPasting_CountsWithEveryValue()
    {
        // The head pastes MODE_ and SEL into MODE_FAST, whose two values only show once it is looked up.
        var unit = Cat + "#define SEL FAST\n#if X\n#define MODE_FAST quick\n#else\n#define MODE_FAST slow\n#endif\n"
                 + Config(40) + "#define PICK(s) CAT(MODE_, s)\nint CAT(PICK(SEL), _run)(void) { return 1; }\n";
        Assert.Equal(new[] { "quick_run", "slow_run" }, Names(unit, new()));
    }

    [Fact]
    public void ConfigHeaders_DoNotMultiplyTheWork()
    {
        var headers = new Dictionary<string, string> { ["cfg.h"] = Config(2000), ["tmpl.h"] = "int CAT(TNAME, _get)(void) { return 1; }\n" };
        var unit = new StringBuilder("#include \"cfg.h\"\n" + Cat);
        for (var k = 0; k < 50; k++) unit.Append($"#define TNAME t{k}\n#include \"tmpl.h\"\n#undef TNAME\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(50, Names(unit.ToString(), headers).Count);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }
}
