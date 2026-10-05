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
            CodeBytesBefore: 900, CodeBytesAfter: 500, InfraBytes: 200,
            InfraEnumerated: true,
            RemovedGarbage: System.Array.Empty<string>(), GarbageBytes: 0));

        // Bucket section headers carry the counts (no alignment padding to depend on).
        Assert.Contains("== KEPT - required to build (3) ==", text);
        Assert.Contains("== REMOVED - not needed to build (2) ==", text);
        Assert.Contains("== KEPT - infrastructure / other, not code (4) ==", text);

        // Summary distinguishes reachable code from include closure, and they sum to BuildRequired.
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
    public void Render_AnalysisOnly_DoesNotFakeInfrastructure()
    {
        // No --out: infrastructure + include-closure aren't known, so the report must say so plainly rather
        // than mislabel build-required includes as infrastructure. Reachable code + dead code still shown.
        var text = CarveReport.Render(new CarveReport.Inputs(
            SourceRoot: "/src",
            Roots: new[] { "main" },
            BuildRequired: new[] { "main.c" },   // == KeptCode in analysis-only (no emit ran)
            KeptCode: new[] { "main.c" },
            RemovedDeadCode: new[] { "dead.c" },
            Infrastructure: System.Array.Empty<string>(),
            ExcludedDirs: System.Array.Empty<string>(),
            CodeBytesBefore: 100, CodeBytesAfter: 60, InfraBytes: 0,
            InfraEnumerated: false,
            RemovedGarbage: System.Array.Empty<string>(), GarbageBytes: 0));

        Assert.Contains("analysis-only", text);
        Assert.Contains("not enumerated", text);
        Assert.Contains("== REMOVED - not needed to build (1) ==", text);
        Assert.Contains("dead.c", text);
        Assert.DoesNotContain("passed through verbatim", text);   // no false infra claim
    }

    [Fact]
    public void Render_FlagsKeptButNotWritten()
    {
        // A kept file absent/locked on disk is in KeptCode but not BuildRequired (emit skipped it). The report
        // must not count it as written code; it gets its own honest section so the counts stay consistent.
        var text = CarveReport.Render(new CarveReport.Inputs(
            SourceRoot: "/src",
            Roots: new[] { "main" },
            BuildRequired: new[] { "main.c" },              // gone.c was skipped by the emitter
            KeptCode: new[] { "main.c", "gone.c" },
            RemovedDeadCode: System.Array.Empty<string>(),
            Infrastructure: System.Array.Empty<string>(),
            ExcludedDirs: System.Array.Empty<string>(),
            CodeBytesBefore: 10, CodeBytesAfter: 10, InfraBytes: 0,
            InfraEnumerated: true,
            RemovedGarbage: System.Array.Empty<string>(), GarbageBytes: 0));

        Assert.Contains("KEPT but NOT WRITTEN", text);
        Assert.Contains("gone.c", text);
        Assert.Contains("== KEPT - required to build (1) ==", text);   // only main.c is required-to-build
        Assert.Contains("1 code + 0 include-closure", text);           // gone.c NOT counted as code
    }

    [Theory]
    [InlineData("build.mk", "make fragment")]
    [InlineData("proj.cmake", "CMake")]
    [InlineData("app.cmd", "linker command / scatter")]
    [InlineData("boot.s", "assembly")]
    [InlineData("flash.cmm", "TRACE32 script")]
    [InlineData("soc.dts", "device tree")]
    [InlineData("libm.a", "prebuilt binary")]
    [InlineData("gen.inc", "generated table / data")]
    [InlineData("cfg.json", "config")]
    public void Render_DescribeExt_LabelsKnownKinds(string file, string hint)
    {
        var text = CarveReport.Render(new CarveReport.Inputs(
            "/s", System.Array.Empty<string>(),
            System.Array.Empty<string>(), System.Array.Empty<string>(),
            System.Array.Empty<string>(),
            new[] { file },
            System.Array.Empty<string>(), 0, 0, 1, InfraEnumerated: true,
            RemovedGarbage: System.Array.Empty<string>(), GarbageBytes: 0));
        Assert.Contains(hint, text);
    }

    [Fact]
    public void Render_ShowsGarbageBucket_And_RoleGroupedInfra_And_OutputReview()
    {
        var text = CarveReport.Render(new CarveReport.Inputs(
            SourceRoot: "/src",
            Roots: new[] { "main" },
            BuildRequired: new[] { "main.c" },
            KeptCode: new[] { "main.c" },
            RemovedDeadCode: System.Array.Empty<string>(),
            Infrastructure: new[] { "Makefile", "flash.ld", "cfg/board.json", "lib/libfoo.a", "README.md" },
            ExcludedDirs: System.Array.Empty<string>(),
            CodeBytesBefore: 100, CodeBytesAfter: 100, InfraBytes: 500,
            InfraEnumerated: true,
            RemovedGarbage: new[] { ".git/config", "src/main.c.bak", "build.log" }, GarbageBytes: 2048));

        // Garbage bucket: summary line, section, and the files, plus how to restore.
        Assert.Contains("REMOVED - garbage", text);
        Assert.Contains(".git/config", text);
        Assert.Contains("src/main.c.bak", text);
        Assert.Contains("forceKeepFiles", text);
        Assert.Contains("2,048 B", text);   // garbage byte total surfaced

        // Infra grouped by build ROLE.
        Assert.Contains("build system & toolchain", text);        // Makefile + flash.ld
        Assert.Contains("data / resources", text);                // board.json + libfoo.a
        Assert.Contains("other", text);                           // README.md

        // The prebuilt .a is flagged for review (may be a vendored prebuilt or a stray output).
        Assert.Contains("REVIEW - look like build outputs", text);
        Assert.Contains("lib/libfoo.a", text);
    }

    [Fact]
    public void Render_FileTraceObserved_TagsAndSummarizes()
    {
        var text = CarveReport.Render(new CarveReport.Inputs(
            SourceRoot: "/src",
            Roots: new[] { "main" },
            BuildRequired: new[] { "main.c" },
            KeptCode: new[] { "main.c" },
            RemovedDeadCode: System.Array.Empty<string>(),
            Infrastructure: new[] { "flash.ld", "data/tab.bin", "notes.md" },
            ExcludedDirs: System.Array.Empty<string>(),
            CodeBytesBefore: 10, CodeBytesAfter: 10, InfraBytes: 30,
            InfraEnumerated: true,
            RemovedGarbage: System.Array.Empty<string>(), GarbageBytes: 0,
            Observed: new[] { "flash.ld", "data/tab.bin" }));   // the run touched 2 of the 3 infra files

        Assert.Contains("file-trace: 2 of 3 observed", text);          // attribution summary
        Assert.Contains("1 NOT observed", text);                       // the drop-candidate count
        Assert.Contains("flash.ld  [observed]", text);                 // observed files tagged
        Assert.Contains("data/tab.bin  [observed]", text);
        Assert.DoesNotContain("notes.md  [observed]", text);           // unobserved file NOT tagged
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        var inputs = new CarveReport.Inputs(
            "/s", new[] { "r" },
            new[] { "b.c", "a.c" }, new[] { "b.c", "a.c" },
            new[] { "z.c", "a.c" },
            new[] { "b.mk", "a.ld" },
            System.Array.Empty<string>(), 10, 5, 1, InfraEnumerated: true,
            RemovedGarbage: new[] { ".git/config", "x.bak" }, GarbageBytes: 42);
        Assert.Equal(CarveReport.Render(inputs), CarveReport.Render(inputs));
    }
}
