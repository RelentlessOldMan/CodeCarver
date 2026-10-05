using CodeCarver.Cli;
using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Review O1/D2/RB14: a carve never replaces a directory it did not create, stage names can't escape
/// outputDirectory, and orphan reaping can't touch a sibling output with a prefix name.</summary>
public sealed class OutputSafetyTests
{
    static (int Code, string Err) Carve(string work, string body)
    {
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
        var cfg = Path.Combine(work, "carve.toml");
        File.WriteAllText(cfg, body);
        var se = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se);
        return (code, se.ToString());
    }

    static string NewWork() => Path.Combine(Path.GetTempPath(), "cc-osafe-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingForeignCarvedDir_IsNotReplaced()
    {
        var work = NewWork();
        try
        {
            var important = Path.Combine(work, "out2", "carved", "IMPORTANT.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(important)!);
            File.WriteAllText(important, "keep me");
            var (code, err) = Carve(work, $"outputDirectory = \"{Path.Combine(work, "out2").Replace('\\', '/')}\"\n[common]\nentryPoints=[\"main\"]\n");
            Assert.Equal(2, code);
            Assert.Contains("refusing to replace", err);
            Assert.Equal("keep me", File.ReadAllText(important));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void ReRunIntoOwnOutput_Replaces()
    {
        var work = NewWork();
        try
        {
            var body = $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n[common]\nentryPoints=[\"main\"]\n";
            Assert.Equal(0, Carve(work, body).Code);
            File.WriteAllText(Path.Combine(work, "out", "carved", "stale.txt"), "old");
            Assert.Equal(0, Carve(work, body).Code);
            Assert.False(File.Exists(Path.Combine(work, "out", "carved", "stale.txt")));
            Assert.True(File.Exists(Path.Combine(work, "out", "carved", "main.c")));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void StageNameWithPathSegments_IsAConfigError()
    {
        var work = NewWork();
        try
        {
            var (code, err) = Carve(work, $"outputDirectory = \"{Path.Combine(work, "out").Replace('\\', '/')}\"\n"
                + "[common]\nentryPoints=[\"main\"]\n[stages.\"../../x\"]\ncarveSourceFileContents = false\n");
            Assert.Equal(2, code);
            Assert.Contains("section names may use only", err);
            Assert.False(Directory.Exists(Path.Combine(work, "x")));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Carve_UnexpectedFailure_WritesDiagZip_WithNoSourceNames()
    {
        // Review D1: the crash package is described as safe to send. Force a crash (outputDirectory beneath a
        // regular file) on a tree full of distinctive names; no name may appear in any zip entry.
        var work = NewWork();
        string? zip = null;
        try
        {
            var src = Path.Combine(work, "src");
            Directory.CreateDirectory(Path.Combine(src, "secret_dir"));
            File.WriteAllText(Path.Combine(src, "secret_dir", "secret_blob.h"),
                string.Concat(Enumerable.Range(0, 60).Select(i => $"0x{i:X2}, 0x{i:X2},\n")));
            File.WriteAllText(Path.Combine(src, "secret_main.c"),
                "#include \"secret_dir/secret_blob.h\"\nint secret_entry(void){return 0;}\n");
            var blocker = Path.Combine(work, "blocker.txt");
            File.WriteAllText(blocker, "x");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(blocker, "out").Replace('\\', '/')}\"\n"
                + "[common]\nentryPoints=[\"secret_entry\"]\n");
            var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se);
            Assert.Equal(1, code);
            var m = System.Text.RegularExpressions.Regex.Match(se.ToString(), @"diagnostic package written -> (.+\.zip)");
            Assert.True(m.Success, se.ToString());
            zip = m.Groups[1].Value.Trim();
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                using var r = new StreamReader(entry.Open());
                var text = r.ReadToEnd();
                foreach (var name in new[] { "secret_dir", "secret_blob", "secret_main", "secret_entry", "blocker", work })
                    Assert.False(text.Contains(name, StringComparison.OrdinalIgnoreCase), $"{entry.Name} leaks '{name}':\n{text}");
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
            try { if (zip is not null) File.Delete(zip); } catch { }
        }
    }

    [Fact]
    public void Summary_HoldsNoPathFileOrSymbolName()
    {
        // The one-way workflow: only this file goes back. It must never carry an input name.
        var work = NewWork();
        try
        {
            var src = Path.Combine(work, "src", "secret_dir");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "secret_main.c"), "void secret_helper(void);\nint secret_entry(void){ secret_helper(); return 0; }\n");
            File.WriteAllText(Path.Combine(src, "secret_helper.c"), "void secret_helper(void){}\n");
            File.WriteAllText(Path.Combine(src, "secret_dead.c"), "void secret_dead(void){}\n");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"secret_out\"\n[common]\nentryPoints = [\"secret_entry\"]\n"
                + "[stages.secret_stage]\ncarveSourceFileContents = true\n");
            var code = CarveCommand.Run(new[] { "carve", Path.Combine(work, "src"), "--config", cfg }, new StringWriter(), new StringWriter());
            Assert.Equal(0, code);
            var ccDir = Path.Combine(work, "secret_out", "secret_stage", "codecarver");
            foreach (var f in new[] { "summary.txt", "summary.json" })
            {
                var text = File.ReadAllText(Path.Combine(ccDir, f));
                Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(work, text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("stage0.keptFiles", text);
            }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void ConfigRelativePaths_ResolveAgainstTheConfigFile_U1()
    {
        var work = NewWork();
        try
        {
            var src = Path.Combine(work, "proj", "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
            File.WriteAllText(Path.Combine(work, "proj", "run.trace"), Path.Combine(src, "main.c").Replace('\\', '/') + "\n");
            var cfg = Path.Combine(work, "proj", "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\"]\n[runs.r]\nrunTraceFiles = [\"run.trace\"]\n");
            // The process's current directory is not proj/, so cwd-relative resolution would miss run.trace.
            var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se);
            Assert.True(code == 0, se.ToString());
            Assert.True(File.Exists(Path.Combine(work, "proj", "out", "carved", "main.c")));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void StageFlagWithoutStages_IsAnError_U7()
    {
        var work = NewWork();
        try
        {
            var src = Path.Combine(work, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\"]\n");
            var se = new StringWriter();
            Assert.Equal(2, CarveCommand.Run(new[] { "carve", src, "--config", cfg, "--stage", "max" }, new StringWriter(), se));
            Assert.Contains("no [stages.X]", se.ToString());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void UnresolvedEntryPoint_SuggestsNearMiss_AndWhyStillAnswers_U8()
    {
        var work = NewWork();
        try
        {
            var src = Path.Combine(work, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "main.c"), "void uart_init(void){}\nint main(void){ uart_init(); return 0; }\n");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, "outputDirectory = \"out\"\n[common]\nentryPoints = [\"main\", \"uart_inti\"]\n");
            var se = new StringWriter();
            Assert.Equal(1, CarveCommand.Run(new[] { "carve", src, "--config", cfg }, new StringWriter(), se));
            Assert.Contains("did you mean: uart_init", se.ToString());
            var so = new StringWriter();
            Assert.Equal(0, CarveCommand.Run(new[] { "carve", src, "--config", cfg, "--why", "uart_init" }, so, new StringWriter()));
            Assert.Contains("uart_init", so.ToString());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Begin_DoesNotReapStagingOfSiblingOutputWithPrefixName()
    {
        var work = NewWork();
        try
        {
            Directory.CreateDirectory(work);
            var sibling = Path.Combine(work, ".ccstaging-out-2-abcdef12");
            Directory.CreateDirectory(sibling);
            using (var st = StagedOutput.Begin(Path.Combine(work, "out"))) { }
            Assert.True(Directory.Exists(sibling));
            var own = Path.Combine(work, ".ccstaging-out-0123abcd");
            Directory.CreateDirectory(own);
            using (var st = StagedOutput.Begin(Path.Combine(work, "out"))) { }
            Assert.False(Directory.Exists(own));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
