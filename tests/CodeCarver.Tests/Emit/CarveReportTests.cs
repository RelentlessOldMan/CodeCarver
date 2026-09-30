using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>The carve report is the auditable account of a carve: it must show the three buckets with
/// correct counts and group infrastructure by kind, so a reviewer can confirm nothing load-bearing was
/// dropped and see exactly what non-code was passed through.</summary>
public sealed class CarveReportTests
{
    [Fact]
    public void Render_ShowsThreeBuckets_WithCountsAndInfraGrouping()
    {
        var text = CarveReport.Render(new CarveReport.Inputs(
            SourceRoot: "/src",
            Roots: new[] { "main" },
            BuildRequired: new[] { "main.c", "util.c", "tables.inc" },   // 2 code + 1 include-closure
            KeptCode: new[] { "main.c", "util.c" },
            RemovedDeadCode: new[] { "dead.c", "old/legacy.c" },
            Infrastructure: new[] { "Makefile", "flash.ld", "board/variant.ld", "data.bin" },
            ExcludedDirs: new[] { "tests" },
            CodeBytesBefore: 900, CodeBytesAfter: 500, InfraBytes: 200));

        // Bucket section headers carry the counts (no alignment padding to depend on).
        Assert.Contains("== KEPT - required to build (3) ==", text);
        Assert.Contains("== REMOVED - not needed to build (2) ==", text);
        Assert.Contains("== KEPT - infrastructure / other, not code (4) ==", text);

        // Summary distinguishes reachable code from include closure.
        Assert.Contains("2 code + 1 include-closure", text);

        // Every file appears under some bucket.
        foreach (var f in new[] { "main.c", "util.c", "tables.inc", "dead.c", "old/legacy.c",
                                  "Makefile", "flash.ld", "board/variant.ld", "data.bin" })
            Assert.Contains(f, text);

        // Infrastructure grouped by extension with a role hint; the two .ld files land in one group.
        Assert.Contains(".ld (2) - linker script", text);
        Assert.Contains("(no extension) (1)", text);   // Makefile

        // Excluded dirs are surfaced.
        Assert.Contains("tests", text);
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        var inputs = new CarveReport.Inputs(
            "/s", new[] { "r" },
            new[] { "b.c", "a.c" }, new[] { "b.c", "a.c" },
            new[] { "z.c", "a.c" },
            new[] { "b.mk", "a.ld" },
            Array.Empty<string>(), 10, 5, 1);
        Assert.Equal(CarveReport.Render(inputs), CarveReport.Render(inputs));
    }
}
