using System.IO.Compression;
using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// In-process tests of the carve pipeline via <see cref="CarveCommand.Run"/>. Unlike the spawn-a-process
/// CLI integration tests (which validate behavior but can't be measured by coverlet), these exercise the
/// whole flow in-process with captured writers — so the CLI wiring finally gets measured coverage — and let
/// us assert on the diagnostic package's no-leak contract directly.
/// </summary>
public sealed class CarveCommandTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var code = CarveCommand.Run(args, so, se);
        return (code, so.ToString(), se.ToString());
    }

    private static string NewTree(out string outDir)
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-cmd-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int helper(void);\nint main(void){return helper();}\n");
        File.WriteAllText(Path.Combine(src, "helper.c"), "int helper(void){return 1;}\n");
        File.WriteAllText(Path.Combine(src, "dead.c"), "int never(void){return 9;}\n");
        File.WriteAllText(Path.Combine(src, "Makefile"), "all:\n\tgcc main.c helper.c\n");
        File.WriteAllText(Path.Combine(src, "flash.ld"), "MEMORY{}\n");
        return src;
    }

    private static void Cleanup(string src)
    {
        try { var work = Directory.GetParent(src)!.FullName; if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
    }

    [Fact]
    public void Run_CarvesTree_EmitsCompleteBuildableProject_Exit0()
    {
        var src = NewTree(out var outDir);
        try
        {
            var (code, o, _) = Run("carve", src, "--roots", "main", "--out", outDir);
            Assert.Equal(0, code);
            // Reachable code emitted; dead code dropped; infrastructure passed through -> buildable project.
            Assert.True(File.Exists(Path.Combine(outDir, "main.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "helper.c")));
            Assert.False(File.Exists(Path.Combine(outDir, "dead.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "Makefile")));   // keep-by-default passthrough
            Assert.True(File.Exists(Path.Combine(outDir, "flash.ld")));
            Assert.Contains("emitted", o);
            Assert.Contains("passthru", o);
            Assert.Contains("buckets", o);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_MissingRoots_Exit2()
    {
        var src = NewTree(out _);
        try
        {
            var (code, _, err) = Run("carve", src);
            Assert.Equal(2, code);
            Assert.Contains("--roots", err);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_UnknownFlag_Exit2()
    {
        var src = NewTree(out _);
        try
        {
            var (code, _, err) = Run("carve", src, "--roots", "main", "--prun");   // typo
            Assert.Equal(2, code);
            Assert.Contains("unknown or incomplete option", err);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_StrictRoots_UnresolvedRoot_Exit1()
    {
        var src = NewTree(out _);
        try
        {
            var (code, _, err) = Run("carve", src, "--roots", "main,ghost_symbol", "--strict-roots");
            Assert.Equal(1, code);
            Assert.Contains("strict-roots", err);
            Assert.Contains("ghost_symbol", err);   // named as unresolved
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_Verify_SoundCarve_Exit0_ReportsOk()
    {
        var src = NewTree(out _);
        try
        {
            var (code, o, _) = Run("carve", src, "--roots", "main", "--verify");
            Assert.Equal(0, code);
            Assert.Contains("verify", o);
            Assert.Contains("OK", o);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_AnalysisOnly_Report_ScopedHonestly()
    {
        var src = NewTree(out _);
        var report = Path.Combine(Directory.GetParent(src)!.FullName, "r.txt");
        try
        {
            var (code, _, _) = Run("carve", src, "--roots", "main", "--report", report);
            Assert.Equal(0, code);
            var rpt = File.ReadAllText(report);
            Assert.Contains("analysis-only", rpt);          // no --out -> honest scoping
            Assert.Contains("dead.c", rpt);                 // dead-code bucket
            Assert.Contains("not enumerated", rpt);         // infra not faked
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_DiagPackage_DoesNotLeakRootNamesOrSourcePath()
    {
        // The core privacy contract, exercised end-to-end in-process: a carve with --diag produces a package
        // that carries NEITHER the root symbol names NOR the raw command-line values.
        var src = NewTree(out _);
        var zip = Path.Combine(Directory.GetParent(src)!.FullName, "diag.zip");
        try
        {
            var (code, _, _) = Run("carve", src, "--roots", "main,helper", "--diag", zip);
            Assert.Equal(0, code);
            Assert.True(File.Exists(zip));

            using var z = ZipFile.OpenRead(zip);
            string Entry(string n) { using var r = new StreamReader(z.GetEntry(n)!.Open()); return r.ReadToEnd(); }
            var summary = Entry("summary.txt");
            var json = Entry("diagnostics.json");

            // Root symbol names must not appear anywhere in the shareable package.
            Assert.DoesNotContain("helper", summary);
            Assert.DoesNotContain("helper", json);
            // Command line is recorded with values elided, not verbatim.
            Assert.Contains("--roots <elided>", summary);
            Assert.DoesNotContain("--roots main", summary);
            // Root count is recorded instead of the names.
            Assert.Contains("rootCount", summary);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_NonexistentDir_Exit2()
    {
        var (code, _, err) = Run("carve", Path.Combine(Path.GetTempPath(), "cc-nope-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(2, code);
        Assert.Contains("usage", err);
    }

    [Fact]
    public void Run_Config_SuppliesDefaults()
    {
        var src = NewTree(out var outDir);
        var cfg = Path.Combine(Directory.GetParent(src)!.FullName, "carve.json");
        try
        {
            File.WriteAllText(cfg, "{ \"roots\": [\"main\"], \"lang\": \"c\", \"out\": \"" + outDir.Replace("\\", "\\\\") + "\" }");
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "main.c")));   // roots + out came from the config
            Assert.Contains("emitted", o);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_BuildLog_ParsesCompileCommands()
    {
        var src = NewTree(out _);
        var log = Path.Combine(Directory.GetParent(src)!.FullName, "build.txt");
        try
        {
            File.WriteAllText(log, "gcc -DFEATURE=1 -Iinc -c main.c\ngcc -c helper.c\n");
            var (code, _, err) = Run("carve", src, "--roots", "main", "--build-log", log);
            Assert.Equal(0, code);
            Assert.Contains("compile command", err);   // the build summary line
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_Manifest_ListsBucketsIncludingInfrastructure()
    {
        var src = NewTree(out var outDir);
        var man = Path.Combine(Directory.GetParent(src)!.FullName, "m.json");
        try
        {
            var (code, _, _) = Run("carve", src, "--roots", "main", "--out", outDir, "--manifest", man);
            Assert.Equal(0, code);
            var json = File.ReadAllText(man);
            Assert.Contains("infrastructureFiles", json);
            Assert.Contains("Makefile", json);      // infra listed
            Assert.Contains("droppedFiles", json);
            Assert.Contains("dead.c", json);        // dead code listed as dropped
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_Prune_EmitsAndFlagsExperimental()
    {
        var src = NewTree(out var outDir);
        try
        {
            var (code, o, _) = Run("carve", src, "--roots", "main", "--out", outDir, "--prune");
            Assert.Equal(0, code);
            Assert.Contains("pruned", o);
            Assert.Contains("EXPERIMENTAL", o);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_DumpSpans_ListsKeepAndDrop()
    {
        var src = NewTree(out _);
        try
        {
            var (code, o, _) = Run("carve", src, "--roots", "main", "--dump-spans");
            Assert.Equal(0, code);
            Assert.Contains("KEEP", o);
            Assert.Contains("drop", o);   // dead() shows as drop
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_Why_ExplainsKeepChain()
    {
        var src = NewTree(out _);
        try
        {
            var (code, o, _) = Run("carve", src, "--roots", "main", "--why", "helper");
            Assert.Equal(0, code);
            Assert.Contains("helper", o);   // the explained symbol + its keep-chain
        }
        finally { Cleanup(src); }
    }

    private static void AddGarbage(string src)
    {
        Directory.CreateDirectory(Path.Combine(src, ".git"));
        File.WriteAllText(Path.Combine(src, ".git", "config"), "[core]\n");
        File.WriteAllText(Path.Combine(src, "main.c.bak"), "stale\n");
    }

    [Fact]
    public void Run_PrunesGarbageByDefault_ReportsIt_StillBuildable()
    {
        var src = NewTree(out var outDir);
        AddGarbage(src);
        var man = Path.Combine(Directory.GetParent(src)!.FullName, "m.json");
        try
        {
            var (code, o, _) = Run("carve", src, "--roots", "main", "--out", outDir, "--manifest", man);
            Assert.Equal(0, code);
            // Garbage not copied; real infra still there.
            Assert.False(File.Exists(Path.Combine(outDir, ".git", "config")));
            Assert.False(File.Exists(Path.Combine(outDir, "main.c.bak")));
            Assert.True(File.Exists(Path.Combine(outDir, "Makefile")));
            // Console + manifest surface it.
            Assert.Contains("garbage", o);
            var json = File.ReadAllText(man);
            Assert.Contains("removedGarbageFiles", json);
            Assert.Contains(".git/config", json);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_KeepGarbage_KeepsEverything()
    {
        var src = NewTree(out var outDir);
        AddGarbage(src);
        try
        {
            var (code, _, _) = Run("carve", src, "--roots", "main", "--out", outDir, "--keep-garbage");
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, ".git", "config")));   // kept when disabled
            Assert.True(File.Exists(Path.Combine(outDir, "main.c.bak")));
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_Aux_ForcesOneGarbageFileBack()
    {
        var src = NewTree(out var outDir);
        AddGarbage(src);
        try
        {
            var (code, _, _) = Run("carve", src, "--roots", "main", "--out", outDir, "--aux", "main.c.bak");
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "main.c.bak")));       // forced back
            Assert.False(File.Exists(Path.Combine(outDir, ".git", "config"))); // rest still pruned
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public void Run_DiagRepro_AttachesAnonymizedGraph_NoRealNames()
    {
        var src = NewTree(out _);
        var zip = Path.Combine(Directory.GetParent(src)!.FullName, "d.zip");
        try
        {
            var (code, _, _) = Run("carve", src, "--roots", "main", "--diag", zip, "--diag-repro");
            Assert.Equal(0, code);
            using var z = ZipFile.OpenRead(zip);
            var entry = z.GetEntry("repro.graph.json");
            Assert.NotNull(entry);
            using var r = new StreamReader(entry!.Open());
            var graph = r.ReadToEnd();
            Assert.DoesNotContain("helper", graph);   // anonymized: real symbol names tokenized away
        }
        finally { Cleanup(src); }
    }
}
