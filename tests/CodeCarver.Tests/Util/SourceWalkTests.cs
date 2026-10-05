using System.Diagnostics;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>
/// The shared source-tree walk must survive real Windows trees: directory junctions/symlinks must not be
/// recursed into (a junction to an ancestor loops forever; one to a sibling double-scans and double-emits),
/// yet every real file must still be found exactly once. Junctions (mklink /J) need no elevation, so this
/// runs on a normal dev/CI box; it self-skips if junction creation isn't permitted.
/// </summary>
public sealed class SourceWalkTests
{
    // Create a directory junction; returns false (test self-skips) if the OS refuses.
    private static bool TryJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    [Fact]
    public void SiblingJunction_NotFollowed_RealFilesFoundOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-walk-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real");
        var sub = Path.Combine(real, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(real, "a.c"), "x");
        File.WriteAllText(Path.Combine(sub, "b.c"), "y");
        try
        {
            if (!TryJunction(Path.Combine(root, "link"), real)) return; // skip: junctions not permitted here

            var found = SourceWalk.Files(root).Select(f => Rel(root, f)).OrderBy(s => s).ToList();

            // Only the two canonical files, once each — the junction 'link/…' was neither returned nor recursed.
            Assert.Equal(new[] { "real/a.c", "real/sub/b.c" }, found);
            Assert.DoesNotContain(found, f => f.StartsWith("link/", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AncestorJunctionCycle_Terminates()
    {
        // A junction pointing at an ANCESTOR is the classic infinite-recursion trap. The walk must terminate
        // and return a bounded, correct result rather than hang.
        var root = Path.Combine(Path.GetTempPath(), "cc-walk-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.c"), "x");
        try
        {
            if (!TryJunction(Path.Combine(real, "loop"), root)) return; // loop -> its own ancestor

            var found = SourceWalk.Files(root).Select(f => Rel(root, f)).ToList();

            Assert.Contains("real/a.c", found);
            Assert.True(found.Count < 50, $"walk did not terminate cleanly — {found.Count} entries (cycle followed?)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void DotFilesAndHiddenEntries_AreWalked_VcsMetadataIsNot()
    {
        // Review RB6: Linux reports every dot-file as Hidden; skipping Hidden lost .config (Kconfig) etc.
        var root = Path.Combine(Path.GetTempPath(), "cc-walk-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".settings"));
            Directory.CreateDirectory(Path.Combine(root, ".git", "objects"));
            File.WriteAllText(Path.Combine(root, ".config"), "CONFIG_X=y");
            File.WriteAllText(Path.Combine(root, ".settings", "board.mk"), "X=1");
            File.WriteAllText(Path.Combine(root, ".git", "objects", "ab"), "blob");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(Path.Combine(root, ".config"), FileAttributes.Hidden);
                new DirectoryInfo(Path.Combine(root, ".settings")).Attributes |= FileAttributes.Hidden;
            }
            var found = SourceWalk.Files(root).Select(f => Rel(root, f)).ToList();
            Assert.Contains(".config", found);
            Assert.Contains(".settings/board.mk", found);
            Assert.DoesNotContain(found, f => f.StartsWith(".git/", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
