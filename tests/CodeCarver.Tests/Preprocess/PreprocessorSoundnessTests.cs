using CodeCarver.Cli;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>Review step 2 (PP1–PP5): closed-world resolves only what the build told us, and the scanner
/// recognises every directive spelling.</summary>
public sealed class PreprocessorSoundnessTests
{
    static bool[] Dead(string text, MacroTable t, bool closed = true) => PreprocessorScanner.DeadLineMap(text, t, closed);

    [Fact]
    public void DeadLineMap_IfWithAttachedParen_IsADirective()
    {
        // PP5: "#if(" used to push no frame, so its #else flipped the ENCLOSING frame.
        var text = "#ifndef APP_H\n#define APP_H\n#if( CFG_A == 1 )\na();\n#else\nc();\n#endif\n#endif\n";
        var d = Dead(text, MacroTable.FromDefines(new[] { "CFG_A=0" }));
        Assert.True(d[4]);    // a(): dead
        Assert.False(d[6]);   // c(): live
    }

    [Theory]
    [InlineData("#if 0\nx\n#endif/*x*/\ny\n")]
    [InlineData("#if 0\nx\n#endif// x\ny\n")]
    public void DeadLineMap_DirectiveSpellings(string text)
    {
        var d = Dead(text, new MacroTable());
        Assert.True(d[2]);
        Assert.False(d[4]);   // after the #endif: live
    }

    [Fact]
    public void DeadLineMap_IfBangDefined_IsADirective()
    {
        var d = Dead("#if!defined(Z)\na\n#else\nb\n#endif\n", new MacroTable());
        Assert.False(d[2]);
        Assert.True(d[4]);
    }

    [Fact]
    public void DirectiveInsideBlockComment_IsIgnored()
    {
        var d = Dead("#if 1\n/*\n#endif\n*/\nx\n#else\ny\n#endif\n", new MacroTable());
        Assert.False(d[5]);   // x live
        Assert.True(d[7]);    // y dead: the commented #endif did not close the #if
    }

    [Fact]
    public void DefineUnderUnknownCondition_IsUncertain()
    {
        // PP3: MAYBE is unknown (open world), so USE_FAST is only possibly defined: both branches live.
        var text = "#ifdef MAYBE\n#define USE_FAST 1\n#endif\n#ifdef USE_FAST\nfast();\n#else\nslow();\n#endif\n";
        var d = Dead(text, MacroTable.FromDefines(new[] { "OTHER=1" }), closed: false);
        Assert.False(d[5]);
        Assert.False(d[7]);
    }

    [Fact]
    public void UndefUnderUnknownCondition_IsUncertain()
    {
        var text = "#ifdef MAYBE\n#undef FEAT\n#endif\n#ifdef FEAT\nf();\n#else\ng();\n#endif\n";
        var t = MacroTable.FromDefines(new[] { "FEAT" });
        t.MarkUnknown("MAYBE");
        var d = Dead(text, t, closed: true);
        Assert.False(d[5]);
        Assert.False(d[7]);
    }

    [Fact]
    public void CertainDefine_InsideCertainBranch_StillResolves()
    {
        var text = "#ifdef A\n#define B 1\n#endif\n#ifdef B\nb();\n#else\nnb();\n#endif\n";
        var d = Dead(text, MacroTable.FromDefines(new[] { "A" }));
        Assert.False(d[5]);
        Assert.True(d[7]);
    }

    [Fact]
    public void IncludeGuard_DoesNotMakeHeaderDefinesUncertain()
    {
        // The guard macro is #defined in the tree (ambient), but the guard is certainly true on first inclusion.
        var t = MacroTable.FromDefines(new[] { "X=1" });
        t.Ambient = new HashSet<string> { "CFG_H" };
        var text = "#ifndef CFG_H\n#define CFG_H\n#define USE_FAST 1\n#ifdef USE_FAST\nfast();\n#else\nslow();\n#endif\n#endif\n";
        var d = Dead(text, t);
        Assert.False(d[5]);
        Assert.True(d[7]);
    }

    [Fact]
    public void ClosedWorld_AmbientMacro_IsUnknown()
    {
        // PP1: USE_FAST is #defined in some header (ambient) -> unknown, not undefined.
        var t = MacroTable.FromDefines(new[] { "X=1" });
        t.Ambient = new HashSet<string> { "USE_FAST" };
        var d = Dead("#ifdef USE_FAST\nfast();\n#else\nslow();\n#endif\n", t);
        Assert.False(d[2]);
        Assert.False(d[4]);
    }

    [Theory]
    [InlineData("__GNUC__")]
    [InlineData("__cplusplus")]
    [InlineData("_MSC_VER")]
    [InlineData("__ARM_ARCH")]
    public void ClosedWorld_ReservedBuiltinAbsent_IsUnknown(string name)
    {
        // PP2: a compiler built-in nobody probed for this TU is unknown, not undefined.
        var d = Dead($"#ifdef {name}\na();\n#else\nb();\n#endif\n", MacroTable.FromDefines(new[] { "X=1" }));
        Assert.False(d[2]);
        Assert.False(d[4]);
    }

    [Fact]
    public void CertainUndef_BeatsAmbient()
    {
        var t = new MacroTable { Ambient = new HashSet<string> { "F" } };
        var d = Dead("#undef F\n#ifdef F\na();\n#endif\n", t);
        Assert.True(d[3]);
    }

    [Theory]
    [InlineData("-1 < 0u")]
    [InlineData("0xFFFFFFFFFFFFFFFF > 0")]
    [InlineData("true")]
    public void EvaluateCondition_UnsafeToEvaluate_IsUnknown(string expr) =>
        Assert.Equal(Tri.Unknown, PreprocessorScanner.EvaluateCondition(expr, new MacroTable(), closedWorld: true));

    [Fact]
    public void FunctionLikeDashD_IsStoredUnderItsName()
    {
        Assert.Equal(Tri.True, PreprocessorScanner.EvaluateCondition("defined F", MacroTable.FromDefines(new[] { "F(x)=x" })));
    }

    // ---- CLI ---------------------------------------------------------------------------------------------

    static (int Code, string Out, string Err, string Work) Carve(Action<string> tree, string body, Action<string>? extra = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-pp-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        tree(src);
        extra?.Invoke(work);
        var cfg = Path.Combine(work, "carve.toml");
        File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n"
            + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n" + body.Replace("@WORK@", work.Replace('\\', '/')));
        var so = new StringWriter(); var se = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
        return (code, so.ToString(), se.ToString(), work);
    }

    static string CcJson(string work, string src, params string[] commands)
    {
        var p = Path.Combine(work, "cc.json");
        File.WriteAllText(p, "[" + string.Join(",", commands.Select(c =>
            $"{{\"directory\":\"{src.Replace('\\', '/')}\",\"file\":\"{c.Split(' ').Last()}\",\"command\":\"{c}\"}}")) + "]");
        return p.Replace('\\', '/');
    }

    [Fact]
    public void Cli_MacroDefinedInConfigHeader_KeepsBothBranches_PP1()
    {
        var (code, o, _, work) = Carve(s =>
        {
            File.WriteAllText(Path.Combine(s, "config.h"), "#define USE_FAST 1\n");
            File.WriteAllText(Path.Combine(s, "main.c"), "#include \"config.h\"\nvoid fast(void); void slow(void);\n"
                + "int main(void){\n#ifdef USE_FAST\nfast();\n#else\nslow();\n#endif\nreturn 0; }\n");
            File.WriteAllText(Path.Combine(s, "fast.c"), "void fast(void){}\n");
            File.WriteAllText(Path.Combine(s, "slow.c"), "void slow(void){}\n");
        }, "[builds.m]\nbuildLogs = [\"@WORK@/cc.json\"]\n",
           w => CcJson(w, Path.Combine(w, "src"), "gcc -DX=1 -c main.c", "gcc -DX=1 -c fast.c"));
        try
        {
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(work, "out", "carved", "fast.c")), o);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Cli_CompilerBuiltin_KeepsBothBranches_PP2()
    {
        var (code, o, _, work) = Carve(s =>
        {
            File.WriteAllText(Path.Combine(s, "main.c"), "void gcc_path(void); void other_path(void);\n"
                + "int main(void){\n#ifdef __GNUC__\ngcc_path();\n#else\nother_path();\n#endif\nreturn 0; }\n");
            File.WriteAllText(Path.Combine(s, "g.c"), "void gcc_path(void){}\n");
            File.WriteAllText(Path.Combine(s, "o.c"), "void other_path(void){}\n");
        }, "[builds.m]\nbuildLogs = [\"@WORK@/cc.json\"]\n",
           w => CcJson(w, Path.Combine(w, "src"), "gcc -DX=1 -c main.c"));
        try
        {
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(work, "out", "carved", "g.c")), o);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Cli_UnprobeableCompiler_IsAConfigError_PP4()
    {
        var (code, _, err, work) = Carve(s => File.WriteAllText(Path.Combine(s, "main.c"), "int main(void){return 0;}\n"),
            "[builds.m]\ncompiler = \"definitely-not-a-compiler-xyz\"\n");
        try
        {
            Assert.Equal(2, code);
            Assert.Contains("could not be probed", err);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Cli_VendorCompilerLog_IsUsed_NotSilentlyIgnored()
    {
        // BL3: an armcc log used to yield zero commands while the run claimed closed-world. Now it is parsed.
        var (code, o, _, work) = Carve(s => File.WriteAllText(Path.Combine(s, "main.c"), "int main(void){return 0;}\n"),
            "[builds.m]\nbuildLogs = [\"@WORK@/make.log\"]\n",
            w => File.WriteAllText(Path.Combine(w, "make.log"), "armcc -DX=1 -c main.c\n"));
        try
        {
            Assert.Equal(0, code);
            Assert.Contains("1 compile command(s)", o);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
