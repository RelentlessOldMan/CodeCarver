using System.IO.Compression;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A failed verify writes every failure up twice with no flag: raw (local) and anonymized with a replayable bundle
/// that reproduces it.
/// </summary>
public sealed class FailureCasesTests
{
    static string Debug(TreeCarve t, string rel) => Path.Combine(t.Root, "out", "codecarver", "debug", rel);

    static TreeCarve TraceMissedAFile()
    {
        // The trace never opened legacy_decoy.c, yet emitted code calls into it unconditionally: verify fails.
        var t = new TreeCarve()
            .W("main.c", "int secret_fn(void);\nint main(void){ return secret_fn(); }\n")
            .W("core/secret.c", "/* Proprietary bring-up for the widget */\nint legacy_decoy(void);\nint secret_fn(void) { return legacy_decoy() + 0x4000; }\n")
            .W("legacy/legacy_decoy.c", "int legacy_decoy(void) { return 666; }\n");
        t.TracePaths = new() { t.S("main.c"), t.S("core/secret.c") };
        return t;
    }

    [Fact]
    public void FailedVerify_WritesRawAndAnonymizedCases_AndTheBundleReproduces()
    {
        using var t = TraceMissedAFile();
        var r = t.Carve(log: false);
        Assert.Equal(3, r.Code);

        var raw = File.ReadAllText(Debug(t, "raw/cases.txt"));
        Assert.Contains("legacy_decoy", raw);
        Assert.Contains("legacy/legacy_decoy.c", raw);
        Assert.Contains("NOT opened by the traced build", raw);
        Assert.Contains("inside Function secret_fn", raw);
        Assert.Contains("replay: yes", raw);

        var anon = File.ReadAllText(Debug(t, "anon/cases.txt"));
        Assert.Contains("replay: yes", anon);
        Assert.Contains("NOT opened by the traced build", anon);
        foreach (var word in new[] { "secret", "legacy", "decoy", "Proprietary", "widget", "core", "4000" })
            Assert.DoesNotContain(word, anon);

        // Everything in the zip: no original word, no path of this machine.
        var zip = Debug(t, "anon.zip");
        Assert.True(File.Exists(zip));
        using (var z = ZipFile.OpenRead(zip))
        {
            Assert.Contains(z.Entries, e => e.FullName.Replace('\\', '/') == "case1/carve.toml");
            Assert.DoesNotContain(z.Entries, e => e.FullName.Contains("/out/") || e.FullName.Contains("\\out\\"));
            foreach (var e in z.Entries)
            {
                using var s = new StreamReader(e.Open());
                var text = s.ReadToEnd() + " " + e.FullName;
                foreach (var word in new[] { "secret", "legacy", "decoy", "Proprietary", "widget", "core", "cc-tc-", "Temp", "Users" })
                    Assert.DoesNotContain(word, text);
            }
        }

        // The key maps back.
        var key = File.ReadAllText(Debug(t, "raw/key.txt"));
        Assert.Contains("legacy", key);

        Assert.Contains(".debug.cases = 1\n", t.Summary.Replace("\r", ""));
        Assert.Contains(".debug.reproduced = 1\n", t.Summary.Replace("\r", ""));
        Assert.Contains("debug   : 1 failure case(s) written up, 1 reproduce", r.Out);
    }

    [Fact]
    public void TheBundleCarvesOnItsOwn_FromTheZip()
    {
        using var t = TraceMissedAFile();
        Assert.Equal(3, t.Carve(log: false).Code);
        var unpacked = Path.Combine(t.Root, "unpacked");
        ZipFile.ExtractToDirectory(Debug(t, "anon.zip"), unpacked);
        var cfg = Path.Combine(unpacked, "case1", "carve.toml");
        Assert.Contains("carve fs/anon/root --config carve.toml", File.ReadLines(cfg).First());
        var srcRel = "fs/anon/root";
        var so = new StringWriter(); var se = new StringWriter();
        var code = CodeCarver.Cli.CarveCommand.Run(new[] { "carve", Path.Combine(unpacked, "case1", srcRel), "--config", cfg }, so, se);
        Assert.True(code == 3, so + "\n" + se);
    }

    [Fact]
    public void PassingVerify_WritesNoCases()
    {
        using var t = new TreeCarve()
            .W("main.c", "int f(void);\nint main(void){ return f(); }\n")
            .W("f.c", "int f(void) { return 0; }\n");
        Assert.Equal(0, t.Carve(log: false).Code);
        Assert.False(Directory.Exists(Debug(t, "")));
    }
}
