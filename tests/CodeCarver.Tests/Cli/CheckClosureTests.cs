using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A stage closes over what its emitted code uses as the link check reads it: a definition the graph left out (a use
/// it did not model) is kept and the stage emitted again, and the addition is counted by cause.
/// </summary>
public sealed class CheckClosureTests
{
    [Fact]
    public void UseTheGraphMissed_IsKept_AndCounted()
    {
        using var t = new TreeCarve();
        // The graph reads no use at file scope outside an initializer; the check sees modd_helper used.
        t.W("main.c", "int modd_helper(void){ return 1; }\n_Static_assert(sizeof(&modd_helper) > 0, \"x\");\n"
                    + "int modd_unused(void){ return 2; }\nint main(void){ return 0; }\n");
        var (code, o, e) = t.Carve(extraToml: "[stages.p]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o + e);
        var carved = File.ReadAllText(Path.Combine(t.Root, "out", "p", "carved", "main.c"));
        Assert.Contains("int modd_helper(void)", carved);
        Assert.DoesNotContain("modd_unused", carved);
        var summary = File.ReadAllText(Path.Combine(t.Root, "out", "p", "codecarver", "summary.txt"));
        Assert.Contains("stage0.verify.keptByCheck.prunedFromKeptFile = 1", summary);
        Assert.Contains("KEPT modd_helper", File.ReadAllText(Path.Combine(t.Root, "out", "p", "codecarver", "verify.txt")));
        // What the graph made of the use, and where it sits: counts that name the missed shape.
        Assert.Contains("stage0.verify.keptByCheck.prunedFromKeptFile.useNotModelled = 1", summary);
        Assert.Contains("stage0.verify.keptByCheck.prunedFromKeptFile.use.atFileScope = 1", summary);
        Assert.Contains("stage0.verify.keptByCheck.prunedFromKeptFile.use.reference = 1", summary);
        Assert.Contains("stage0.verify.keptByCheck = 1", summary);   // the breakdown is not added to the total
    }

    [Theory]
    [InlineData("void f(void)\n{\n    tgt(1);\n}\n", 3, "inFunctionBody", "call")]
    [InlineData("static const struct ops o = {\n    tgt,\n};\n", 2, "inInitializer", "reference")]
    [InlineData("void (*p)(void) =\n    tgt;\n", 2, "inInitializer", "reference")]
    [InlineData("#define RUN(x) \\\n    tgt(x)\n", 2, "inMacroDefinition", "call")]
    [InlineData("struct s {\n    int tgt;\n};\n", 2, "inOtherBlock", "reference")]
    [InlineData("_Static_assert(sizeof(&tgt) > 0, \"x\");\n", 1, "atFileScope", "reference")]
    [InlineData("void f(void)\n{\n    other();\n}\n", 3, "inFunctionBody", "nameNotOnLine")]
    public void UseShape_SaysWhereTheUseSits(string text, int line, string place, string form)
    {
        var shapes = CodeCarver.Core.Diagnostics.UseShape.Describe(text, line, "tgt");
        Assert.Equal(place, shapes[0]);
        Assert.Equal(form, shapes[1]);
        Assert.DoesNotContain("inConditional", shapes);
    }

    [Fact]
    public void UseShape_CountsAnIfBlock_ButNotTheIncludeGuard()
    {
        const string guarded = "#ifndef G_H\n#define G_H\nvoid f(void) { tgt(); }\n#endif\n";
        Assert.DoesNotContain("inConditional", CodeCarver.Core.Diagnostics.UseShape.Describe(guarded, 3, "tgt"));
        const string inIf = "#ifndef G_H\n#define G_H\n#if FEATURE\nvoid f(void) { tgt(); }\n#endif\n#endif\n";
        Assert.Contains("inConditional", CodeCarver.Core.Diagnostics.UseShape.Describe(inIf, 4, "tgt"));
        Assert.Equal("lineNotFound", CodeCarver.Core.Diagnostics.UseShape.Describe("x\n", 9, "tgt")[0]);
    }

    /// <summary>A header compiles only where it is included: keeping one that defines the name links nothing, so the
    /// failure stays a failure.</summary>
    [Fact]
    public void DefinitionInAnUnincludedHeader_StillFails()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int modd_helper(void);\nint main(void){ return modd_helper(); }\n")
         .W("lib.h", "int modd_helper(void) { return 1; }\nint modd_b(void) { return 2; }\nint modd_c(void) { return 3; }\n");
        var (code, o, e) = t.Carve(extraToml: "[advanced]\nmaxSymbolsPerFile = 2\n");
        Assert.True(code == 3, o + e);
        Assert.DoesNotContain("keptByCheck =", t.Summary.Replace("keptByCheck = 0", ""));
        Assert.DoesNotContain("KEPT", t.VerifyLog);   // listed only as what was kept
    }

    /// <summary>A later stage of the same kind starts from the earlier one's additions and reports them too.</summary>
    [Fact]
    public void LaterStageOfTheSameKind_ReportsInheritedAdditions()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int modd_helper(void){ return 1; }\n_Static_assert(sizeof(&modd_helper) > 0, \"x\");\nint main(void){ return 0; }\n");
        var (code, o, e) = t.Carve(extraToml: "[stages.a]\ncarveSourceFileContents = true\n[stages.m]\ncarveSourceFileContents = true\ncarveHeaderFileContents = true\n");
        Assert.True(code == 0, o + e);
        foreach (var s in new[] { "a", "m" })
        {
            var summary = File.ReadAllText(Path.Combine(t.Root, "out", s, "codecarver", "summary.txt"));
            Assert.Contains("verify.keptByCheck.prunedFromKeptFile = 1", summary);
            Assert.Contains("KEPT modd_helper", File.ReadAllText(Path.Combine(t.Root, "out", s, "codecarver", "verify.txt")));
        }
    }
}
