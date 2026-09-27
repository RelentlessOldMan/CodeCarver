using System.Text;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// Seeded, reproducible fuzzing of the front-end's core robustness contract: a malformed, truncated, or
/// hostile source file must NEVER throw out of BuildGraph — the front-end catches per-file extraction
/// failures and force-keeps that file instead of sinking the whole-repo carve. (This is the class of
/// input that produced the real AV crash; HostileInputTests pins specific shapes, this covers the space.)
/// Fixed seeds keep failures reproducible; the generator biases toward the things that break C parsers:
/// unbalanced braces/parens, truncation mid-token, stray preprocessor directives, deep nesting, huge
/// identifiers, and raw non-UTF-ish bytes.
/// </summary>
public sealed class FuzzTests
{
    private static string Generate(int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder();
        var tokens = new[]
        {
            "int ", "void ", "static ", "return ", "if(", "for(;;)", "{", "}", "(", ")", ";",
            "#define ", "#ifdef X", "#endif", "#include \"", "struct ", "typedef ", "*", ",",
            "0x", "foo", "\\\n", "/*", "*/", "//", "\"", "'", "__attribute__((", "))",
        };
        var n = rng.Next(1, 120);
        for (var i = 0; i < n; i++)
        {
            sb.Append(tokens[rng.Next(tokens.Length)]);
            if (rng.Next(6) == 0) sb.Append(new string('a', rng.Next(1, 300))); // long identifier run
            if (rng.Next(10) == 0) sb.Append((char)rng.Next(1, 256));           // stray byte
            if (rng.Next(4) == 0) sb.Append('\n');
        }
        // Half the time, truncate mid-stream to simulate a cut-off/partial file.
        var text = sb.ToString();
        return rng.Next(2) == 0 && text.Length > 4 ? text[..rng.Next(1, text.Length)] : text;
    }

    [Fact]
    public void CFrontEnd_NeverThrows_OnRandomMalformedInput()
    {
        for (var seed = 0; seed < 250; seed++)
        {
            var text = Generate(seed);
            using var fe = new CFrontEnd();
            // The whole contract: no exception escapes, and a graph always comes back. A file that can't be
            // extracted is force-kept (surfaced via Warnings/ForceKeepFiles), never a throw.
            var ex = Record.Exception(() => fe.BuildGraph(new[] { ("fuzz.c", text) }));
            Assert.True(ex is null, $"seed {seed} threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Fact]
    public void CppFrontEnd_NeverThrows_OnRandomMalformedInput()
    {
        for (var seed = 1000; seed < 1150; seed++)
        {
            var text = Generate(seed);
            using var fe = new CppFrontEnd();
            var ex = Record.Exception(() => fe.BuildGraph(new[] { ("fuzz.cpp", text) }));
            Assert.True(ex is null, $"seed {seed} threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }
}
