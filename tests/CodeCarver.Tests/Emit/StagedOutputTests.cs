using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The crash-recovery kernel for a batch tool (Spec_CrashRecovery §11/§41/§61): emitting the carved tree
/// must be atomic and must never leave a prior good --out half-overwritten or polluted with stale files.
/// These pin the staging/promote contract the CLI relies on.
/// </summary>
public sealed class StagedOutputTests
{
    private static string NewWork()
    {
        var w = Path.Combine(Path.GetTempPath(), "cc-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(w);
        return w;
    }

    [Fact]
    public void Begin_CreatesEmptyStaging_BesideOut_NotTheOutDir()
    {
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using var staged = StagedOutput.Begin(outDir);

            Assert.True(Directory.Exists(staged.Dir));
            Assert.Empty(Directory.EnumerateFileSystemEntries(staged.Dir));
            Assert.NotEqual(Path.GetFullPath(outDir), Path.GetFullPath(staged.Dir));
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(outDir)),
                         Path.GetDirectoryName(Path.GetFullPath(staged.Dir))); // same parent -> same volume
            Assert.False(Directory.Exists(outDir)); // out itself untouched until promote
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_FreshOut_MovesStagedTreeIntoPlace()
    {
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using (var staged = StagedOutput.Begin(outDir))
            {
                File.WriteAllText(Path.Combine(staged.Dir, "a.c"), "1");
                staged.Promote();
            }
            Assert.True(File.Exists(Path.Combine(outDir, "a.c")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_ReplacesPriorOutput_DropsStaleFiles()
    {
        // THE soundness fix: a prior carve left a.c + b.c in --out; a tighter re-carve emits only a.c.
        // After promote, b.c (which the new carve dropped) must be GONE, not lingering as a stale file
        // that would get compiled/linked.
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "a.c"), "old-a");
            File.WriteAllText(Path.Combine(outDir, "b.c"), "old-b");   // will be dropped by the new carve

            using (var staged = StagedOutput.Begin(outDir))
            {
                File.WriteAllText(Path.Combine(staged.Dir, "a.c"), "new-a"); // only a.c this time
                staged.Promote();
            }

            Assert.Equal("new-a", File.ReadAllText(Path.Combine(outDir, "a.c")));
            Assert.False(File.Exists(Path.Combine(outDir, "b.c")));   // stale file gone
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Dispose_WithoutPromote_LeavesPriorOutputUntouched_AndRemovesStaging()
    {
        // Simulates a run killed / failing mid-emit before promote: the prior good --out must survive
        // byte-for-byte, and the incomplete staging dir must not linger.
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "keep.c"), "known-good");

            string stagingDir;
            using (var staged = StagedOutput.Begin(outDir))
            {
                stagingDir = staged.Dir;
                File.WriteAllText(Path.Combine(staged.Dir, "partial.c"), "half-written");
                // no Promote() -> Dispose aborts
            }

            Assert.Equal("known-good", File.ReadAllText(Path.Combine(outDir, "keep.c"))); // prior output intact
            Assert.False(File.Exists(Path.Combine(outDir, "partial.c")));                 // partial NOT merged in
            Assert.False(Directory.Exists(stagingDir));                                   // staging cleaned up
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_LeavesNoBackupOrStagingSiblings()
    {
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "a.c"), "old");
            using (var staged = StagedOutput.Begin(outDir))
            {
                File.WriteAllText(Path.Combine(staged.Dir, "a.c"), "new");
                staged.Promote();
            }
            var leftovers = Directory.EnumerateDirectories(work)
                .Select(Path.GetFileName)
                .Where(n => n!.StartsWith(".ccstaging", StringComparison.Ordinal) || n.Contains(".ccold"))
                .ToList();
            Assert.Empty(leftovers);
        }
        finally { Cleanup(work); }
    }

    private static void Cleanup(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
    }
}
