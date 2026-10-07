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
        Assert.False(Directory.Exists(Path.Combine(unpacked, "case1", "fs")));   // packed: rebuilt from store/
        CodeCarver.Cli.FailureCases.Unpack(unpacked);
        var cfg = Path.Combine(unpacked, "case1", "carve.toml");
        Assert.Contains("carve fs/anon/root --config carve.toml", File.ReadLines(cfg).First());
        var srcRel = "fs/anon/root";
        var so = new StringWriter(); var se = new StringWriter();
        var code = CodeCarver.Cli.CarveCommand.Run(new[] { "carve", Path.Combine(unpacked, "case1", srcRel), "--config", cfg }, so, se);
        Assert.True(code == 3, so + "\n" + se);
    }

    [Fact]
    public void CasesSharingAHeader_StoreItOnce()
    {
        // Two different failures (two names, two files) whose files include the same big header.
        var big = string.Concat(Enumerable.Range(0, 2000).Select(i => $"#define BIG_REG_{i} ({i}u)\n"));
        using var t = new TreeCarve()
            .W("big_regs.h", big)
            .W("main.c", "int first_fn(void); int second_fn(void);\nint main(void){ return first_fn() + second_fn(); }\n")
            .W("one.c", "#include \"big_regs.h\"\nint gone_one(void);\nint first_fn(void) { return gone_one(); }\n")
            .W("two.c", "#include \"big_regs.h\"\nint gone_two(void);\nint second_fn(void) { return gone_two(); }\n")
            .W("old/gone_one.c", "int gone_one(void) { return 1; }\n")
            .W("old/gone_two.c", "int gone_two(void) { return 2; }\n");
        t.TracePaths = new() { t.S("main.c"), t.S("one.c"), t.S("two.c"), t.S("big_regs.h") };
        Assert.Equal(3, t.Carve(log: false).Code);
        var anonDir = Debug(t, "anon");
        Assert.Contains("case 2:", File.ReadAllText(Path.Combine(anonDir, "cases.txt")));
        var lists = Directory.EnumerateFiles(anonDir, "files.tsv", SearchOption.AllDirectories).ToList();
        Assert.Equal(2, lists.Count);
        // The header's stored copy is named in both cases' lists, and is in store/ once.
        var blobs = lists.Select(l => File.ReadLines(l).Select(x => x.Split('\t')[1]).ToHashSet()).ToList();
        var shared = blobs[0].Intersect(blobs[1]).ToList();
        Assert.Contains(shared, id => new FileInfo(Path.Combine(anonDir, "store", id)).Length > 20_000);
        Assert.Equal(Directory.EnumerateFiles(Path.Combine(anonDir, "store")).Count(), blobs[0].Union(blobs[1]).Count());
        Assert.Contains("2 reproduce", File.ReadAllText(Path.Combine(t.Root, "out", "codecarver", "summary.txt")).Replace(".debug.reproduced = 2", "2 reproduce"));
    }

    [Fact]
    public void Summary_SaysWhyTheKeptCodeIsKept()
    {
        // cb.c is reached only through a function pointer: kept for soundness, the indirect-only bucket.
        using var t = new TreeCarve()
            .W("main.c", "#include \"api.h\"\nint (*hook)(void) = cb;\nint main(void){ return direct() + hook(); }\n")
            .W("api.h", "int direct(void);\nint cb(void);\n")
            .W("direct.c", "int direct(void) { return 1; }\n")
            .W("cb.c", "int cb(void) { return 2; }\n");
        Assert.Equal(0, t.Carve(log: false).Code);
        var s = t.Summary.Replace("\r", "");
        Assert.Contains("keep.files.indirectOnly = 1\n", s);
        Assert.Contains("keep.files.root = 1\n", s);
        Assert.Contains("keep.files.header = 1\n", s);
        Assert.Contains("keep.bytes.total = ", s);
        Assert.Matches(@"time\.reachability\.ms = \d+\n", s);
    }

    [Fact]
    public void BuildOutputDropIn_WritesUpTheErrors_RawAndAnonymized()
    {
        // The carve keeps main.c only; building it (say, with code the carve can't see) failed on two names.
        using var t = new TreeCarve()
            .W("main.c", "int main(void){ return 0; }\n")
            .W("drivers/secret_extra.c", "int secret_extra_fn(void) { return 1; }\n")
            .W("cfg/secret_opt.h", "#define SECRET_OPT 1\n");
        Assert.Equal(0, t.Carve(log: false).Code);
        var carved = Path.Combine(t.Root, "out", "carved");
        File.WriteAllText(Path.Combine(t.Root, "out", CodeCarver.Cli.CarveCommand.BuildOutputFile),
            "gcc -c main.c\n"
            + $"{Path.Combine(carved, "main.c")}:1:10: fatal error: cfg/secret_opt.h: No such file or directory\n"
            + "/usr/bin/ld: main.o: in function `main':\nmain.c:(.text+0x9): undefined reference to `secret_extra_fn'\n"
            + "collect2: error: ld returned 1 exit status\n");
        var r = t.Carve(log: false);
        Assert.Equal(0, r.Code);
        Assert.Contains("build   : build-output.txt: 2 error(s) written up, 2 reproduce", r.Out);

        var dir = Path.Combine(t.Root, "out", "codecarver", "debug-build");
        var raw = File.ReadAllText(Path.Combine(dir, "raw", "cases.txt"));
        Assert.Contains("cause build.MissingHeader", raw);
        Assert.Contains("cfg/secret_opt.h", raw);
        Assert.Contains("cause build.UndefinedReference", raw);
        Assert.Contains("drivers/secret_extra.c", raw);
        Assert.Contains("dropped", raw);

        var anon = File.ReadAllText(Path.Combine(dir, "anon", "cases.txt"));
        Assert.Contains("replay: yes", anon);
        Assert.Contains("fatal error: ", anon);                     // the compiler's own words stay
        Assert.Contains(": No such file or directory", anon);
        Assert.Contains("undefined reference to `", anon);
        foreach (var word in new[] { "secret", "extra", "drivers", "SECRET", "OPT", "cc-tc-" })
            Assert.DoesNotContain(word, anon);
        Assert.True(File.Exists(Path.Combine(dir, "anon.zip")));
        Assert.Contains(".buildErrors.cases = 2\n", t.Summary.Replace("\r", ""));
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
