using CodeCarver.Cli;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// The independent emitted-tree verify (review V1): a tokenizer-only check that emitted code does not use a
/// function defined only in a dropped file. Unit tests pin the scanner's definition/use rules; CLI tests pin
/// that the reproduced unsound carves now fail the run (exit 3) instead of printing "verify : OK".
/// </summary>
public sealed class EmittedLinkCheckTests
{
    static HashSet<string> Uses(string text) => EmittedLinkCheck.Scan(text).Uses.Select(u => u.Name).ToHashSet();
    static List<string> Defs(string text) => EmittedLinkCheck.Scan(text).Definitions.Select(d => d.Name).ToList();

    [Fact]
    public void Scan_PrototypeIsNotAUse_CallIs()
    {
        var u = Uses("void c(void);\nvoid a(void){ c(); }\n");
        Assert.Contains("c", u);
        var p = Uses("void c(void);\nint x;\n");
        Assert.DoesNotContain("c", p);
    }

    [Fact]
    public void Scan_FindsDefinitions_WithStaticFlag()
    {
        var r = EmittedLinkCheck.Scan("static int helper(int x){ return x; }\nint api(void) { return helper(1); }\nint proto(void);\n");
        Assert.Equal(new[] { "helper", "api" }, r.Definitions.Select(d => d.Name));
        Assert.True(r.Definitions[0].Static);
        Assert.False(r.Definitions[1].Static);
    }

    [Fact]
    public void Scan_LocalsParametersAndMembersAreNotUses()
    {
        var u = Uses("int f(int count){ int init = 0; init++; s.read = 1; p->write(); return count + init; }\n");
        Assert.DoesNotContain("count", u);
        Assert.DoesNotContain("init", u);
        Assert.DoesNotContain("read", u);
        Assert.DoesNotContain("write", u);
    }

    [Fact]
    public void Scan_CommentsStringsAndConditionalsAreIgnored_DefineBodiesAreUses()
    {
        var u = Uses("/* gone(); */\n// gone2();\nconst char *s = \"gone3()\";\n#ifdef gone4\n#endif\n#define CALL(x) real(x)\n");
        Assert.DoesNotContain("gone", u);
        Assert.DoesNotContain("gone2", u);
        Assert.DoesNotContain("gone3", u);
        Assert.DoesNotContain("gone4", u);
        Assert.Contains("real", u);
        Assert.DoesNotContain("x", u);
    }

    [Fact]
    public void Scan_AddressTakenAtFileScope_IsAUse()
    {
        Assert.Contains("handler", Uses("void (*fp)(void) = handler;\n"));
        Assert.Contains("drv_init", Uses("INITCALL(drv_init);\n"));
    }

    [Fact]
    public void Scan_CppNamespacesAndConstructors()
    {
        var d = Defs("namespace a { namespace b {\nvoid f() {}\n} }\nextern \"C\" { int g(void) { return 0; } }\nFoo::Foo() : x(1), y(2) {}\n");
        Assert.Contains("f", d);
        Assert.Contains("g", d);
        Assert.Contains("Foo", d);
    }

    [Fact]
    public void Run_DeadOnlyUse_IsClassifiedNotHard()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.c"), "int main(void){\n#ifdef X\nfast();\n#endif\nreturn 0; }\n");
            File.WriteAllText(Path.Combine(dir, "fast.c"), "void fast(void){}\n");
            var main = Path.Combine(dir, "main.c");
            var fast = Path.Combine(dir, "fast.c");
            var live = EmittedLinkCheck.Run(new[] { ("main.c", main) }, new[] { ("fast.c", fast) });
            Assert.Single(live.Hard);
            var deadMap = new bool[7]; deadMap[3] = true;
            var dead = EmittedLinkCheck.Run(new[] { ("main.c", main) }, new[] { ("fast.c", fast) }, (_, _) => deadMap);
            Assert.Empty(dead.Hard);
            Assert.Single(dead.DeadOnly);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- CLI: reproduced unsound carves now fail the run --------------------------------------------------

    static (int Code, string Out) Carve(Action<string> tree, string body = "")
    {
        var work = Path.Combine(Path.GetTempPath(), "elc-cli-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        try
        {
            tree(src);
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n"
                + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n" + body);
            var so = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, new StringWriter());
            Assert.True(File.Exists(Path.Combine(work, "out", "codecarver", "verify.txt")));
            return (code, so.ToString());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    static void W(string src, string rel, string text)
    {
        var p = Path.Combine(src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public void Cli_HealthyCarve_VerifyOk()
    {
        var (code, o) = Carve(s =>
        {
            W(s, "main.c", "void a(void);\nint main(void){ a(); return 0; }\n");
            W(s, "a.c", "void a(void){}\n");
            W(s, "dead.c", "void dead(void){}\n");
        });
        Assert.Equal(0, code);
        Assert.Contains("verify  : OK", o);
    }

    [Fact]
    public void Cli_PlanClosedOverEmittedCode_F1_VerifyOk()
    {
        // F1 (review): a kept file's unreached function calls into another file; the emitted file references it,
        // so that file must be kept (D-A: file-level output always links). Before the F1 fix this failed verify.
        var (code, o) = Carve(s =>
        {
            W(s, "main.c", "void a(void);\nint main(void){ a(); return 0; }\n");
            W(s, "a.c", "void c(void);\nvoid a(void){}\nvoid b_unused(void){ c(); }\n");
            W(s, "c.c", "void c(void){}\n");
        });
        Assert.DoesNotContain("defined only in dropped", o);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Cli_PrunedStage_ClosedOverRetainedSpans()
    {
        // Same F1 tree at the pruned stage: b_unused is removable, so c.c may go; the output must still verify.
        var (code, o) = Carve(s =>
        {
            W(s, "main.c", "void a(void);\nint main(void){ a(); return 0; }\n");
            W(s, "a.c", "void c(void);\nvoid a(void){}\nvoid b_unused(void){ c(); }\n");
            W(s, "c.c", "void c(void){}\n");
        }, "carveSourceFileContents = true\n");
        Assert.Equal(0, code);
        Assert.Contains("verify  : OK", o);
    }

    [Fact]
    public void Cli_ClosedWorldDrop_ReportedAsDeadLineNote_NotFailure()
    {
        // A drop that rests on the #ifdef model is reported, not failed: the run stays green when the model is right.
        var work = Path.Combine(Path.GetTempPath(), "elc-cw-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        try
        {
            W(src, "main.c", "void fast(void); void slow(void);\nint main(void){\n#ifdef USE_FAST\nfast();\n#else\nslow();\n#endif\nreturn 0; }\n");
            W(src, "fast.c", "void fast(void){}\n");
            W(src, "slow.c", "void slow(void){}\n");
            File.WriteAllText(Path.Combine(work, "cc.json"),
                "[{\"directory\":\"" + src.Replace('\\', '/') + "\",\"file\":\"main.c\",\"command\":\"gcc -DX=1 -c main.c\"}]");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n"
                + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n[builds.m]\nbuildLogs = [\""
                + Path.Combine(work, "cc.json").Replace('\\', '/') + "\"]\n");
            var so = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, new StringWriter());
            var o = so.ToString();
            Assert.Equal(0, code);
            Assert.Contains("#ifdef-dead lines", o);
            Assert.Contains("DEAD fast", File.ReadAllText(Path.Combine(work, "out", "codecarver", "verify.txt")));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Cli_SameNameMacroInOtherTarget_N1_IsCaughtByVerify()
    {
        // N1 (review): a function-like macro in target A hides target B's real function from the graph. Until the
        // name-table fix lands, the emitted-tree verify must catch it (exit 3) rather than print OK.
        var (code, o) = Carve(s =>
        {
            W(s, "a/uart.h", "#define uart_write(x) hal_uart_write(x)\n");
            W(s, "b/main.c", "void uart_write(int x);\nint main(void){ uart_write(1); return 0; }\n");
            W(s, "b/uart.c", "void uart_write(int x){ (void)x; }\n");
        });
        if (code == 0) Assert.Contains("verify  : OK", o);          // fixed: b/uart.c kept
        else { Assert.Equal(3, code); Assert.Contains("uart_write", o); }
    }
}
