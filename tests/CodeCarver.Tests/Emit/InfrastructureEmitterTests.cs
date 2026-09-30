using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The keep-by-default pass is what makes --out a COMPLETE buildable project: it must pass through every
/// non-code file (Makefiles, linker scripts, data, configs) verbatim, remove ONLY the code the carve proved
/// dead, and never re-copy the code already emitted. These pin that contract.
/// </summary>
public sealed class InfrastructureEmitterTests
{
    private static string NewTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-infra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "board"));
        File.WriteAllText(Path.Combine(root, "keep.c"), "int kept(void){return 0;}\n");   // kept code (already emitted)
        File.WriteAllText(Path.Combine(root, "drop.c"), "int dead(void){return 1;}\n");   // dead code (dropped)
        File.WriteAllText(Path.Combine(root, "Makefile"), "all:\n\tgcc keep.c\n");         // infra
        File.WriteAllText(Path.Combine(root, "flash.ld"), "MEMORY{}\n");                   // infra
        File.WriteAllText(Path.Combine(root, "data.bin"), "\x00\x01\x02");               // infra (non-code blob)
        File.WriteAllText(Path.Combine(root, "sub", "config.json"), "{}\n");               // infra, nested
        File.WriteAllText(Path.Combine(root, "board", "variant.ld"), "MEMORY{}\n");        // infra under a dir
        return root;
    }

    private static void Cleanup(string root)
    { try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { } }

    [Fact]
    public void Copy_PassesThroughInfra_RemovesDeadCode_SkipsAlreadyEmitted()
    {
        var root = NewTree();
        var outDir = Path.Combine(Path.GetTempPath(), "cc-infra-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = InfrastructureEmitter.Copy(root, outDir,
                alreadyEmittedRel: new[] { "keep.c" },      // FileTreeEmitter already wrote this
                droppedCodeFilesRel: new[] { "drop.c" },    // carve proved it dead
                excludeDirs: Array.Empty<string>(),
                auxGlobs: Array.Empty<string>());

            // All non-code kept, verbatim, layout preserved:
            Assert.True(File.Exists(Path.Combine(outDir, "Makefile")));
            Assert.True(File.Exists(Path.Combine(outDir, "flash.ld")));
            Assert.True(File.Exists(Path.Combine(outDir, "data.bin")));
            Assert.True(File.Exists(Path.Combine(outDir, "sub", "config.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "board", "variant.ld")));
            // Code already emitted is NOT re-copied; dead code is NOT resurrected:
            Assert.False(File.Exists(Path.Combine(outDir, "keep.c")));
            Assert.False(File.Exists(Path.Combine(outDir, "drop.c")));
            Assert.Equal(5, res.Count);
            Assert.Contains("sub/config.json", res.Files);   // rel paths are '/'-separated + sorted
        }
        finally { Cleanup(root); Cleanup(outDir); }
    }

    [Fact]
    public void Copy_RespectsExclude_UnlessAuxForcesItBack()
    {
        var root = NewTree();
        var outA = Path.Combine(Path.GetTempPath(), "cc-infra-exA-" + Guid.NewGuid().ToString("N"));
        var outB = Path.Combine(Path.GetTempPath(), "cc-infra-exB-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Exclude board/: its variant.ld must not be copied.
            var res = InfrastructureEmitter.Copy(root, outA,
                Array.Empty<string>(), Array.Empty<string>(),
                excludeDirs: new[] { "board" }, auxGlobs: Array.Empty<string>());
            Assert.False(File.Exists(Path.Combine(outA, "board", "variant.ld")));
            Assert.True(File.Exists(Path.Combine(outA, "flash.ld")));

            // Same exclude, but --aux force-includes the excluded linker script.
            var res2 = InfrastructureEmitter.Copy(root, outB,
                Array.Empty<string>(), Array.Empty<string>(),
                excludeDirs: new[] { "board" }, auxGlobs: new[] { "board/variant.ld" });
            Assert.True(File.Exists(Path.Combine(outB, "board", "variant.ld")));   // forced back in
        }
        finally { Cleanup(root); Cleanup(outA); Cleanup(outB); }
    }

    [Fact]
    public void Copy_AuxGlob_NoMatch_Warns_And_RootEscape_Refused()
    {
        var root = NewTree();
        var outDir = Path.Combine(Path.GetTempPath(), "cc-infra-warn-" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = InfrastructureEmitter.Copy(root, outDir,
                Array.Empty<string>(), Array.Empty<string>(),
                excludeDirs: Array.Empty<string>(),
                auxGlobs: new[] { "*.nomatch", "../escape/*.ld" });

            Assert.Contains(res.Warnings, w => w.Contains("*.nomatch"));          // no-match glob warns
            Assert.Contains(res.Warnings, w => w.Contains("refused"));            // root-escape glob refused
            // Nothing outside --out was written; the normal infra still copied.
            Assert.True(File.Exists(Path.Combine(outDir, "flash.ld")));
        }
        finally { Cleanup(root); Cleanup(outDir); }
    }

    [Fact]
    public void Copy_OutEqualsSource_DoesNotCopyOntoItself()
    {
        // Belt-and-braces: if a caller points --out at the source tree, every dst resolves to its own src;
        // the SameFile guard must skip each so no file is copied onto itself (and nothing is corrupted).
        var root = NewTree();
        try
        {
            var before = File.ReadAllText(Path.Combine(root, "Makefile"));
            var res = InfrastructureEmitter.Copy(root, root,   // out == source
                Array.Empty<string>(), Array.Empty<string>(),
                excludeDirs: Array.Empty<string>(), auxGlobs: Array.Empty<string>());

            Assert.Equal(0, res.Count);   // everything skipped (dst == src)
            Assert.Equal(before, File.ReadAllText(Path.Combine(root, "Makefile")));  // untouched
        }
        finally { Cleanup(root); }
    }
}
