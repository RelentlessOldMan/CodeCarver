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
