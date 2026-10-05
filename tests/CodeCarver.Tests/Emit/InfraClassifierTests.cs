using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The infrastructure classifier is the soundness-sensitive piece of garbage pruning: it decides what the
/// keep-by-default emitter is allowed to drop WITHOUT evidence from the carve. The contract is "airtight only"
/// — a file is garbage iff it cannot be a build/run input by universal convention (VCS metadata, compiler/IDE
/// scratch, dep/coverage artifacts, editor/OS junk, logs/temp). Anything that COULD be a vendored prebuilt or a
/// real input (.o/.a/.so/.lib, bin/, build/) must NOT be classified garbage. These pin that line.
/// </summary>
public sealed class InfraClassifierTests
{
    [Theory]
    // VCS metadata — never a build input.
    [InlineData(".git/config")]
    [InlineData(".git/objects/ab/cdef")]
    [InlineData("sub/.svn/entries")]
    [InlineData(".hg/store/data")]
    // Compiler / IDE scratch dirs.
    [InlineData("build/CMakeFiles/foo.dir/x.c.o")]
    [InlineData("tools/__pycache__/gen.cpython-311.pyc")]
    [InlineData(".vs/CodeCarver/v17/Browse.VC.db")]
    // Coverage artifacts (a .d is judged by content - see below).
    [InlineData("cov/main.gcda")]
    [InlineData("cov/main.gcno")]
    [InlineData("cov/main.c.gcov")]
    [InlineData("gen/table.pyc")]
    // Editor / OS junk.
    [InlineData("src/main.c.bak")]
    [InlineData("src/patch.orig")]
    [InlineData(".main.c.swp")]
    [InlineData("src/.DS_Store")]
    [InlineData("docs/Thumbs.db")]
    [InlineData("notes.txt~")]
    // Logs / temp.
    [InlineData("build.log")]
    [InlineData("scratch.tmp")]
    public void IsGarbage_True_ForProvablyNonInputs(string rel)
        => Assert.True(InfraClassifier.IsGarbage(rel), rel);

    [Theory]
    [InlineData("main.o: main.c main.h \\\n  config.h\n", true)]          // gcc -MD
    [InlineData("\nobj/x.obj : x.c\n", true)]
    [InlineData("module app.main;\nimport std.stdio;\n", false)]        // D source
    [InlineData("syscall::open:entry\n{\n  printf(\"%s\", copyinstr(arg0));\n}\n", false)] // DTrace
    public void DotD_IsGarbageOnlyWhenItIsAMakeDependencyFile_RB15(string content, bool garbage)
    {
        var p = Path.Combine(Path.GetTempPath(), "cc-dfile-" + Guid.NewGuid().ToString("N") + ".d");
        File.WriteAllText(p, content);
        try
        {
            Assert.Equal(garbage, InfraClassifier.IsGarbage("obj/x.d", p));
            Assert.False(InfraClassifier.IsGarbage("obj/x.d"));   // never by name alone
        }
        finally { File.Delete(p); }
    }

    [Theory]
    // Real source / headers / build files — obviously not garbage.
    [InlineData("src/main.c")]
    [InlineData("inc/types.h")]
    [InlineData("Makefile")]
    [InlineData("flash.ld")]
    [InlineData("cfg/board.json")]
    // AMBIGUOUS build outputs / prebuilts — could be vendored and load-bearing, so NEVER auto-garbage.
    [InlineData("lib/libfoo.a")]
    [InlineData("vendor/crypto.o")]
    [InlineData("prebuilt/radio.so")]
    [InlineData("bin/bootloader.bin")]
    [InlineData("build/image.hex")]
    [InlineData("x.lib")]
    [InlineData("y.dll")]
    [InlineData("z.exe")]
    public void IsGarbage_False_ForInputsAndAmbiguousBinaries(string rel)
        => Assert.False(InfraClassifier.IsGarbage(rel), rel);

    [Fact]
    public void IsGarbage_IsCaseInsensitive_OnWindowsStylePaths()
    {
        Assert.True(InfraClassifier.IsGarbage(".GIT/CONFIG"));
        Assert.True(InfraClassifier.IsGarbage("Src/Main.C.BAK"));
    }

    [Theory]
    [InlineData("Makefile", InfraRole.BuildSystem)]
    [InlineData("build/foo.mk", InfraRole.BuildSystem)]
    [InlineData("CMakeLists.txt", InfraRole.BuildSystem)]
    [InlineData("flash.ld", InfraRole.BuildSystem)]
    [InlineData("startup.s", InfraRole.BuildSystem)]
    [InlineData("debug.cmm", InfraRole.BuildSystem)]
    [InlineData("cfg/board.json", InfraRole.Data)]
    [InlineData("tables/opcodes.inc", InfraRole.Data)]
    [InlineData("soc.dtsi", InfraRole.Data)]
    [InlineData("lib/libfoo.a", InfraRole.Data)]
    [InlineData("README.md", InfraRole.Other)]
    [InlineData("fonts/logo.png", InfraRole.Other)]
    public void RoleOf_GroupsByBuildRole(string rel, InfraRole expected)
        => Assert.Equal(expected, InfraClassifier.RoleOf(rel));

    [Theory]
    [InlineData("vendor/crypto.o", true)]
    [InlineData("lib/libfoo.a", true)]
    [InlineData("bin/boot.bin", true)]
    [InlineData("fw.hex", true)]
    [InlineData("link.map", true)]
    [InlineData("src/main.c", false)]
    [InlineData("cfg/board.json", false)]
    public void LooksLikeBuildOutput_FlagsBinariesForReview(string rel, bool expected)
        => Assert.Equal(expected, InfraClassifier.LooksLikeBuildOutput(rel));
}
