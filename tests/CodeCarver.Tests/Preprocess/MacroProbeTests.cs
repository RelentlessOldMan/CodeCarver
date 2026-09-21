using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>Probes the pinned gcc for its macro set and checks #ifdef resolution runs against it.
/// Skips when the toolchain isn't fetched.</summary>
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
        return null;
    }

    [Fact]
    public void Probe_ReturnsCompilerPredefinedMacros()
    {
        var gcc = Gcc();
        if (gcc is null) return;
        var table = MacroProbe.Probe(gcc);
        Assert.NotNull(table);
        Assert.True(table!.IsDefined("__GNUC__")); // every gcc predefines this
    }

    [Fact]
    public void Probe_HonoursDashD_AndDrivesClosedWorldResolution()
    {
        var gcc = Gcc();
        if (gcc is null) return;
        var table = MacroProbe.Probe(gcc, new[] { "-DMY_FEATURE=1" });
        Assert.NotNull(table);

        Assert.True(table!.IsDefined("MY_FEATURE"));
        // Closed-world against the complete probed set: a supplied feature is on, an absent one is off.
        Assert.Equal(Tri.True,
            PreprocessorScanner.EvaluateCondition("defined(MY_FEATURE) && !defined(SOME_ABSENT_MACRO)", table, closedWorld: true));
    }
}
