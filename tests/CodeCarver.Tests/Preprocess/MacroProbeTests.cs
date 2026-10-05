using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>Probes a real gcc for its macro set and checks #ifdef resolution runs against it. Uses the pinned
/// toolchain when fetched, otherwise a gcc/cc on PATH (CI images have one); reports SKIPPED — not Passed — only
/// when neither exists.</summary>
public class MacroProbeTests
{
    private static string? Gcc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, ".toolchains", "w64devkit", "bin", "gcc.exe");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        // Fall back to a compiler on PATH, so the success path is exercised wherever one is installed.
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe" } : new[] { "" };
        foreach (var name in new[] { "gcc", "cc" })
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                foreach (var ext in exts)
                {
                    string cand;
                    try { cand = Path.Combine(p.Trim(), name + ext); } catch (ArgumentException) { continue; }
                    if (File.Exists(cand)) return cand;
                }
            }
        return null;
    }

    [SkippableFact]
    public void Probe_ReturnsCompilerPredefinedMacros()
    {
        var gcc = Gcc();
        Skip.If(gcc is null, "no gcc: neither the pinned toolchain (.toolchains/w64devkit) nor gcc/cc on PATH");
        var table = MacroProbe.Probe(gcc!);
        Assert.NotNull(table);
        Assert.True(table!.IsDefined("__GNUC__")); // every gcc (and clang-as-cc) predefines this
    }

    [Fact]
    public void Probe_MissingCompiler_ReturnsNull_NotThrowOrEmptyTable()
    {
        // Contract (environment-independent): if the compiler can't be run, Probe returns null so the caller
        // does NOT enable closed-world. Returning an EMPTY table instead would be a silent soundness bug —
        // under closed-world every macro would look undefined and real branches would be dropped.
        var table = MacroProbe.Probe("cc-nonexistent-compiler-zzz-" + Guid.NewGuid().ToString("N"));
        Assert.Null(table);
    }

    [SkippableFact]
    public void Probe_HonoursDashD_AndDrivesClosedWorldResolution()
    {
        var gcc = Gcc();
        Skip.If(gcc is null, "no gcc: neither the pinned toolchain (.toolchains/w64devkit) nor gcc/cc on PATH");
        var table = MacroProbe.Probe(gcc!, new[] { "-DMY_FEATURE=1" });
        Assert.NotNull(table);

        Assert.True(table!.IsDefined("MY_FEATURE"));
        // Closed-world against the complete probed set: a supplied feature is on, an absent one is off.
        Assert.Equal(Tri.True,
            PreprocessorScanner.EvaluateCondition("defined(MY_FEATURE) && !defined(SOME_ABSENT_MACRO)", table, closedWorld: true));
    }
}
