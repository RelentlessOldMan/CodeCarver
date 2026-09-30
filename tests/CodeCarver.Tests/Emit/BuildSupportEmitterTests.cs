using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The --aux glob resolver decides which non-source build-support files get copied alongside the carved
/// tree. Getting it wrong either drops files the build needs or writes outside --out, so the glob semantics
/// (recursive '**', root-escape refusal) are pinned here directly against the public MatchGlob/GlobEscapesRoot.
/// </summary>
public sealed class BuildSupportEmitterTests
{
    private static string NewTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-aux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cfg", "boards"));
        File.WriteAllText(Path.Combine(root, "top.mk"), "x");
        File.WriteAllText(Path.Combine(root, "cfg", "a.cmd"), "x");
        File.WriteAllText(Path.Combine(root, "cfg", "boards", "b.cmd"), "x");
        return root;
    }

    [Fact]
    public void MatchGlob_TrailingGlobstar_MatchesEverythingUnderPrefixRecursively()
    {
        // 'cfg/**' must recurse: both cfg/a.cmd and cfg/boards/b.cmd match; the sibling top.mk (outside cfg/)
        // does not. This is the trailing/standalone '**' arm of the glob compiler.
        var root = NewTree();
        try
        {
            var hits = BuildSupportEmitter.MatchGlob(root, "cfg/**")
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(s => s).ToArray();
            Assert.Equal(new[] { "cfg/a.cmd", "cfg/boards/b.cmd" }, hits);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MatchGlob_SeparatorlessPattern_MatchesByBasenameAtAnyDepth()
    {
        // A pattern with no '/' matches by basename anywhere in the tree.
        var root = NewTree();
        try
        {
            var hits = BuildSupportEmitter.MatchGlob(root, "*.cmd")
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(s => s).ToArray();
            Assert.Equal(new[] { "cfg/a.cmd", "cfg/boards/b.cmd" }, hits);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("../escape/*.ld", true)]   // parent traversal -> would write outside --out
    [InlineData("a/../../b", true)]        // mid-path traversal
    [InlineData("cfg/**", false)]          // legitimate in-root recursive pattern
    [InlineData("*.cmd", false)]
    public void GlobEscapesRoot_RefusesTraversalAndAbsolutePaths(string glob, bool escapes)
    {
        Assert.Equal(escapes, BuildSupportEmitter.GlobEscapesRoot(glob));
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
