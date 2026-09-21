using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

public class PreprocessorScannerTests
{
    private static MacroTable Defines(params string[] d) => MacroTable.FromDefines(d);

    [Theory]
    [InlineData("defined(A)", true)]                 // A is a known define
    [InlineData("!defined(A)", false)]
    [InlineData("defined(A) && defined(B)", true)]
    [InlineData("defined(A) || defined(Z)", true)]   // short-circuit: A true => whole true even if Z unknown
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void EvaluateCondition_DefiniteCases(string expr, bool expected)
    {
        var tri = PreprocessorScanner.EvaluateCondition(expr, Defines("A", "B"));
        Assert.Equal(expected ? Tri.True : Tri.False, tri);
    }

    [Theory]
    [InlineData("defined(Z)")]                        // absent macro: unknown in open world (a header might define it)
    [InlineData("defined(A) && defined(Z)")]          // A true but Z unknown => unknown
    [InlineData("UNDEFINED_THING")]                   // bare absent identifier: unknown in open world
    [InlineData("1 ? 2 : 3")]                          // unsupported syntax => unknown (conservative)
    public void EvaluateCondition_OpenWorld_Unknown(string expr)
    {
        Assert.Equal(Tri.Unknown, PreprocessorScanner.EvaluateCondition(expr, Defines("A")));
    }

    [Theory]
    [InlineData("defined(Z)", false)]                 // closed world: absent => definitely undefined
    [InlineData("!defined(Z)", true)]
    [InlineData("UNDEFINED_THING", false)]
    public void EvaluateCondition_ClosedWorld_Definite(string expr, bool expected)
    {
        var tri = PreprocessorScanner.EvaluateCondition(expr, Defines("A"), closedWorld: true);
        Assert.Equal(expected ? Tri.True : Tri.False, tri);
    }

    [Theory]
    [InlineData("VER >= 3", "VER=5", Tri.True)]
    [InlineData("VER >= 3", "VER=2", Tri.False)]
    [InlineData("VER == 504", "VER=504", Tri.True)]
    [InlineData("(A + 1) > 2", "A=2", Tri.True)]
    public void EvaluateCondition_Arithmetic(string expr, string define, Tri expected)
    {
        Assert.Equal(expected, PreprocessorScanner.EvaluateCondition(expr, MacroTable.FromDefines(new[] { define })));
    }

    [Fact]
    public void DeadLineMap_Ifdef_KnownDefine_TakesBranch()
    {
        const string text = "#ifdef A\nint live;\n#else\nint dead;\n#endif\n";
        var dead = PreprocessorScanner.DeadLineMap(text, Defines("A"));
        Assert.False(dead[2]); // int live;
        Assert.True(dead[4]);  // int dead;  (A definitely defined => #else dead)
    }

    [Fact]
    public void DeadLineMap_ElifChain_TakenBranchKillsLaterBranches()
    {
        const string text =
            "#if defined(WIN)\n" +      // 1  (WIN absent -> unknown -> kept, not taken)
            "int win;\n" +              // 2
            "#elif defined(LINUX)\n" +  // 3  (LINUX known -> taken)
            "int lin;\n" +              // 4
            "#else\n" +                 // 5
            "int other;\n" +            // 6
            "#endif\n";                 // 7
        var dead = PreprocessorScanner.DeadLineMap(text, Defines("LINUX"));
        Assert.False(dead[2]);  // win kept (open world can't prove WIN undefined)
        Assert.False(dead[4]);  // linux live
        Assert.True(dead[6]);   // else dead (a branch was definitely taken)
    }

    [Fact]
    public void DeadLineMap_ClosedWorld_DropsAbsentPlatform()
    {
        const string text = "#if defined(WIN)\nint win;\n#else\nint other;\n#endif\n";
        var dead = PreprocessorScanner.DeadLineMap(text, Defines("LINUX"), closedWorld: true);
        Assert.True(dead[2]);   // win dropped: closed world proves WIN undefined
        Assert.False(dead[4]);  // other live
    }

    [Fact]
    public void DeadLineMap_UnknownCondition_KeepsBothBranches()
    {
        const string text = "#if 1 ? 2 : 3\nint a;\n#else\nint b;\n#endif\n";
        var dead = PreprocessorScanner.DeadLineMap(text, Defines());
        Assert.False(dead[2]);
        Assert.False(dead[4]);
    }

    [Fact]
    public void DeadLineMap_InFileDefine_AffectsLaterCondition()
    {
        const string text = "#define FEATURE 1\n#if FEATURE\nint on;\n#else\nint off;\n#endif\n";
        var dead = PreprocessorScanner.DeadLineMap(text, Defines());
        Assert.False(dead[3]); // int on;
        Assert.True(dead[5]);  // int off;
    }

    [Fact]
    public void DeadLineMap_If0_AlwaysDead()
    {
        const string text = "#if 0\nint dead;\n#endif\nint live;\n";
        var dead = PreprocessorScanner.DeadLineMap(text, Defines());
        Assert.True(dead[2]);   // #if 0 body always dead, even open world
        Assert.False(dead[4]);
    }
}
