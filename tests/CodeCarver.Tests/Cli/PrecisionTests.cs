using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Review step 9 (P1–P7): tighter where the compiler's own rules allow it, and still sound.</summary>
public sealed class PrecisionTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root;
        public string Src => Path.Combine(Root, "src");
        public Work(string? parent = null)
        {
            Root = Path.Combine(parent ?? Path.GetTempPath(), "cc-prec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Src);
        }
        public void W(string rel, string text)
        {
            var p = Path.Combine(Src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        public (int Code, string Out, string Err) Carve(string body, string entry = "\"main\"")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, "out").Replace('\\', '/')}\"\n[common]\nentryPoints = [{entry}]\n" + body);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so.ToString(), se.ToString());
        }
        public bool Emitted(string rel) => File.Exists(Path.Combine(Root, "out", "carved", rel));
        public string CcJson(params (string File, string Cmd)[] cmds)
        {
            var p = Path.Combine(Root, "cc.json");
            File.WriteAllText(p, "[" + string.Join(",", cmds.Select(c =>
                $"{{\"directory\":\"{Src.Replace('\\', '/')}\",\"file\":\"{c.File}\",\"command\":\"{c.Cmd}\"}}")) + "]");
            return p.Replace('\\', '/');
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void Include_ResolvesThroughTheTusSearchPath_P1()
    {
        using var w = new Work();
        w.W("boards/a/board.h", "void a_only(void);\n");
        w.W("boards/b/board.h", "#include \"b_extra.h\"\n");
        w.W("boards/b/b_extra.h", "void b_only(void);\n");
        w.W("app/main.c", "#include <board.h>\nint main(void){ return 0; }\n");
        var log = w.CcJson(("app/main.c", "gcc -Iboards/a -c app/main.c"));
        var (code, o, e) = w.Carve($"[builds.m]\nbuildLogs = [\"{log}\"]\n");
        Assert.Equal(0, code);
        Assert.True(w.Emitted("boards/a/board.h"), o + e);
        Assert.False(w.Emitted("boards/b/b_extra.h"), o + e);
    }

    [Fact]
    public void QuotedInclude_BesideTheIncluder_WinsWithoutABuildLog_P1()
    {
        using var w = new Work();
        w.W("a/cfg.h", "#define A 1\n");
        w.W("b/cfg.h", "#include \"b_only.h\"\n");
        w.W("b/b_only.h", "#define B 1\n");
        w.W("a/main.c", "#include \"cfg.h\"\nint main(void){ return A; }\n");
        var (code, o, e) = w.Carve("");
        Assert.Equal(0, code);
        Assert.False(w.Emitted("b/b_only.h"), o + e);
    }

    [Fact]
    public void StaticFunction_IsNotReachedFromAnotherTu_P2()
    {
        using var w = new Work();
        w.W("main.c", "void helper(void);\nint main(void){ helper(); return 0; }\n");
        w.W("lib.c", "void helper(void){}\n");
        w.W("other.c", "static void helper(void){}\nvoid other_entry(void){ helper(); }\n");
        var (code, o, e) = w.Carve("");
        Assert.Equal(0, code);
        Assert.True(w.Emitted("lib.c"));
        Assert.False(w.Emitted("other.c"), o + e);
    }

    [Fact]
    public void StaticInAnIncludedC_IsStillVisibleToTheIncluder_P2()
    {
        using var w = new Work();
        w.W("main.c", "#include \"impl.c\"\nint main(void){ helper(); return 0; }\n");
        w.W("impl.c", "static void helper(void){}\n");
        var (code, o, e) = w.Carve("");
        Assert.Equal(0, code);
        Assert.True(w.Emitted("impl.c"), o + e);
    }

    [Fact]
    public void LocalNamedLikeAFunction_IsNotAnAddressTake_P3()
    {
        using var w = new Work();
        w.W("main.c", "int main(void){ int count = 0; count++; return count; }\n");
        w.W("count.c", "int count(void){ return 1; }\n");
        var (code, o, e) = w.Carve("");
        Assert.Equal(0, code);
        Assert.False(w.Emitted("count.c"), o + e);
    }

    [Fact]
    public void LocalOutOfScope_DoesNotHideALaterReference_P3()
    {
        using var w = new Work();
        w.W("main.c", "void reg(void (*)(void)); void handler(void);\nint main(void){ { int handler = 0; (void)handler; } reg(handler); return 0; }\n");
        w.W("h.c", "void handler(void){}\n");
        w.W("r.c", "void reg(void (*f)(void)){ (void)f; }\n");
        var (code, o, e) = w.Carve("");
        Assert.Equal(0, code);
        Assert.True(w.Emitted("h.c"), o + e);
    }

    [Fact]
    public void QualifiedEntryPoint_RootsOneTarget_P4()
    {
        using var w = new Work();
        w.W("t1/main.c", "int main(void){ return 1; }\n");
        w.W("t2/main.c", "int main(void){ return 2; }\n");
        var (code, o, e) = w.Carve("", "\"t1/main.c:main\"");
        Assert.Equal(0, code);
        Assert.True(w.Emitted("t1/main.c"), o + e);
        Assert.False(w.Emitted("t2/main.c"), o + e);
    }

    [Fact]
    public void AssemblyOfAnotherTarget_DoesNotRoot_WhenTheLogNamesTheBuildsOwn_P6()
    {
        using var w = new Work();
        w.W("main.c", "int main(void){ return 0; }\n");
        w.W("isr_a.c", "void A_Handler(void){}\n");
        w.W("isr_b.c", "void B_Handler(void){}\n");
        w.W("a/startup.s", ".word A_Handler\n");
        w.W("b/startup.s", ".word B_Handler\n");
        var log = w.CcJson(("main.c", "gcc -c main.c"), ("a/startup.s", "gcc -c a/startup.s"));
        var (code, o, e) = w.Carve($"[builds.m]\nbuildLogs = [\"{log}\"]\n");
        Assert.Equal(0, code);
        Assert.True(w.Emitted("isr_a.c"), o + e);
        Assert.False(w.Emitted("isr_b.c"), o + e);
    }

    [Fact]
    public void ExcludeMatchesRelativeSegments_NotTheRootsOwnPath_P7()
    {
        var parent = Path.Combine(Path.GetTempPath(), "cc-prec-tests-" + Guid.NewGuid().ToString("N"), "tests");
        Directory.CreateDirectory(parent);
        try
        {
            using var w = new Work(parent);
            w.W("main.c", "int main(void){ return 0; }\n");
            w.W("tests/t.c", "int t(void){ return 0; }\n");
            var (code, o, e) = w.Carve("excludeDirectories = [\"tests\"]\n");
            Assert.Equal(0, code);
            Assert.True(w.Emitted("main.c"), o + e);
            Assert.False(w.Emitted("tests/t.c"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(parent)!, true); } catch { } }
    }
}
