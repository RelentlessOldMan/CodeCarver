using System.Diagnostics;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// End-to-end tests of the actual product surface: the CLI's argument handling, exit codes, and safety
/// guards. Every eval bug so far lived in this glue (arg parsing, --out safety, size accounting), which
/// the library-level tests can't see — so these invoke the built CodeCarver.Cli.dll as a process and
/// assert on exit code + key stderr, exactly as a user/script experiences it.
///
/// Exit-code contract (Program.cs): 0 = ok, 1 = nothing to carve / roots unresolved / strict-roots fail,
/// 2 = usage/config error, 3 = --verify found an unsound edge.
///
/// Skips (does not fail) when the CLI dll hasn't been built — the test project references only Core +
/// Frontend, so a bare `dotnet test` need not have built the CLI. CI builds the solution first.
/// </summary>
public sealed class CliIntegrationTests
{
    private static string? FindCliDll()
    {
        var config = AppContext.BaseDirectory.Replace('\\', '/').Contains("/Release/") ? "Release" : "Debug";
        var rel = Path.Combine("src", "CodeCarver.Cli", "bin", config, "net8.0", "CodeCarver.Cli.dll");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, rel);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    // Returns null when the CLI dll isn't built — callers early-return (silent skip), the same convention
    // BuildVerifyTests uses for a missing toolchain, since xunit 2.5.3 has no Assert.Skip.
    private static (int Code, string Out)? RunCli(params string[] args)
    {
        var dll = FindCliDll();
        if (dll is null) return null;
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, so + se);
    }

    /// <summary>A minimal C tree with a real entry (run -> keep) and a dead function. Returns the src dir.</summary>
    private static string MakeTree(out string work)
    {
        work = Path.Combine(Path.GetTempPath(), "cc-cli-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"),
            "int keep(void){return 1;}\nint dead(void){return 2;}\nint run(void){return keep();}\n");
        return src;
    }

    [Fact]
    public void UnknownCommand_ExitsUsage()
    {
        var r = RunCli("wibble");
        if (r is null) return;
        var (code, outp) = r.Value;
        Assert.Equal(2, code);
        Assert.Contains("unknown command", outp);
    }

    [Fact]
    public void Version_PrintsAndSucceeds()
    {
        var r = RunCli("version");
        if (r is null) return;
        var (code, outp) = r.Value;
        Assert.Equal(0, code);
        Assert.Contains("CodeCarver", outp);
    }

    [Fact]
    public void Carve_MissingRoots_ExitsUsage()
    {
        var src = MakeTree(out var work);
        try
        {
            var r = RunCli("carve", src);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("--roots", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_UnknownFlag_ExitsUsage()
    {
        var src = MakeTree(out var work);
        try
        {
            var r = RunCli("carve", src, "--roots", "run", "--prun"); // typo'd flag
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("unknown or incomplete option", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_RootsNoneFound_ExitsError()
    {
        var src = MakeTree(out var work);
        try
        {
            var r = RunCli("carve", src, "--roots", "nope_not_a_symbol");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(1, code);
            Assert.Contains("none of the requested roots", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_StrictRoots_OneUnresolved_Fails()
    {
        var src = MakeTree(out var work);
        try
        {
            // 'run' resolves, 'ghost' does not -> partial resolution + --strict-roots must fail (exit 1).
            var r = RunCli("carve", src, "--roots", "run,ghost", "--strict-roots");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(1, code);
            Assert.Contains("strict-roots", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_MaxParseBytesInvalid_ExitsUsage()
    {
        var src = MakeTree(out var work);
        try
        {
            var r = RunCli("carve", src, "--roots", "run", "--max-parse-bytes", "notanumber");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("--max-parse-bytes", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_MalformedConfig_ExitsUsage()
    {
        var src = MakeTree(out var work);
        try
        {
            var cfg = Path.Combine(work, "bad.json");
            File.WriteAllText(cfg, "{ this is not valid json ");
            var r = RunCli("carve", src, "--config", cfg, "--roots", "run");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("not valid JSON", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_OutOverlapsSource_Refused_SourceIntact()
    {
        var src = MakeTree(out var work);
        try
        {
            var before = File.ReadAllText(Path.Combine(src, "main.c"));
            // --out == source with --prune would rewrite main.c in place; must be refused before any write.
            var r = RunCli("carve", src, "--roots", "run", "--out", src, "--prune");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("overwrite your source", outp);
            Assert.Equal(before, File.ReadAllText(Path.Combine(src, "main.c"))); // untouched
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_HappyPath_EmitsAndLeavesSourceUnchanged()
    {
        var src = MakeTree(out var work);
        try
        {
            var before = File.ReadAllText(Path.Combine(src, "main.c"));
            var outDir = Path.Combine(work, "out");
            var r = RunCli("carve", src, "--roots", "run", "--out", outDir);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "main.c")));      // emitted
            Assert.Equal(before, File.ReadAllText(Path.Combine(src, "main.c"))); // source unchanged
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_Verify_CleanCarve_Succeeds()
    {
        var src = MakeTree(out var work);
        try
        {
            var r = RunCli("carve", src, "--roots", "run", "--verify");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);                 // sound carve -> exit 0
            Assert.Contains("verify  : OK", outp);
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_OutNonEmptyNonCarveDir_Refused_DataIntact()
    {
        // Regression (eval #8 HIGH): the atomic emit must NOT wipe a pre-existing --out it didn't create.
        var src = MakeTree(out var work);
        try
        {
            var outDir = Path.Combine(work, "precious");
            Directory.CreateDirectory(Path.Combine(outDir, ".git"));
            File.WriteAllText(Path.Combine(outDir, "notes.txt"), "MINE");
            File.WriteAllText(Path.Combine(outDir, ".git", "HEAD"), "ref");

            var r = RunCli("carve", src, "--roots", "run", "--out", outDir);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("not empty and was not created by CodeCarver", outp);
            Assert.Equal("MINE", File.ReadAllText(Path.Combine(outDir, "notes.txt"))); // untouched
            Assert.True(File.Exists(Path.Combine(outDir, ".git", "HEAD")));            // .git untouched
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_OutNonEmptyNonCarveDir_WithClean_Succeeds()
    {
        var src = MakeTree(out var work);
        try
        {
            var outDir = Path.Combine(work, "precious");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "notes.txt"), "MINE");

            var r = RunCli("carve", src, "--roots", "run", "--out", outDir, "--clean");
            if (r is null) return;
            var (code, _) = r.Value;
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "main.c")));       // replaced with the carve
            Assert.False(File.Exists(Path.Combine(outDir, "notes.txt")));   // --clean authorized removal
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_ReEmitIntoPriorCarveOutput_NoCleanNeeded()
    {
        // A directory CodeCarver itself produced carries the marker, so a re-carve into it is seamless.
        var src = MakeTree(out var work);
        try
        {
            var outDir = Path.Combine(work, "out");
            var r1 = RunCli("carve", src, "--roots", "run", "--out", outDir);
            if (r1 is null) return;
            Assert.Equal(0, r1.Value.Code);

            var r2 = RunCli("carve", src, "--roots", "run", "--out", outDir); // again, no --clean
            Assert.Equal(0, r2!.Value.Code);
            Assert.True(File.Exists(Path.Combine(outDir, "main.c")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_Diag_WritesPackage()
    {
        var src = MakeTree(out var work);
        try
        {
            var zip = Path.Combine(work, "diag.zip");
            var r = RunCli("carve", src, "--roots", "run", "--diag", zip);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            Assert.True(File.Exists(zip));
            Assert.Contains("diagnostic package", outp);
        }
        finally { Cleanup(work); }
    }

    private static void Cleanup(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
    }
}
