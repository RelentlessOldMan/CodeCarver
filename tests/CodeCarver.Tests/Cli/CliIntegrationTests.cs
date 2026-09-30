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
    public void Carve_MacroDenseHeader_AutoKeptWhole_NormalLargeFileStillParsed()
    {
        // eval-#14 fix: a giant register/header map (5-16 MB of #defines, transitively #included) sits UNDER
        // --max-parse-bytes yet drives the parser to tens of GB (a graph node per #define + retained AST/text).
        // It must be auto-detected by content and kept WHOLE (never parsed) with NO magic flag. A NORMAL large
        // .c (mostly code) must NOT be misclassified — it's still parsed. main.c #includes the dense header and
        // calls run; the carve must succeed, report the header on the 'dense' line, and emit it verbatim.
        var work = Path.Combine(Path.GetTempPath(), "cc-dense-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(work, "proj");
        try
        {
            Directory.CreateDirectory(proj);
            // ~1.7 MB of pure #defines -> macro-dense (>= 1 MB and >60% #define lines). N_DEFINES is large so
            // the "one graph node per #define" explosion is unmistakable: parsing this would add ~60k Macro
            // nodes (the eval-#14 memory blowup); the fix must keep it whole so those nodes never exist.
            const int N_DEFINES = 60_000;
            var regs = new System.Text.StringBuilder(1_700_000);
            for (int i = 0; i < N_DEFINES; i++) regs.Append("#define REG_").Append(i).Append(" 0x").Append(i.ToString("X6")).Append('\n');
            File.WriteAllText(Path.Combine(proj, "regs.h"), regs.ToString());
            // ~1.1 MB of ordinary code -> NOT dense (mostly non-#define lines); must still be parsed. Only a
            // few thousand functions (padded to size), so a correct run's total node count stays well under
            // the >60k it would be if regs.h had been parsed -- that gap is the regression assertion below.
            const int N_FUNCS = 3_000;
            var pad = new string('x', 320); // comment padding to push each line past the big-file worker floor
            var bulk = new System.Text.StringBuilder(1_200_000);
            for (int i = 0; i < N_FUNCS; i++)
                bulk.Append("int fn_").Append(i).Append("(void){ return ").Append(i).Append("; } /* ").Append(pad).Append(" */\n");
            File.WriteAllText(Path.Combine(proj, "bulk.c"), bulk.ToString());
            File.WriteAllText(Path.Combine(proj, "main.c"),
                "#include \"regs.h\"\nint run(void){return REG_1;}\nint main(void){return run();}\n");
            var outDir = Path.Combine(work, "out");

            var r = RunCli("carve", proj, "--roots", "main,run", "--out", outDir);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            // The 'dense' report line names regs.h (auto-kept-whole) but NOT bulk.c — the normal large .c is
            // instead parsed (it shows up on a 'bigparse:' line, proving it was NOT misclassified as dense).
            var denseLine = outp.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("dense")) ?? "";
            Assert.Contains("regs.h", denseLine);
            Assert.DoesNotContain("bulk.c", denseLine);
            Assert.Contains("bulk.c", outp);                                  // present (parsed), just not dense

            // THE MECHANISM ASSERTION: the eval-#14 blowup was one graph node per #define. The 'nodes : R/T'
            // line reports total graph nodes T. bulk.c contributes ~N_FUNCS nodes; if regs.h had been parsed
            // T would exceed N_DEFINES (~60k). Assert T is far below that -> the 60k defines became ZERO nodes
            // (header kept whole, no explosion). This pins the actual cause, deterministically, without the
            // flakiness of measuring RAM.
            var nodesMatch = System.Text.RegularExpressions.Regex.Match(outp, @"nodes\s*:\s*[\d,]+/([\d,]+)\s+kept");
            Assert.True(nodesMatch.Success, "expected a 'nodes : R/T kept' line; got:\n" + outp);
            var totalNodes = long.Parse(nodesMatch.Groups[1].Value.Replace(",", ""));
            Assert.True(totalNodes < 20_000,
                $"total graph nodes = {totalNodes:N0}; expected < 20,000. A count near/above {N_DEFINES:N0} means " +
                "the dense header was parsed (one node per #define) — the eval-#14 explosion regressed.");

            Assert.True(File.Exists(Path.Combine(outDir, "regs.h")));         // kept whole (via #include-closure)
            Assert.Equal(regs.ToString(), File.ReadAllText(Path.Combine(outDir, "regs.h"))); // verbatim, unmodified
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_KeptFiles_EmittedByteExact_RegardlessOfEncoding()
    {
        // The sound default (file-level, no --prune) copies kept files verbatim, so any encoding — Latin-1,
        // UTF-16, a UTF-8 BOM, exotic bytes in comments — round-trips byte-for-byte. A firmware tree carries
        // such files; the carve must never silently re-encode or corrupt them.
        var work = Path.Combine(Path.GetTempPath(), "cc-enc-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(work, "proj");
        try
        {
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "main.c"),
                "#include \"latin1.h\"\n#include \"utf16.h\"\nint run(void){return 1;}\nint main(void){return run();}\n");

            // A Latin-1 header with a non-ASCII byte in a comment (0xB0 = degree sign) — invalid UTF-8.
            var latin1 = new byte[] { (byte)'/', (byte)'*', (byte)' ', 0xB0, (byte)'C', (byte)' ', (byte)'*', (byte)'/', (byte)'\n' };
            File.WriteAllBytes(Path.Combine(proj, "latin1.h"), latin1);
            // A UTF-16 LE file with BOM.
            var utf16 = System.Text.Encoding.Unicode.GetBytes("/* utf16 header */\n");
            var utf16WithBom = System.Text.Encoding.Unicode.GetPreamble().Concat(utf16).ToArray();
            File.WriteAllBytes(Path.Combine(proj, "utf16.h"), utf16WithBom);

            var outDir = Path.Combine(work, "out");
            var r = RunCli("carve", proj, "--roots", "main,run", "--out", outDir);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            // Byte-for-byte identical in the output (kept whole via include-closure, copied verbatim).
            Assert.Equal(latin1, File.ReadAllBytes(Path.Combine(outDir, "latin1.h")));
            Assert.Equal(utf16WithBom, File.ReadAllBytes(Path.Combine(outDir, "utf16.h")));
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

    [Fact]
    public void Carve_BuildLog_AcceptsCompileCommandsJson()
    {
        // --build-log must consume a compile_commands.json (CMake + the synthetic generator emit this), not
        // just a text log. Proven end to end: the CLI reports the scraped compile-command count.
        var src = MakeTree(out var work);
        try
        {
            var main = Path.Combine(src, "main.c");
            var db = Path.Combine(work, "compile_commands.json");
            File.WriteAllText(db, "[ { \"directory\": \"" + src.Replace("\\", "/") +
                "\", \"file\": \"" + main.Replace("\\", "/") +
                "\", \"command\": \"gcc -DFEATURE_X -I. -c main.c\" } ]");

            var r = RunCli("carve", src, "--roots", "run", "--build-log", db);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            Assert.Contains("compile command(s)", outp); // the DB was parsed + used (build-log line)
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_BuildLogRelativeInclude_ResolvesAgainstEntryDirectory_NotCarveRoot()
    {
        // eval-#9 HIGH: a relative -I in a compile_commands.json entry must resolve against THAT entry's
        // 'directory', not the carve root. main.c (in src/app) includes "table.inc"; the real one is in
        // src/app/cfg (via -Icfg from directory=src/app); a same-named DECOY sits at the carve root's cfg/.
        // Resolving -Icfg against the carve root would wrongly emit the decoy and drop the real file.
        var work = Path.Combine(Path.GetTempPath(), "cc-idir-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(work, "proj");
        var app = Path.Combine(proj, "src", "app");
        try
        {
            Directory.CreateDirectory(Path.Combine(app, "cfg"));
            Directory.CreateDirectory(Path.Combine(proj, "cfg"));
            File.WriteAllText(Path.Combine(app, "main.c"), "#include \"table.inc\"\nint main(void){return 0;}\n");
            File.WriteAllText(Path.Combine(app, "cfg", "table.inc"), "/* REAL */\n");
            File.WriteAllText(Path.Combine(proj, "cfg", "table.inc"), "/* DECOY */\n");
            var db = Path.Combine(work, "cc.json");
            File.WriteAllText(db, "[ { \"directory\": \"" + app.Replace("\\", "/") +
                "\", \"file\": \"main.c\", \"arguments\": [\"gcc\", \"-Icfg\", \"-c\", \"main.c\"] } ]");
            var outDir = Path.Combine(work, "out");
            var report = Path.Combine(work, "report.txt");

            var r = RunCli("carve", proj, "--roots", "main", "--build-log", db, "--out", outDir, "--report", report);
            if (r is null) return;
            var (code, _) = r.Value;
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "src", "app", "cfg", "table.inc")));  // REAL kept

            // Keep-by-default makes --out a complete buildable project, so the DECOY (a real file the carve
            // didn't resolve as the include) is ALSO copied — but as passthrough INFRASTRUCTURE, not as the
            // resolved include. Resolution correctness (eval-#9 HIGH: -Icfg resolves against the entry's
            // 'directory', picking src/app/cfg, not the carve-root decoy) now shows in WHICH BUCKET each lands:
            // the REAL one is required-to-build (include closure); the decoy is infrastructure-only.
            var rpt = File.ReadAllText(report);
            var infraIdx = rpt.IndexOf("== KEPT - infrastructure", StringComparison.Ordinal);
            Assert.True(infraIdx > 0, "report should have an infrastructure section");
            var buildRequired = rpt.Substring(0, infraIdx);
            var infrastructure = rpt.Substring(infraIdx);
            Assert.Contains("\n    src/app/cfg/table.inc", buildRequired);  // REAL resolved as the include (build-required)
            Assert.Contains("\n    cfg/table.inc", infrastructure);         // DECOY only passed through (infrastructure)
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_BuildLog_PerTuDefines_KeepsBranchAnotherTuCompiles()
    {
        // eval-#9 per-TU soundness: widget.c is compiled BOTH with and without -DFEATURE. A root calls
        // fallback_impl, which lives in the #else branch (compiled by the no-FEATURE TU). Unioning defines
        // would mark FEATURE defined globally, kill the #else, drop fallback_impl -> widget.c unreachable
        // and dropped (unsound). Per-file config: FEATURE is inconsistent for widget.c -> UNKNOWN -> both
        // branches kept -> fallback_impl reachable -> widget.c emitted.
        var work = Path.Combine(Path.GetTempPath(), "cc-pertu-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(work, "proj");
        try
        {
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "main.c"),
                "int fallback_impl(void);\nint run(void){return fallback_impl();}\nint main(void){return run();}\n");
            File.WriteAllText(Path.Combine(proj, "widget.c"),
                "#ifdef FEATURE\nint feature_impl(void){return 1;}\n#else\nint fallback_impl(void){return 2;}\n#endif\n");
            var d = proj.Replace("\\", "/");
            var db = Path.Combine(work, "cc.json");
            File.WriteAllText(db,
                "[ {\"directory\":\"" + d + "\",\"file\":\"main.c\",\"arguments\":[\"gcc\",\"-c\",\"main.c\"]}," +
                "  {\"directory\":\"" + d + "\",\"file\":\"widget.c\",\"arguments\":[\"gcc\",\"-DFEATURE\",\"-c\",\"widget.c\"]}," +
                "  {\"directory\":\"" + d + "\",\"file\":\"widget.c\",\"arguments\":[\"gcc\",\"-c\",\"widget.c\"]} ]");
            var outDir = Path.Combine(work, "out");

            var r = RunCli("carve", proj, "--roots", "main", "--build-log", db, "--out", outDir);
            if (r is null) return;
            var (code, _) = r.Value;
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outDir, "widget.c")));   // #else branch kept -> reachable
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_BuildLog_UnityIncludedC_UsesUniversalConfig()
    {
        // eval-#11 fix #1: impl.c is compiled standalone with -DFEATURE, but also #included by unity.c
        // (compiled WITHOUT it). Keying per-file config by path would give impl.c only its -DFEATURE config,
        // killing the #else (other()) that unity.c's TU actually compiles. impl.c must fall back to the
        // universal config -> both branches analyzed -> other() is extracted (visible in --dump-spans).
        var work = Path.Combine(Path.GetTempPath(), "cc-unity-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(work, "proj");
        try
        {
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "main.c"), "int run_unity(void);\nint main(void){return run_unity();}\n");
            File.WriteAllText(Path.Combine(proj, "unity.c"), "#include \"impl.c\"\nint run_unity(void){return other();}\n");
            File.WriteAllText(Path.Combine(proj, "impl.c"),
                "#ifdef FEATURE\nint feat(void){return 1;}\n#else\nint other(void){return 2;}\n#endif\n");
            var d = proj.Replace("\\", "/");
            var db = Path.Combine(work, "cc.json");
            File.WriteAllText(db,
                "[ {\"directory\":\"" + d + "\",\"file\":\"main.c\",\"arguments\":[\"gcc\",\"-c\",\"main.c\"]}," +
                "  {\"directory\":\"" + d + "\",\"file\":\"unity.c\",\"arguments\":[\"gcc\",\"-c\",\"unity.c\"]}," +
                "  {\"directory\":\"" + d + "\",\"file\":\"impl.c\",\"arguments\":[\"gcc\",\"-DFEATURE\",\"-c\",\"impl.c\"]} ]");

            var r = RunCli("carve", proj, "--roots", "main", "--build-log", db, "--dump-spans");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            Assert.Contains("other", outp);   // #else branch analyzed -> impl.c used the universal config
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_BuildLog_ClosedWorld_VaryingMacro_KeepsBothBranches()
    {
        // eval-#12: under closed-world (--assume-defines-complete, same path --probe takes), a macro that
        // VARIES across a file's compile commands must be treated as UNKNOWN, not absent-hence-undefined —
        // else the #ifdef branch dies. impl.c compiled BOTH with and without -DFEATURE => both feat and other
        // must be analyzed. (--assume-defines-complete exercises the closed-world path with no compiler.)
        var work = Path.Combine(Path.GetTempPath(), "cc-cw-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(work, "proj");
        try
        {
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "main.c"), "int main(void){return 0;}\n");
            File.WriteAllText(Path.Combine(proj, "impl.c"),
                "#ifdef FEATURE\nint feat(void){return 1;}\n#else\nint other(void){return 2;}\n#endif\n");
            var d = proj.Replace("\\", "/");
            var db = Path.Combine(work, "cc.json");
            File.WriteAllText(db,
                "[ {\"directory\":\"" + d + "\",\"file\":\"main.c\",\"arguments\":[\"gcc\",\"-c\",\"main.c\"]}," +
                "  {\"directory\":\"" + d + "\",\"file\":\"impl.c\",\"arguments\":[\"gcc\",\"-DFEATURE\",\"-c\",\"impl.c\"]}," +
                "  {\"directory\":\"" + d + "\",\"file\":\"impl.c\",\"arguments\":[\"gcc\",\"-c\",\"impl.c\"]} ]");

            var r = RunCli("carve", proj, "--roots", "main", "--build-log", db, "--assume-defines-complete", "--dump-spans");
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(0, code);
            Assert.Contains("feat", outp);    // #ifdef branch analyzed (would be dropped by closed-world if absent, not unknown)
            Assert.Contains("other", outp);   // #else branch analyzed
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Carve_OutIsAFile_RefusedUpFront()
    {
        var src = MakeTree(out var work);
        try
        {
            var f = Path.Combine(work, "notadir.txt");
            File.WriteAllText(f, "x");
            var r = RunCli("carve", src, "--roots", "run", "--out", f);
            if (r is null) return;
            var (code, outp) = r.Value;
            Assert.Equal(2, code);
            Assert.Contains("is a file", outp);
        }
        finally { Cleanup(work); }
    }

    private static void Cleanup(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
    }
}
