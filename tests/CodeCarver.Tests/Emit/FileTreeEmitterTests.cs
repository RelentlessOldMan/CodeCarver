using CodeCarver.Core.Emit;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests.Emit;

public class FileTreeEmitterTests
{
    [Fact]
    public void Emit_WritesKeptFiles_OmitsDropped()
    {
        // A source tree on disk: keep.c is reachable, drop.c is not.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-emit-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "keep.c"), "int kept(void){return 0;}\n");
            File.WriteAllText(Path.Combine(srcDir, "drop.c"), "int dead(void){return 1;}\n");

            // Build a plan that keeps keep.c and drops drop.c.
            var b = new GraphBuilder();
            var kept = b.Func("kept", "keep.c");
            _ = b.Func("dead", "drop.c");
            var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(kept, RootKind.ExplicitSymbol) });

            var result = FileTreeEmitter.Emit(plan, srcDir, outDir);

            Assert.True(File.Exists(Path.Combine(outDir, "keep.c")));
            Assert.False(File.Exists(Path.Combine(outDir, "drop.c")));
            Assert.Equal(1, result.FilesWritten);
            Assert.Contains("keep.c", result.Written);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Emit_PreservesNestedLayout()
    {
        var work = Path.Combine(Path.GetTempPath(), "codecarver-emit-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "sub", "keep.c"), "int kept(void){return 0;}\n");

            var b = new GraphBuilder();
            var kept = b.Func("kept", "sub/keep.c");
            var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(kept, RootKind.ExplicitSymbol) });

            FileTreeEmitter.Emit(plan, srcDir, outDir);

            Assert.True(File.Exists(Path.Combine(outDir, "sub", "keep.c")));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}
