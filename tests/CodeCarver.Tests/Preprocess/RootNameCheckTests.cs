using CodeCarver.Cli;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>Entry points no text holds fail before the graph is built; anything a paste could make waits for the
/// exact check after it.</summary>
public sealed class RootNameCheckTests
{
    static IReadOnlyList<string> Missing(string[] roots, params string[] texts)
    {
        var c = new RootNameCheck(roots);
        foreach (var t in texts) c.Add(t);
        return c.Missing();
    }

    [Fact]
    public void NameInTheText_IsNotMissing() =>
        Assert.Empty(Missing(new[] { "modk_start", "src/a.c:modk_stop", "ns::modk_run" },
            "void modk_start(void){}\nvoid modk_stop(void){}\nnamespace ns { void modk_run(); }\n"));

    [Fact]
    public void NameNowhere_IsMissing() =>
        Assert.Equal(new[] { "modk_guess_init" }, Missing(new[] { "modk_start", "modk_guess_init" }, "void modk_start(void){}\n"));

    [Fact]
    public void NameInsideALongerWord_IsMissing() =>
        Assert.Equal(new[] { "modk_init" }, Missing(new[] { "modk_init" }, "void modk_init2(void){}\nint xmodk_init;\n"));

    [Fact]
    public void NameAPasteCouldBuild_IsNotMissing() =>
        Assert.Empty(Missing(new[] { "entry_modk", "modk_isr_vec" },
            "#define ENTRY(f) void entry_##f(void){}\n#define V(a,b,c) a##b##c\nENTRY(modk)\nV(modk_, isr_, vec)\n"));

    [Fact]
    public void WithoutAPasteAnywhere_PiecesDontCount() =>
        Assert.Equal(new[] { "entry_modk" }, Missing(new[] { "entry_modk" }, "int entry_; int modk;\n"));

    [Fact]
    public void NameInASecondText_IsNotMissing() =>
        Assert.Empty(Missing(new[] { "modk_run" }, "int a;\n", "gcc -c -Dmodk_run=modk_run2 x.c\n"));

    [Fact]
    public void Carve_FailsBeforeTheGraph_WithANearMiss()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-rootname-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(work, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "main.c"), "void modk_init(void){}\nint main(void){ modk_init(); return 0; }\n");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\", \"modk_inti\", \"modk_made_up\"]\n");
            var se = new StringWriter();
            Assert.Equal(1, CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se));
            var e = se.ToString();
            Assert.Contains("2 of 3 entry point(s) can't resolve — failing before the graph is built", e);
            Assert.Contains("'modk_inti'", e);
            Assert.Contains("did you mean: modk_init", e);
            Assert.Contains("'modk_made_up'", e);
            Assert.DoesNotContain("build-graph", e);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
