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

    [Fact]
    public void ClosedWorld_UnknownMacro_KeepsBothIfdefBranches()
    {
        // eval-#12: a macro that VARIES across TUs is marked UNKNOWN; even under closed-world its #ifdef
        // branches must all stay live (contrast: an ABSENT macro under closed-world drops the #ifdef branch).
        var unknown = new MacroTable(); unknown.MarkUnknown("FEATURE");
        var dead = PreprocessorScanner.DeadLineMap("#ifdef FEATURE\nA\n#else\nB\n#endif\n", unknown, closedWorld: true);
        Assert.False(dead[2]);  // #ifdef branch (A) live
        Assert.False(dead[4]);  // #else branch (B) live

        var absent = new MacroTable();
        var dead2 = PreprocessorScanner.DeadLineMap("#ifdef FEATURE\nA\n#else\nB\n#endif\n", absent, closedWorld: true);
        Assert.True(dead2[2]);   // absent + closed-world => #ifdef dead
        Assert.False(dead2[4]);  // #else live
    }

    [Fact]
    public void ClosedWorld_UnknownMacro_InIfExpression_IsUnknown()
    {
        var t = new MacroTable(); t.MarkUnknown("VER");
        // VER unknown => the comparison can't resolve => Unknown (both branches kept) even closed-world.
        Assert.Equal(Tri.Unknown, PreprocessorScanner.EvaluateCondition("VER == 1", t, closedWorld: true));
        // an absent macro under closed-world is 0 => the == resolves to False (definite)
        Assert.Equal(Tri.False, PreprocessorScanner.EvaluateCondition("VER == 1", new MacroTable(), closedWorld: true));
    }

    [Theory]
    [InlineData("defined(Z)")]                        // absent macro: unknown in open world (a header might define it)
    [InlineData("defined(A) && defined(Z)")]          // A true but Z unknown => unknown
    [InlineData("UNDEFINED_THING")]                   // bare absent identifier: unknown in open world
    [InlineData("1 ? 2 : 3")]                          // unsupported syntax => unknown (conservative)
    [InlineData("0 || defined(Z)")]                   // false OR unknown => unknown (must NOT collapse to 0)
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
    public void DeadLineMap_Ifndef_KnownDefine_TakesElseBranch()
    {
        // #ifndef is the include-guard/feature-negation form. With X defined, the #ifndef body is dead and the
        // #else is live — the mirror of #ifdef. (Absent X in open world would keep both; pinned elsewhere.)
        const string text = "#ifndef X\nint first_include;\n#else\nint already;\n#endif\n";
        var dead = PreprocessorScanner.DeadLineMap(text, Defines("X"));
        Assert.True(dead[2]);   // X defined => #ifndef body dead
        Assert.False(dead[4]);  // #else live
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

    [Fact]
    public void DeadLineMap_BackslashContinuedDirective_IsJoinedAndEvaluatedAsOne()
    {
        // A directive split across physical lines with trailing '\' must be joined before evaluation.
        // If the continuation is NOT joined, the #if sees only "defined(A) &&" (a trailing-operator parse
        // error -> Unknown -> BOTH branches kept), so the #else would wrongly stay live. Asserting the
        // #else is DEAD proves the two physical lines were stitched into one condition.
        const string text =
            "#define A 1\n" +           // 1
            "#define B 1\n" +           // 2
            "#if defined(A) && \\\n" +  // 3  (continues ->)
            "    defined(B)\n" +        // 4
            "int live;\n" +             // 5
            "#else\n" +                 // 6
            "int dead;\n" +             // 7
            "#endif\n";                 // 8
        var dead = PreprocessorScanner.DeadLineMap(text, Defines());
        Assert.False(dead[5]); // taken branch live
        Assert.True(dead[7]);  // #else dead -> condition resolved True -> continuation was joined
    }

    [Theory]
    [InlineData("2 * 3 == 6", Tri.True)]
    [InlineData("6 / 2 == 3", Tri.True)]
    [InlineData("7 % 3 == 1", Tri.True)]
    [InlineData("2 * 3 == 7", Tri.False)]
    [InlineData("1 / 0", Tri.Unknown)]   // divide-by-zero is not resolvable -> Unknown (conservative keep)
    [InlineData("5 % 0", Tri.Unknown)]   // mod-by-zero likewise -> Unknown, never a crash
    public void EvaluateCondition_MulDivMod(string expr, Tri expected)
    {
        Assert.Equal(expected, PreprocessorScanner.EvaluateCondition(expr, Defines()));
    }

    [Fact]
    public void EvaluateCondition_MacroExpandsThroughAnotherMacro()
    {
        // #define A B / #define B 1 / #if A  -> A must resolve THROUGH B to 1 (True). This is the indirect
        // identifier-resolution path (a macro whose value is itself a macro/expression).
        var t = MacroTable.FromDefines(new[] { "A=B", "B=1" });
        Assert.Equal(Tri.True, PreprocessorScanner.EvaluateCondition("A", t));
    }

    [Theory]
    [InlineData("A=B", "B=A")]   // mutual cycle A->B->A
    [InlineData("A=A", null)]    // direct self-reference
    public void EvaluateCondition_CyclicMacro_TerminatesAsUnknown(string d1, string? d2)
    {
        // A self/mutually referential macro must not loop forever; the recursion-depth guard bottoms out to
        // Unknown (unresolvable) so both branches are conservatively kept.
        var defines = d2 is null ? new[] { d1 } : new[] { d1, d2 };
        var t = MacroTable.FromDefines(defines);
        Assert.Equal(Tri.Unknown, PreprocessorScanner.EvaluateCondition("A", t));
    }

    [Theory]
    [InlineData("@")]        // a bare junk token
    [InlineData("1 + + *")]  // operator soup
    [InlineData("(1")]       // unbalanced paren
    public void EvaluateCondition_MalformedExpression_IsConservativelyUnknown(string expr)
    {
        // A condition the scanner can't parse must NEVER resolve to a definite branch (that would drop code).
        // It falls back to Unknown so the branch is kept. (Soundness floor for garbage/unsupported syntax.)
        Assert.Equal(Tri.Unknown, PreprocessorScanner.EvaluateCondition(expr, Defines("A")));
    }

    [Fact]
    public void DeadLineMap_UndefInFile_ClosedWorld_ReopensBranchAsUndefined()
    {
        // #undef inside the scanned file must actually remove the macro: after it, a closed-world #ifdef on
        // that name resolves to undefined (branch dead). If #undef were ignored, X would still be defined and
        // the #ifdef branch would be the LIVE one -- the opposite result -- so this pins #undef handling.
        const string text =
            "#define X 1\n" + // 1
            "#undef X\n" +    // 2
            "#ifdef X\n" +    // 3
            "int a;\n" +      // 4
            "#else\n" +       // 5
            "int b;\n" +      // 6
            "#endif\n";       // 7
        var dead = PreprocessorScanner.DeadLineMap(text, Defines(), closedWorld: true);
        Assert.True(dead[4]);   // X undefined after #undef -> #ifdef branch dead
        Assert.False(dead[6]);  // #else live
    }

    [Fact]
    public void DeadLineMap_ElifChain_FalseAndUnknownBranches()
    {
        // Exercises #elif whose condition is definitely-FALSE (dropped) and definitely-UNKNOWN (kept), in a
        // chain where no earlier branch was taken. Only the unknown elif and the trailing #else survive.
        const string text =
            "#if 0\n" +           // 1  not taken (False)
            "int a;\n" +          // 2  dead
            "#elif 0\n" +         // 3  elif False
            "int b;\n" +          // 4  dead
            "#elif defined(UNK)\n" + // 5  elif Unknown (open world, UNK absent)
            "int c;\n" +          // 6  kept
            "#else\n" +           // 7
            "int d;\n" +          // 8  kept (no branch definitely taken)
            "#endif\n";           // 9
        var dead = PreprocessorScanner.DeadLineMap(text, Defines());
        Assert.True(dead[2]);   // #if 0 body dead
        Assert.True(dead[4]);   // #elif 0 body dead
        Assert.False(dead[6]);  // #elif defined(UNK) kept (unknown)
        Assert.False(dead[8]);  // #else kept
    }
}
