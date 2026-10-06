using System.IO.Compression;
using System.Text;
using CodeCarver.Core.Diagnostics;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

/// <summary>
/// Tests the shareable diagnostic package (spec §39): generation, naming, human + structured artifacts,
/// robustness against a bad destination, failure capture, and — most important for a tool whose inputs are
/// proprietary — that the package leaks NO source content and redacts home paths (spec §18).
/// </summary>
public sealed class DiagnosticReportTests
{
    private static (string Summary, string Json, string Manifest) ReadPackage(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string Read(string name)
        {
            using var s = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
            return s.ReadToEnd();
        }
        return (Read("summary.txt"), Read("diagnostics.json"), Read("manifest.txt"));
    }

    [Fact]
    public void WritePackage_CreatesZip_WithThreeArtifacts()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Set("lang", "c");
            report.Set("keptFiles", 3);
            report.Event("phase build-graph: 12 ms");
            report.Warn("kept whole: weird.c");

            var ok = report.TryWritePackage(Path.Combine(work, "pkg.zip"), out var zipPath, out var err);

            Assert.True(ok, err);
            Assert.True(File.Exists(zipPath));
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.NotNull(zip.GetEntry("summary.txt"));
            Assert.NotNull(zip.GetEntry("diagnostics.json"));
            Assert.NotNull(zip.GetEntry("manifest.txt"));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Summary_And_Json_CarrySessionAndFields()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Set("keptFiles", 42);
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);

            var (summary, json, manifest) = ReadPackage(zipPath);
            Assert.Contains(report.SessionId, summary);
            Assert.Contains(report.SessionId, json);
            Assert.Contains("keptFiles", summary);
            Assert.Contains("42", json);
            Assert.Contains($"DiagnosticFormatVersion: {DiagnosticReport.FormatVersion}", summary);
            Assert.Contains("Excluded", manifest);          // self-describing: says what it does NOT carry
            Assert.Contains("Source file contents", manifest);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Package_RedactsHomePath_NoUsernameLeak()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var report = DiagnosticReport.Start();
            report.Set("sourceRoot", Path.Combine(home, "secret-project", "src")); // contains username
            report.Warn($"could not read {Path.Combine(home, "private", "a.c")}");

            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);
            var (summary, json, _) = ReadPackage(zipPath);

            // Neither the home path (hence the username) nor anything under it may appear (review D1: a path under
            // home used to become %USERPROFILE%/secret-project/src, which still names the project).
            Assert.DoesNotContain(home, summary);
            Assert.DoesNotContain(home, json);
            Assert.DoesNotContain("secret-project", summary + json);
            Assert.DoesNotContain("private", summary + json);
            Assert.Contains("<path>", summary);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void WriteToDirectory_GeneratesTimestampedZipInside()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var ok = DiagnosticReport.Start().TryWritePackage(work, out var zipPath, out var err);
            Assert.True(ok, err);
            Assert.StartsWith(work, zipPath);
            Assert.Contains("CodeCarver_Diagnostics_", Path.GetFileName(zipPath));
            Assert.EndsWith(".zip", zipPath);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void AppendsZipExtension_WhenMissing()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            DiagnosticReport.Start().TryWritePackage(Path.Combine(work, "report"), out var zipPath, out _);
            Assert.EndsWith(".zip", zipPath);
            Assert.True(File.Exists(zipPath));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void BadDestination_ReturnsFalse_DoesNotThrow()
    {
        // A destination the process can't create is a write failure, not a crash (spec §24). Its parent is an
        // existing FILE, so the directory can never be made — on any OS, and without touching a real drive (the
        // old `Z:\` could be a mapped network share; review TS8).
        var blocker = Path.Combine(Path.GetTempPath(), "cc-diag-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "");
        try
        {
            var bad = Path.Combine(blocker, "deep", "pkg.zip");
            var ex = Record.Exception(() =>
            {
                var ok = DiagnosticReport.Start().TryWritePackage(bad, out _, out var err);
                Assert.False(ok);
                Assert.NotNull(err);
            });
            Assert.Null(ex); // never throws
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void SetFailure_CapturesExceptionInPackage()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            try { throw new InvalidOperationException("boom-marker"); }
            catch (Exception ex) { report.SetFailure(ex); }

            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);
            var (summary, json, _) = ReadPackage(zipPath);
            Assert.Contains("boom-marker", summary);
            Assert.Contains("InvalidOperationException", json);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void SetFailure_CapturesInnerException()
    {
        // The unhandled-exception path often wraps the real cause (e.g. a TargetInvocationException around an
        // IOException). The diagnostic must record the INNER exception too, or the useful cause is lost.
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            var inner = new FileNotFoundException("inner-boom-marker");
            report.SetFailure(new InvalidOperationException("outer", inner));

            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);
            var (_, json, _) = ReadPackage(zipPath);
            Assert.Contains("innerException", json);
            Assert.Contains("inner-boom-marker", json);
            Assert.Contains("FileNotFoundException", json);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Package_RendersNullAndListFields()
    {
        // Structured fields are rendered into the human summary: a null value shows as "(none)" (not blank or
        // a crash) and a collection (e.g. the root set) is joined, so the summary is readable for support.
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Set("buildLog", null);
            report.Set("roots", new[] { "main", "USART1_IRQHandler" });
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);

            var (summary, _, _) = ReadPackage(zipPath);
            Assert.Contains("(none)", summary);                    // null field rendered safely
            Assert.Contains("main, USART1_IRQHandler", summary);   // list field joined
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Redact_ReplacesHome_LeavesOtherTextAlone()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("hello world", DiagnosticReport.Redact("hello world"));
        var red = DiagnosticReport.Redact(Path.Combine(home, "x"));
        Assert.DoesNotContain(home, red);
    }

    [Fact]
    public void Redact_StripsAbsolutePaths_OutsideHome()
    {
        // The core leak fix: a proprietary source tree does NOT live under the home dir (the corpora are at
        // C:\Playground\..., \\IRISH\TestHole\..., WSL /mnt/...). Home-only redaction would have shipped these
        // verbatim from free text (e.g. exception messages). Now any absolute path token is scrubbed.
        var drive = DiagnosticReport.Redact(@"could not find C:\Playground\firmware\src\reg.h here");
        Assert.DoesNotContain(@"C:\Playground", drive);
        Assert.DoesNotContain("firmware", drive);
        Assert.Contains("<path>", drive);

        var unc = DiagnosticReport.Redact(@"reading \\IRISH\TestHole\death\secret.c failed");
        Assert.DoesNotContain("IRISH", unc);
        Assert.DoesNotContain("secret", unc);

        var wsl = DiagnosticReport.Redact("at /mnt/c/work/proprietary/main.c");
        Assert.DoesNotContain("proprietary", wsl);

        // A non-path ratio like "24/24" must NOT be mistaken for a path.
        Assert.Equal("24/24 passed", DiagnosticReport.Redact("24/24 passed"));
    }

    [Fact]
    public void Attachment_AppearsInZip_AndManifestListsIt_NoNamesWarningWhenSafe()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Attach("repro.graph.json", "{\"anon\":true}", containsNames: false, description: "anonymized graph");
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);

            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("repro.graph.json");
            Assert.NotNull(entry);
            using (var r = new StreamReader(entry!.Open())) Assert.Contains("\"anon\":true", r.ReadToEnd());

            var (_, _, manifest) = ReadPackage(zipPath);
            Assert.Contains("repro.graph.json", manifest);
            Assert.Contains("anonymized graph", manifest);
            Assert.DoesNotContain("INCLUDES NAMES", manifest); // safe attachment -> no names warning
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Attachment_WithNames_ManifestWarnsProminently()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Attach("keepdrop.txt", "kept: driver.c", containsNames: true, description: "keep/drop table");
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);

            var (_, _, manifest) = ReadPackage(zipPath);
            Assert.Contains("INCLUDES NAMES", manifest);   // the recipient is warned before sharing
            Assert.Contains("keepdrop.txt", manifest);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Attachment_ContentIsRedacted_LikeEverythingElse()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var report = DiagnosticReport.Start();
            report.Attach("keepdrop.txt", $"kept: {Path.Combine(home, "proj", "a.c")}", containsNames: true);
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);

            using var zip = ZipFile.OpenRead(zipPath);
            using var r = new StreamReader(zip.GetEntry("keepdrop.txt")!.Open());
            var content = r.ReadToEnd();
            Assert.DoesNotContain(home, content);   // home path (username) redacted inside the attachment too
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Attachment_SameName_Overwrites_NoDuplicateEntry()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Attach("a.txt", "first");
            report.Attach("a.txt", "second");   // replaces the first
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);

            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Equal(1, zip.Entries.Count(e => e.Name == "a.txt"));
            using var r = new StreamReader(zip.GetEntry("a.txt")!.Open());
            Assert.Equal("second", r.ReadToEnd());
        }
        finally { Cleanup(work); }
    }

    private static void Cleanup(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
    }

    [Fact]
    public void Redact_PathWithSpacesAndRelativePaths_FullyRemoved()
    {
        var r = DiagnosticReport.Redact(@"failed on src/secret_dir/secret_blob.h and boards\acme\x.c in 'secret_entry'");
        Assert.DoesNotContain("secret_dir", r);
        Assert.DoesNotContain("secret_blob", r);
        Assert.DoesNotContain("acme", r);
        Assert.DoesNotContain("secret_entry", r);
        Assert.DoesNotContain("acme", DiagnosticReport.Redact("open /work/acme/fw/main.c failed"));
        Assert.DoesNotContain("SecretProj", DiagnosticReport.Redact(@"read C:\Users\bob\work\SecretProj\x.c"));
        Assert.Equal("24/24 passed", DiagnosticReport.Redact("24/24 passed"));
    }

    [Fact]
    public void Warnings_AreRecordedAsCategoryCountsOnly()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var report = DiagnosticReport.Start();
            report.Warn("boards/acme_secret/flash.cmm: `DO acme_init.cmm` — ambiguous basename 'acme_init' matches 2 files; bound to the first");
            report.Warn("acme_secret/regs.c: parse exceeded the 20000 ms budget — kept whole, not carved");
            report.AddSensitive(new[] { "acme_entry_point" });
            report.Event("about acme_entry_point");
            report.TryWritePackage(Path.Combine(work, "p.zip"), out var zipPath, out _);
            var (summary, json, _) = ReadPackage(zipPath);
            Assert.DoesNotContain("acme", summary + json);
            Assert.Contains("cmm.ambiguous-do: 1", summary);
            Assert.Contains("frontend.parse-timeout: 1", summary);
        }
        finally { Cleanup(work); }
    }
}
