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

    [Fact]
    public void ATemplateFarDown_IsFound()
    {
        // 30 includes down (death-lite reaches 46); the walk stopped at 8 and missed it.
        var headers = new Dictionary<string, string> { ["cat.h"] = Cat, ["tmpl.h"] = "int CAT(TNAME, _get)(void) { return 1; }\n" };
        for (var k = 0; k < 30; k++) headers[$"l{k}.h"] = k == 29 ? "#define TNAME deep\n#include \"tmpl.h\"\n" : $"#include \"l{k + 1}.h\"\n";
        Assert.Equal(new[] { "deep_get" }, Names("#include \"cat.h\"\n#include \"l0.h\"\n", headers));
    }

    [Fact]
    public void AGuardedHeaderReadAgain_IsWalkedOnce()
    {
        // The check hands a header it does not hold as a new string each read: the walk must not take it for a new header
        // (each include of a guarded header walked it again, with all it includes).
        var headers = new Dictionary<string, string> { ["cat.h"] = Cat, ["tmpl.h"] = "int CAT(TNAME, _get)(void) { return 1; }\n" };
        for (var k = 0; k < 12; k++)
            headers[$"g{k}.h"] = $"#ifndef G{k}\n#define G{k}\n" + (k < 11 ? $"#include \"g{k + 1}.h\"\n#include \"g{k + 1}.h\"\n" : "") + "#endif\n";
        var reads = 0;
        var unit = "#include \"cat.h\"\n#include \"g0.h\"\n#define TNAME t\n#include \"tmpl.h\"\n";
        var found = TemplateInstances.Find(unit, raw =>
        {
            reads++;
            return headers.TryGetValue(raw, out var t) ? new string(t.AsSpan()) : null;   // a new string each read
        });
        Assert.Equal(new[] { "t_get" }, found.Select(i => i.Name).Distinct().ToArray());
        Assert.True(reads < 200, $"{reads} reads for 14 headers (each re-include walked again doubles them)");
    }

    [Theory]
    [InlineData("#if 0\n#include \"g0.h\"\n#endif\n")]                   // under an #if
    [InlineData("/* notes:\n#include \"g0.h\"\n*/\n")]                  // in a comment
    public void AGuardedChainReachedFirstElsewhere_IsWalkedAgainWhereTheNameIsSet(string first)
    {
        // A guarded chain down to a template, reached first (maybe) with no TNAME, then again with TNAME set: walked
        // once, the second include was skipped and its instance missed (death-lite: 60% of them).
        var headers = new Dictionary<string, string> { ["cat.h"] = Cat, ["tmpl.h"] = "int CAT(TNAME, _get)(void) { return 1; }\n" };
        for (var k = 0; k < 10; k++)
            headers[$"g{k}.h"] = $"#ifndef G{k}\n#define G{k}\n#include \"{(k < 9 ? $"g{k + 1}.h" : "tmpl.h")}\"\n#endif\n";
        var unit = "#include \"cat.h\"\n" + first + "#define TNAME r3\n#include \"g0.h\"\n#undef TNAME\n";
        Assert.Contains("r3_get", Names(unit, headers));
    }

    [Fact]
    public void AGuardedChainIncludedForRealBeforeTheNameIsSet_IsNothingAfter()
    {
        // As gcc: the first include, for real, walks the chain with no TNAME; after it every guard is defined and the
        // include with TNAME set is nothing (walked again, it put out instances gcc never sees: death-lite, 103 a unit).
        var headers = new Dictionary<string, string> { ["cat.h"] = Cat, ["tmpl.h"] = "#ifdef TNAME\nint CAT(TNAME, _get)(void) { return 1; }\n#endif\n" };
        for (var k = 0; k < 10; k++)
            headers[$"g{k}.h"] = $"#ifndef G{k}\n#define G{k}\n#include \"{(k < 9 ? $"g{k + 1}.h" : "tmpl.h")}\"\n#endif\n";
        var unit = "#include \"cat.h\"\n#include \"g0.h\"\n#define TNAME r3\n#include \"g0.h\"\n#undef TNAME\n";
        Assert.DoesNotContain("r3_get", Names(unit, headers));
        // The guard taken away again: the next include is a walk again.
        Assert.Contains("r4_get", Names(unit + "#undef G0\n#define TNAME r4\n#include \"g0.h\"\n", headers));
    }

    [Fact]
    public void AHeaderWalkedInsideACycle_IsWalkedAgainWhereTheNameIsSet()
    {
        // a.h includes b.h, b.h includes a.h back and then the template. Walked first from a.h (no TNAME), b.h's include
        // of a.h is nothing (a.h is on the stack) and its record held nothing of a.h. A later #include "b.h" with TNAME
        // set, under an #if before, walks a.h for the first time with it, and the template.
        var headers = new Dictionary<string, string>
        {
            ["cat.h"] = Cat,
            ["tmpl.h"] = "#ifdef TNAME\nint CAT(TNAME, _get)(void) { return 1; }\n#endif\n",
            ["a.h"] = "#ifndef A_H\n#define A_H\n#include \"b.h\"\n#include \"tmpl.h\"\n#endif\n",
            ["b.h"] = "#ifndef B_H\n#define B_H\n#include \"a.h\"\n#endif\n",
        };
        var unit = "#include \"cat.h\"\n#if 0\n#include \"a.h\"\n#endif\n#define TNAME r5\n#include \"b.h\"\n#undef TNAME\n";
        Assert.Contains("r5_get", Names(unit, headers));
    }
    [Fact]
    public void ATemplateIncludingItselfUnderAnIf_IsNotWalkedWithoutEnd()
    {
        // A maybe-include adds no depth: tmpl.h reaching itself twice under an #if walked 2, 4, 8... copies of itself
        // and never stopped.
        var headers = new Dictionary<string, string>
        {
            ["cat.h"] = Cat,
            ["tmpl.h"] = "int CAT(TNAME, _get)(void) { return 1; }\n#if MORE\n#include \"tmpl.h\"\n#include \"tmpl.h\"\n#endif\n",
        };
        var unit = "#include \"cat.h\"\n#define TNAME r5\n#include \"tmpl.h\"\n";
        Assert.Equal(new[] { "r5_get" }, Names(unit, headers));
    }

    /// <summary>Timed: run alone, after the parallel tests (beside them it took 5.3 s in CI, over its 5 s).</summary>
    [Collection(nameof(TemplateWalkMemoryTests))]
    public sealed class Timed
    {
        [Fact]
        public void AChainIncludedByDefinedNames_FirstUnderAnIf_IsMatchedNotWalkedAgainAndAgain()
        {
            // Each header defines the name of the next and includes it by that name, and the template; the unit walks the
            // chain under an #if first, then for several values of TNAME. The defines of the walk under the #if went back
            // past every walk as alternatives, so every walk took each as free and none matched again: the chain walked
            // anew at every include (death-lite: a 1.3 KB unit 14 s and more, 1.0.289).
            const int n = 1000;
            var headers = new Dictionary<string, string> { ["cat.h"] = Cat, ["tmpl.h"] = "#ifdef TNAME\nint CAT(TNAME, _get)(void) { return 1; }\n#endif\n" };
            for (var i = 0; i < n; i++)
            {
                var sb = new StringBuilder($"#ifndef CH{i}_H\n#define CH{i}_H\n#include \"tmpl.h\"\n");
                for (var k = 0; k < 40; k++) sb.Append($"#define CH{i}_R{k} (CH{i}_BASE + {k * 4})\n");
                if (i + 1 < n) sb.Append($"#define CH_NEXT_{i} \"ch{i + 1}.h\"\n#include CH_NEXT_{i}\n");
                headers[$"ch{i}.h"] = sb.Append("#endif\n").ToString();
            }
            var unit = new StringBuilder("#include \"cat.h\"\n#if MAYBE\n#include \"ch0.h\"\n#endif\n");
            for (var t = 0; t < 8; t++) unit.Append($"#define TNAME r{t}\n#if V{t}\n#include \"ch0.h\"\n#endif\n#undef TNAME\n#if W{t}\n#include \"ch{n / 2}.h\"\n#endif\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var names = Names(unit.ToString(), headers);
            Assert.Equal(new[] { "TNAME_get" }.Concat(Enumerable.Range(0, 8).Select(t => $"r{t}_get")).ToArray(), names.Distinct().ToArray());   // TNAME_get: the walk under MAYBE (#ifdef TNAME not evaluated)
            Assert.True(sw.ElapsedMilliseconds < 5_000, $"{sw.ElapsedMilliseconds} ms (1.0.289: 10 s)");
        }
    }
}
