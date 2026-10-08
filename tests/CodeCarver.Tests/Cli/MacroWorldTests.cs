using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Closed world (a build log is given): a macro the log doesn't -D counts as undefined UNLESS it can come from somewhere
/// we can't fully see. Every way a real build gets a macro into a file is covered here. In each case the real build takes
/// the FEAT branch and calls feat(), so feat.c must be kept and verify must pass.
/// </summary>
public sealed class MacroWorldTests
{
    const string FeatOrPlain = "void feat(void); void plain(void);\nint main(void){\n#ifdef FEAT\n  feat();\n#else\n  plain();\n#endif\n  return 0;\n}\n";

    static TreeCarve Tree(string mainPrefix)
    {
        var t = new TreeCarve()
            .W("main.c", mainPrefix + FeatOrPlain)
            .W("feat.c", "void feat(void){}\n")
            .W("plain.c", "void plain(void){}\n");
        return t;
    }

    static void AssertFeatKept(TreeCarve t, (int Code, string Out, string Err) r)
    {
        Assert.True(r.Code == 0, r.Out + r.Err + t.VerifyLog);
        Assert.True(t.Kept("feat.c"), "feat.c dropped: the FEAT branch was judged dead\n" + r.Out + r.Err);
    }

    [Theory]
    [InlineData("-I")]
    [InlineData("-I ")]
    [InlineData("-isystem ")]
    [InlineData("-iquote ")]
    [InlineData("-idirafter ")]
    public void HeaderOutsideTheRoot_FoundThroughAnIncludeDir(string flag)
    {
        using var t = Tree("#include \"cfg.h\"\n");
        t.WOut("sdk/inc/cfg.h", "#define FEAT 1\n");
        var inc = flag + t.Outside("sdk/inc");
        t.Compile("main.c", inc).Compile("feat.c", inc).Compile("plain.c", inc);
        AssertFeatKept(t, t.Carve());
    }

    /// <summary>The macro is tested only on a continuation line of the #if.</summary>
    [Fact]
    public void HeaderOutsideTheRoot_MacroTestedOnAContinuationLine()
    {
        using var t = new TreeCarve()
            .W("main.c", "#include \"cfg.h\"\nvoid feat(void); void plain(void);\nint main(void){\n#if defined(OTHER) || \\\n    defined(FEAT)\n  feat();\n#else\n  plain();\n#endif\n  return 0;\n}\n")
            .W("feat.c", "void feat(void){}\n")
            .W("plain.c", "void plain(void){}\n");
        t.WOut("sdk/inc/cfg.h", "#define FEAT 1\n");
        var inc = "-I" + t.Outside("sdk/inc");
        t.Compile("main.c", inc).Compile("feat.c", inc).Compile("plain.c", inc);
        AssertFeatKept(t, t.Carve());
    }

    [Fact]
    public void HeaderOutsideTheRoot_NestedInclude()
    {
        using var t = Tree("#include \"cfg.h\"\n");
        t.WOut("sdk/inc/cfg.h", "#include \"sub/deep.h\"\n");
        t.WOut("sdk/inc/sub/deep.h", "#  define   FEAT\n");
        var inc = "-I" + t.Outside("sdk/inc");
        t.Compile("main.c", inc).Compile("feat.c", inc).Compile("plain.c", inc);
        AssertFeatKept(t, t.Carve());
    }

    [Fact]
    public void HeaderOutsideTheRoot_ReachedByARelativeInclude()
    {
        using var t = Tree("#include \"../gen/cfg.h\"\n");
        t.WOut("gen/cfg.h", "#define FEAT 1\n");
        t.Compile("main.c").Compile("feat.c").Compile("plain.c");
        AssertFeatKept(t, t.Carve());
    }

    [Fact]
    public void HeaderOutsideTheRoot_OpenedByTheBuildTrace()
    {
        // A computed include: nothing but the trace says which header the build used.
        using var t = Tree("#include CFG_HEADER\n");
        t.WOut("gen/cfg.h", "#define FEAT 1\n");
        var flags = "-DCFG_HEADER=\\\"cfg.h\\\" -I" + t.Outside("gen");
        t.Compile("main.c", flags).Compile("feat.c", flags).Compile("plain.c", flags).TraceCompiled(t.Outside("gen/cfg.h"));
        AssertFeatKept(t, t.Carve());
    }

    [Fact]
    public void HeaderTheTraceOpened_NotOnThisMachine_MacrosStayUnknown()
    {
        // A trace captured elsewhere (WSL, CI) names a header outside the root that doesn't exist here.
        using var t = Tree("#include \"cfg.h\"\n");
        var gone = t.Outside("elsewhere/inc/cfg.h");
        var inc = "-I" + t.Outside("elsewhere/inc");
        t.Compile("main.c", inc).Compile("feat.c", inc).Compile("plain.c", inc).TraceCompiled(gone);
        var r = t.Carve();
        AssertFeatKept(t, r);
        Assert.Contains("aren't on this machine", r.Out + r.Err);
    }

    [Fact]
    public void IncludeDirMissingHere_MacrosItMightDefine_StayUnknown()
    {
        // The log names an include dir that doesn't exist on this machine (another checkout, no pathMap):
        // what it defined can't be seen, so a name nothing visible defines is unknown, not undefined.
        using var t = Tree("#include \"cfg.h\"\n");
        var inc = "-I" + t.Outside("not/here/inc");
        t.Compile("main.c", inc).Compile("feat.c", inc).Compile("plain.c", inc);
        var r = t.Carve();
        AssertFeatKept(t, r);
        Assert.Contains("include dir", r.Out + r.Err);
    }

    [Fact]
    public void HeaderInAnExcludedDirectory_StillDefinesMacros()
    {
        // An excluded directory is not carved, but the build still reads its headers.
        using var t = Tree("#include \"cfg.h\"\n");
        t.W("cfg/cfg.h", "#define FEAT 1\n");
        var inc = "-I" + t.S("cfg");
        t.Compile("main.c", inc).Compile("feat.c", inc).Compile("plain.c", inc);
        AssertFeatKept(t, t.Carve(common: "excludeDirectories = [\"cfg\"]\n"));
    }

    [Theory]
    [InlineData("#include <limits.h>\n", "#if INT_MAX > 32767")]
    [InlineData("#include <stdint.h>\n", "#if UINT32_MAX == 0xFFFFFFFF")]
    [InlineData("#include <stdint.h>\n", "#if SIZE_MAX > 0xFFFF")]
    [InlineData("#include <limits.h>\n", "#if CHAR_BIT == 8")]
    [InlineData("#include <limits.h>\n", "#ifdef PATH_MAX")]
    [InlineData("#include <stdio.h>\n", "#if defined(EOF)")]
    [InlineData("#include <stdbool.h>\n", "#if defined(bool)")]
    [InlineData("", "#ifdef linux")]
    [InlineData("", "#if defined(unix)")]
    [InlineData("", "#ifdef i386")]
    [InlineData("", "#if __has_include(<stdio.h>)")]
    [InlineData("", "#if defined(__has_include) && __has_include(\"x.h\")")]
    public void StandardHeaderAndBuiltinMacros_AreNotAssumedAbsent(string prefix, string cond)
    {
        using var t = new TreeCarve()
            .W("main.c", prefix + $"void feat(void); void plain(void);\nint main(void){{\n{cond}\n  feat();\n#else\n  plain();\n#endif\n  return 0;\n}}\n")
            .W("feat.c", "void feat(void){}\n")
            .W("plain.c", "void plain(void){}\n");
        t.Compile("main.c").Compile("feat.c").Compile("plain.c");
        AssertFeatKept(t, t.Carve());
    }

    [Theory]
    [InlineData("-DFEAT")]
    [InlineData("-D FEAT")]
    [InlineData("-DFEAT=1")]
    [InlineData("\"-DFEAT\"")]
    [InlineData("'-DFEAT=1'")]
    [InlineData("-D'FEAT=1'")]
    [InlineData("--define-macro FEAT")]
    [InlineData("-Wp,-DFEAT")]
    public void DashDSpellings_AllDefine(string flag)
    {
        using var t = Tree("");
        t.Compile("main.c", flag).Compile("feat.c", flag).Compile("plain.c", flag);
        var r = t.Carve();
        AssertFeatKept(t, r);
        Assert.False(t.Kept("plain.c"), "plain.c kept: the -D was not seen\n" + r.Out + r.Err);
    }

    [Fact]
    public void DashDAfterTheSourceFile_StillDefines()
    {
        using var t = Tree("");
        t.LogLines.Add($"gcc -c {t.S("main.c")} -DFEAT -o main.o");
        t.Compile("feat.c", "-DFEAT").Compile("plain.c", "-DFEAT");
        var r = t.Carve();
        AssertFeatKept(t, r);
        Assert.False(t.Kept("plain.c"), r.Out + r.Err);
    }

    [Fact]
    public void ResponseFile_RelativeToTheCommandDirectory()
    {
        using var t = Tree("");
        t.W("flags.rsp", "-DFEAT\n");
        t.Compile("main.c", "@flags.rsp").Compile("feat.c", "@flags.rsp").Compile("plain.c", "@flags.rsp");
        var r = t.Carve();
        AssertFeatKept(t, r);
        Assert.False(t.Kept("plain.c"), r.Out + r.Err);
    }

    [Fact]
    public void ForcedInclude_FoundThroughAnIncludeDir()
    {
        // -include is searched like #include "...": the command's dir, then -I. Here only -I finds it.
        using var t = Tree("");
        t.WOut("sdk/inc/force.h", "#define FEAT 1\n");
        var flags = $"-I{t.Outside("sdk/inc")} -include force.h";
        t.Compile("main.c", flags).Compile("feat.c", flags).Compile("plain.c", flags);
        AssertFeatKept(t, t.Carve());
    }

    [Fact]
    public void FileCompiledTwice_WithAndWithoutTheDefine_KeepsBoth()
    {
        using var t = Tree("");
        t.Compile("main.c", "-DFEAT").Compile("main.c").Compile("feat.c").Compile("plain.c");
        var r = t.Carve();
        AssertFeatKept(t, r);
        Assert.True(t.Kept("plain.c"), r.Out + r.Err);
    }

    [Fact]
    public void MacroDefinedInTheSourceBeforeAnInclude()
    {
        using var t = new TreeCarve()
            .W("main.c", "#define FEAT\n#include \"api.h\"\nint main(void){ api(); return 0; }\n")
            .W("api.h", "void feat(void); void plain(void);\n#ifdef FEAT\nstatic inline void api(void){ feat(); }\n#else\nstatic inline void api(void){ plain(); }\n#endif\n")
            .W("feat.c", "void feat(void){}\n")
            .W("plain.c", "void plain(void){}\n");
        t.Compile("main.c").Compile("feat.c").Compile("plain.c");
        AssertFeatKept(t, t.Carve());
    }

    [Fact]
    public void DefinitionGuardedInItsOwnFile_ByAMacroFromAnOutOfRootHeader()
    {
        // The guarded thing is the DEFINITION (not the call): the call is unconditional.
        using var t = new TreeCarve()
            .W("main.c", "void feat(void);\nint main(void){ feat(); return 0; }\n")
            .W("feat.c", "#include \"cfg.h\"\n#ifdef FEAT\nvoid feat(void){}\n#endif\n");
        t.WOut("sdk/cfg.h", "#define FEAT 1\n");
        var inc = "-I" + t.Outside("sdk");
        t.Compile("main.c", inc).Compile("feat.c", inc);
        AssertFeatKept(t, t.Carve());
    }
}
