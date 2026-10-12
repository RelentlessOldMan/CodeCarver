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

    static string Blob(string dir, string name, string words)
    {
        // An archive-like file: zero bytes around the names, as in a symbol table.
        var bytes = new List<byte> { 0x7f, (byte)'E', (byte)'L', (byte)'F', 0, 0 };
        foreach (var w in words.Split(' ')) { bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(w)); bytes.Add(0); }
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes.ToArray());
        return p;
    }

    [Fact]
    public void NameInABinary_IsFound_AsAWholeWordOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-rootbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var c = new RootNameCheck(new[] { "modt_open", "modt_close", "entry_modk" });
            c.Add("#define E(f) entry_##f\n");
            var blob = Blob(dir, "libmodt.a", "modt_open modt_close2 entry_ modk");
            Assert.True(CodeCarver.Core.Util.BinaryText.IsBinary(blob));
            Assert.True(c.AddBinary(blob));
            // A longer word doesn't hold the name, and a binary's words are never pieces of a paste.
            Assert.Equal(new[] { "modt_close", "entry_modk" }, c.Missing());
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "modt_close\n");
            Assert.False(CodeCarver.Core.Util.BinaryText.IsBinary(Path.Combine(dir, "notes.txt")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void NameInABinary_AcrossAChunkEnd_IsReadWhole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-rootbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The first chunk is 4 MB: one name straddles its end, and a long word ends past it in another name.
            var bytes = new byte[(1 << 23) + 64];
            void Put(int at, string w) => System.Text.Encoding.ASCII.GetBytes(w).CopyTo(bytes, at);
            Put((1 << 22) - 4, "modt_open");
            Put((1 << 23) - 10, "xxxxxxxxxxxxxxxxxxxxmodt_close");
            var p = Path.Combine(dir, "big.bin");
            File.WriteAllBytes(p, bytes);
            var c = new RootNameCheck(new[] { "modt_open", "modt_close" });
            Assert.True(c.AddBinary(p));
            Assert.Equal(new[] { "modt_close" }, c.Missing());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Carve_NamesOnlyInAPrebuiltBinary_AreNotedNotFailed()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-rootname-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(work, "src");
            Directory.CreateDirectory(Path.Combine(src, "modt"));
            File.WriteAllText(Path.Combine(src, "main.c"), "void modk_init(void){}\nint main(void){ modk_init(); return 0; }\n");
            Blob(Path.Combine(src, "modt"), "libmodt.a", "modt_open modt_close");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\", \"modt_open\", \"modt_close\", \"modk_made_up\"]\n");
            var se = new StringWriter();
            Assert.Equal(1, CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se));
            var e = se.ToString();
            Assert.Contains("2 entry point(s) are defined only in 1 prebuilt binary file(s)", e);
            Assert.Contains("1 of 4 entry point(s) can't resolve — failing before the graph is built", e);
            Assert.DoesNotContain("'modt_open'", e);

            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\", \"modt_open\", \"modt_close\"]\n");
            se = new StringWriter();
            Assert.Equal(0, CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se));
            Assert.Contains("2 entry point(s) are defined only in 1 prebuilt binary file(s)", se.ToString());
            Assert.True(Directory.EnumerateFiles(Path.Combine(work, "out"), "libmodt.a", SearchOption.AllDirectories).Any());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Carve_KeepsWhatAPrebuiltBinaryCalls()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-rootname-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(work, "src");
            Directory.CreateDirectory(Path.Combine(src, "modt"));
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){ return 0; }\n");
            File.WriteAllText(Path.Combine(src, "callback.c"), "int modk_on_event(int e){ return e + 1; }\n");
            File.WriteAllText(Path.Combine(src, "dead.c"), "int modk_never(int e){ return e - 1; }\n");
            // The archive calls modk_on_event; a manual that names modk_never is no call.
            Blob(Path.Combine(src, "modt"), "libmodt.a", "modt_open modk_on_event");
            Blob(Path.Combine(src, "modt"), "manual.pdf", "modk_never");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\"]\n");
            var se = new StringWriter();
            Assert.Equal(0, CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se));
            Assert.Contains("1 function/global name(s) of the tree appear in 1 prebuilt binary file(s)", se.ToString());
            var outFiles = Directory.EnumerateFiles(Path.Combine(work, "out"), "*.c", SearchOption.AllDirectories)
                                    .Where(f => File.ReadAllText(f).Contains("return e")).Select(Path.GetFileName).ToList();
            Assert.Contains("callback.c", outFiles);
            Assert.DoesNotContain("dead.c", outFiles);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

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
