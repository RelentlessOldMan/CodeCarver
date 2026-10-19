using System.Diagnostics;
using System.Text.Json;
using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Where the tree lives must not change what is kept. A work run read over a network share kept one file fewer than
/// the same tree read from a copy on the local disk: a version .txt that a source file #includes, found by the scan
/// that follows quoted #includes of non-code files. Each way that scan could miss a file only on one spelling of the
/// root (a drive root, a share path) or only on a flaky drive (a read or size lookup failing) is covered here: the
/// same tree gives the same lists, and a file the scan can't read is a warning and a count, never a quiet difference.
/// All names are invented.
/// </summary>
public class RootSpellingTests
{
    static (string Work, string Src) NewTree()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-rootsp-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        void W(string rel, string text)
        {
            var p = Path.Combine(src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        // app/main.c includes the version text beside it; another directory has a file of the same name that nothing
        // includes, so a run that falls back to matching by name keeps the wrong one too.
        W("app/main.c", "static const char ver[] =\n#include \"fw_version.txt\"\n;\nint main(void) { return ver[0]; }\n");
        W("app/fw_version.txt", "\"1.2.3\"\n");
        W("tools/fw_version.txt", "\"0.0.0\"\n");
        W("tools/stamp.c", "int stamp(void) { return 0; }\n");
        return (work, src);
    }

    static (int Code, string Err, Dictionary<string, string[]> Lists, string Summary) Carve(string work, string root, string name, string advanced = "")
    {
        var outDir = Path.Combine(work, "out-" + name);
        var cfg = Path.Combine(work, name + ".toml");
        File.WriteAllText(cfg, $"outputDirectory = \"{outDir.Replace('\\', '/')}\"\n[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n{advanced}");
        var so = new StringWriter(); var se = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", root, "--config", cfg }, so, se);
        var lists = new Dictionary<string, string[]>();
        var manifest = Path.Combine(outDir, "codecarver", "manifest.json");
        if (File.Exists(manifest))
        {
            using var m = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var p in new[] { "keptFiles", "includeClosureFiles", "infrastructureFiles", "droppedFiles" })
                lists[p] = m.RootElement.GetProperty(p).EnumerateArray().Select(e => e.GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }
        var summary = Path.Combine(outDir, "codecarver", "summary.txt");
        return (code, se.ToString(), lists, File.Exists(summary) ? File.ReadAllText(summary) : "");
    }

    static void AssertSameLists(Dictionary<string, string[]> expected, Dictionary<string, string[]> actual, string how, string err)
    {
        foreach (var (list, want) in expected)
            Assert.True(actual.TryGetValue(list, out var got) && want.SequenceEqual(got),
                $"{how}: {list} = [{string.Join(", ", actual.GetValueOrDefault(list) ?? Array.Empty<string>())}], "
                + $"read directly = [{string.Join(", ", want)}]\n{err}");
    }

    static bool RunQuiet(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(30_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    [Fact]
    public void SameTree_SameLists_ReadDirectly_FromADriveRoot_OrOverAShare()
    {
        var (work, src) = NewTree();
        string? drive = null;
        try
        {
            var direct = Carve(work, src, "direct");
            Assert.Equal(0, direct.Code);
            Assert.Contains("app/fw_version.txt", direct.Lists["keptFiles"]);
            Assert.DoesNotContain("tools/fw_version.txt", direct.Lists["keptFiles"]);
            Assert.Contains("include.readFailures = 0", direct.Summary);

            var others = 0;
            if (OperatingSystem.IsWindows())
            {
                // The tree as the root of a drive (subst), as a mapped share often is: "X:\" ends in its separator.
                var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
                var letter = Enumerable.Range('M', 'Z' - 'M' + 1).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
                if (letter != default && RunQuiet("subst", $"{letter}: \"{src}\""))
                {
                    drive = $"{letter}:";
                    var viaDrive = Carve(work, drive + "\\", "drive");
                    Assert.Equal(0, viaDrive.Code);
                    AssertSameLists(direct.Lists, viaDrive.Lists, "from a drive root", viaDrive.Err);
                    others++;
                }
                // The tree over SMB (the machine's own administrative share), when this machine has one.
                var full = Path.GetFullPath(src);
                var unc = $@"\\localhost\{full[0]}$\{full[3..]}";
                bool reachable;
                try { reachable = Directory.Exists(unc); } catch { reachable = false; }
                if (reachable)
                {
                    var viaShare = Carve(work, unc, "share");
                    Assert.Equal(0, viaShare.Code);
                    AssertSameLists(direct.Lists, viaShare.Lists, "over a share", viaShare.Err);
                    others++;
                }
            }
            // Neither other spelling could be made here (no subst, no admin share): the direct run still stands.
            Console.WriteLine($"root spellings compared: {others}");
        }
        finally
        {
            if (drive is not null) RunQuiet("subst", $"{drive} /d");
            try { Directory.Delete(work, true); } catch { }
        }
    }

    [Fact]
    public void IncludeScan_FileItCannotRead_IsAWarningAndACount_NotAQuietDifference()
    {
        if (!OperatingSystem.IsWindows()) return;   // the lock below is Windows sharing; elsewhere a lock doesn't stop a read
        var (work, src) = NewTree();
        try
        {
            // A second source file, unreadable for the whole run (a share that keeps failing on it), includes a table.
            File.WriteAllText(Path.Combine(src, "app", "side.c"), "#include \"side_table.txt\"\nint side(void) { return 0; }\n");
            File.WriteAllText(Path.Combine(src, "app", "side_table.txt"), "1, 2, 3\n");
            using (new FileStream(Path.Combine(src, "app", "side.c"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var r = Carve(work, src, "locked");
                Assert.Contains("include.readFailures = 1", r.Summary);
                Assert.Contains("app/side.c: could not be read", r.Err);
                Assert.Contains("warnings.includeRead = 1", r.Summary);
            }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
