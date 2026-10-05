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

    private static void MarkPriorOutput(string outDir) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outDir))!, StagedOutput.MarkerName), "x");

    [Fact]
    public void Begin_RefusesForeignNonEmptyDirectory()
    {
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "IMPORTANT.txt"), "keep");
            Assert.Throws<PromoteFailedException>(() => StagedOutput.Begin(outDir));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outDir, "IMPORTANT.txt")));
            Assert.False(File.Exists(Path.Combine(work, StagedOutput.MarkerName)));
        }
        finally { Cleanup(work); }
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
            // Staging starts EMPTY — the carved tree carries no marker, so it promotes to a clean project. The
            // marker lives in the output AREA (the parent) instead.
            Assert.Empty(Directory.EnumerateFileSystemEntries(staged.Dir));
            Assert.True(File.Exists(Path.Combine(work, StagedOutput.MarkerName)));
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
            MarkPriorOutput(outDir); // a prior CodeCarver output (review O1: unmarked dirs are refused)
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
            MarkPriorOutput(outDir); // a prior CodeCarver output (review O1: unmarked dirs are refused)
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
            MarkPriorOutput(outDir); // a prior CodeCarver output (review O1: unmarked dirs are refused)
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

    [Fact]
    public void OutputArea_CarriesMarker_AndIsRecognized_WhileCarvedTreeStaysClean()
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
            // The marker marks the AREA (the parent), so a re-carve recognizes it as safe to replace…
            Assert.True(File.Exists(Path.Combine(work, StagedOutput.MarkerName)));
            Assert.True(StagedOutput.IsCodeCarverOutput(work));
            // …but the carved tree itself stays clean — no stray dotfile in the buildable output.
            Assert.False(File.Exists(Path.Combine(outDir, StagedOutput.MarkerName)));
            Assert.Equal(new[] { "a.c" },
                Directory.EnumerateFiles(outDir).Select(Path.GetFileName).ToArray());
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void IsCodeCarverOutput_FalseForArbitraryDir()
    {
        var work = NewWork();
        try
        {
            File.WriteAllText(Path.Combine(work, "notes.txt"), "mine");
            Assert.False(StagedOutput.IsCodeCarverOutput(work));   // no marker -> not ours -> must not be wiped
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_WhenRenameBlockedByOpenHandle_FallsBackToCopy()
    {
        // Reproduces the Windows AV/indexer failure: a handle held open on a just-written staged file blocks
        // the directory rename. Promote must retry and then fall back to copying the tree in, so the carve
        // still lands instead of exiting with a spurious failure.
        if (!OperatingSystem.IsWindows()) return; // the sharing-violation-on-rename behavior is Windows-specific
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using var staged = StagedOutput.Begin(outDir);
            File.WriteAllText(Path.Combine(staged.Dir, "a.c"), "hello");
            var locked = Path.Combine(staged.Dir, "locked.c");
            File.WriteAllText(locked, "held");
            // Hold a handle that allows readers (so the copy fallback can read it) but blocks the rename.
            using (var _ = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                staged.Promote(); // rename fails -> retries -> copy fallback
            }
            Assert.True(File.Exists(Path.Combine(outDir, "a.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "locked.c")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_PriorOutHasOpenFile_StillLands_ViaInPlaceFallback()
    {
        // eval-#9 MEDIUM: a process holding a file open in the prior --out (or a shell cwd'd there) blocks the
        // directory move-aside. Promote must fall back to replacing contents IN PLACE so the carve still
        // lands, instead of failing every re-carve. (Windows lock semantics; skip elsewhere.)
        if (!OperatingSystem.IsWindows()) return;
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using (var s1 = StagedOutput.Begin(outDir))
            {
                File.WriteAllText(Path.Combine(s1.Dir, "keep.c"), "old");
                File.WriteAllText(Path.Combine(s1.Dir, "stale.c"), "x"); // dropped by the next carve
                s1.Promote();
            }
            using var s2 = StagedOutput.Begin(outDir);
            File.WriteAllText(Path.Combine(s2.Dir, "keep.c"), "new");    // only keep.c this time
            // Hold keep.c open with write-sharing — the common "a tool/shell has something open in out" case.
            using (var _ = new FileStream(Path.Combine(outDir, "keep.c"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var ex = Record.Exception(() => s2.Promote());
                Assert.Null(ex);                                        // must not fail the whole carve
            }
            Assert.Equal("new", File.ReadAllText(Path.Combine(outDir, "keep.c"))); // landed
            Assert.False(File.Exists(Path.Combine(outDir, "stale.c")));            // stale dropped
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_ReadOnlyPriorOutput_NoBackupLeak_AndOutputWritable()
    {
        // eval-#10 HIGH (Perforce): read-only files in the prior --out blocked backup deletion, leaking a
        // full .ccold copy per re-carve. Now ReadOnly is cleared before deleting, and the carved output is
        // emitted writable. (Read-only-blocks-delete is Windows behavior; skip elsewhere.)
        if (!OperatingSystem.IsWindows()) return;
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using (var s1 = StagedOutput.Begin(outDir)) { File.WriteAllText(Path.Combine(s1.Dir, "a.c"), "x"); s1.Promote(); }
            File.SetAttributes(Path.Combine(outDir, "a.c"), FileAttributes.ReadOnly); // prior out read-only (Perforce-like)

            using (var s2 = StagedOutput.Begin(outDir))
            {
                var f = Path.Combine(s2.Dir, "a.c");
                File.WriteAllText(f, "y");
                File.SetAttributes(f, FileAttributes.ReadOnly);   // staged file also read-only (copied from a RO source)
                s2.Promote();
            }

            var leftover = Directory.EnumerateDirectories(work).Select(Path.GetFileName)
                .Where(n => n!.Contains(".ccold") || n.StartsWith(".ccstaging", StringComparison.Ordinal)).ToList();
            Assert.Empty(leftover);                                                    // no leaked backup/staging
            Assert.Equal("y", File.ReadAllText(Path.Combine(outDir, "a.c")));          // updated
            Assert.True((File.GetAttributes(Path.Combine(outDir, "a.c")) & FileAttributes.ReadOnly) == 0); // writable
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_PriorOutHasExclusiveLock_PreflightBails_LeavesPriorUnchanged()
    {
        // eval-#10 MEDIUM: a no-share lock must NOT leave a torn tree. The in-place pre-flight opens every
        // destination for write BEFORE mutating; a locked file bails with the prior output byte-for-byte
        // unchanged and a Torn=false failure ("unchanged"), not a half-replaced tree.
        if (!OperatingSystem.IsWindows()) return;
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using (var s1 = StagedOutput.Begin(outDir))
            {
                File.WriteAllText(Path.Combine(s1.Dir, "keep.c"), "old");
                File.WriteAllText(Path.Combine(s1.Dir, "a.c"), "aold");
                s1.Promote();
            }
            using var s2 = StagedOutput.Begin(outDir);
            File.WriteAllText(Path.Combine(s2.Dir, "keep.c"), "new");
            File.WriteAllText(Path.Combine(s2.Dir, "a.c"), "anew");
            // Exclusive (no-share) lock on keep.c: blocks the dir move-aside AND the in-place overwrite.
            using (var _ = new FileStream(Path.Combine(outDir, "keep.c"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<PromoteFailedException>(() => s2.Promote());
                Assert.False(ex.Torn);                                    // reported as "unchanged", not torn
            }
            // Pre-flight bailed before writing anything -> prior output intact.
            Assert.Equal("old", File.ReadAllText(Path.Combine(outDir, "keep.c")));
            Assert.Equal("aold", File.ReadAllText(Path.Combine(outDir, "a.c")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Dispose_WithoutPromote_RemovesStaging()
    {
        // A handled failure / early return (or a Ctrl-C handler calling Dispose) must leave NO staging orphan.
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            string stageDir;
            using (var staged = StagedOutput.Begin(outDir))
            {
                stageDir = staged.Dir;
                File.WriteAllText(Path.Combine(staged.Dir, "a.c"), "1");
                // no Promote() -> Dispose must clean up
            }
            Assert.False(Directory.Exists(stageDir));
            Assert.False(Directory.Exists(outDir)); // never promoted
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Begin_ReapsOrphanedStagingAndBackup_FromHardKilledPriorRun()
    {
        // Ctrl-C / power loss skips Dispose, orphaning a `.ccstaging-out-*` (and, mid-promote, an `out.ccold-*`)
        // sibling. The NEXT run's Begin must reap them so they don't accumulate a full tree copy each. Only
        // OUR name-prefixed siblings for THIS --out are touched; unrelated dirs are left alone.
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            var orphanStaging = Path.Combine(work, ".ccstaging-out-deadbeef");
            var orphanBackup = Path.Combine(work, "out.ccold-deadbeef");
            var unrelated = Path.Combine(work, ".ccstaging-other-cafef00d"); // different --out name
            var innocent = Path.Combine(work, "keep-me");
            foreach (var d in new[] { orphanStaging, orphanBackup, unrelated, innocent })
            {
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "leftover.c"), "x");
            }

            using var staged = StagedOutput.Begin(outDir);

            Assert.False(Directory.Exists(orphanStaging)); // reaped (our staging for this out)
            Assert.False(Directory.Exists(orphanBackup));  // reaped (our backup for this out)
            Assert.True(Directory.Exists(unrelated));      // different output name -> untouched
            Assert.True(Directory.Exists(innocent));       // arbitrary dir -> untouched
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_InPlaceFallback_UndeletableStaleFile_SurfacesTornError()
    {
        // eval-#10: when the in-place fallback runs and a file the new carve DROPPED cannot be removed (held
        // open without delete-share), the tree is left STALE/unsound. That must be surfaced as a torn failure
        // — NOT a silent exit 0 that hands back an output still carrying the dropped file.
        if (!OperatingSystem.IsWindows()) return; // relies on Windows open-handle-blocks-delete/rename semantics
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using (var s1 = StagedOutput.Begin(outDir))
            {
                File.WriteAllText(Path.Combine(s1.Dir, "keep.c"), "old");
                File.WriteAllText(Path.Combine(s1.Dir, "stale.c"), "gone-next-carve"); // dropped by the re-carve
                s1.Promote();
            }

            using var s2 = StagedOutput.Begin(outDir);
            File.WriteAllText(Path.Combine(s2.Dir, "keep.c"), "new");   // only keep.c this time
            // keep.c held write-shared -> blocks the dir move-aside so the in-place fallback runs, but still
            // lets the in-place overwrite of keep.c succeed. stale.c held WITHOUT delete-share -> its removal
            // fails, so it lingers as a stale file the carve meant to drop.
            using (var _hk = new FileStream(Path.Combine(outDir, "keep.c"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var _hs = new FileStream(Path.Combine(outDir, "stale.c"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = Assert.Throws<PromoteFailedException>(() => s2.Promote());
                Assert.True(ex.Torn);                                   // "possibly stale/unsound", not "unchanged"
                Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("new", File.ReadAllText(Path.Combine(outDir, "keep.c"))); // the update did land
            Assert.True(File.Exists(Path.Combine(outDir, "stale.c")));             // dropped file could NOT be removed
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Promote_FreshOut_RenameBlocked_CopyFallbackPreservesNestedSubdirs()
    {
        // When the staging->final rename is blocked (AV/indexer holding a just-written file) the copy fallback
        // must reproduce the FULL tree, including nested subdirectories — a carved source tree is not flat.
        if (!OperatingSystem.IsWindows()) return; // sharing-violation-on-rename is Windows-specific
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using var staged = StagedOutput.Begin(outDir);
            var subDir = Path.Combine(staged.Dir, "drivers", "uart");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "uart.c"), "nested");
            var locked = Path.Combine(staged.Dir, "top.c");
            File.WriteAllText(locked, "root");
            using (var _ = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                staged.Promote(); // rename blocked -> copy fallback (CopyTree recreates subdirs)
            }
            Assert.True(File.Exists(Path.Combine(outDir, "top.c")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(outDir, "drivers", "uart", "uart.c")));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void IsCodeCarverOutput_MalformedPath_ReturnsFalse_DoesNotThrow()
    {
        // The "is this dir safe to atomically replace?" probe must be crash-proof: a malformed path is simply
        // "not one of ours" (false), never an exception that aborts the run before it can refuse to wipe.
        var bad = "bad\0path";   // embedded NUL -> Path.Combine throws internally -> caught -> false
        var ex = Record.Exception(() => Assert.False(StagedOutput.IsCodeCarverOutput(bad)));
        Assert.Null(ex);
    }

    [Fact]
    public void Begin_DoesNotReapTheLiveStagingOfAConcurrentRun_RB14()
    {
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            using var first = StagedOutput.Begin(outDir);
            File.WriteAllText(Path.Combine(first.Dir, "a.c"), "int a;");
            var second = StagedOutput.Begin(outDir);   // a second run into the same output
            Assert.True(File.Exists(Path.Combine(first.Dir, "a.c")));
            first.Promote();
            Assert.True(File.Exists(Path.Combine(outDir, "a.c")));
            Assert.Single(Directory.GetFiles(work, "*.lock"));   // only the still-live second stage's
            second.Dispose();
            Assert.Empty(Directory.GetFiles(work, "*.lock"));
        }
        finally { Cleanup(work); }
    }

    [Fact]
    public void Begin_ReapsAnOrphanWhoseOwnerIsGone_RB14()
    {
        var work = NewWork();
        try
        {
            var outDir = Path.Combine(work, "out");
            var orphan = Path.Combine(work, ".ccstaging-out-0123abcd");
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(work, ".ccstaging-out-0123abcd.lock"), "");   // released lock left by a killed run
            using var s = StagedOutput.Begin(outDir);
            Assert.False(Directory.Exists(orphan));
            Assert.False(File.Exists(Path.Combine(work, ".ccstaging-out-0123abcd.lock")));
        }
        finally { Cleanup(work); }
    }

    private static void Cleanup(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
    }
}
