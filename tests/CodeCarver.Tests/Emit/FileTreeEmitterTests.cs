using CodeCarver.Core.Emit;
using CodeCarver.Core.Frontend;
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
    public void Emit_CopiesUnscannedLocalInclude_NotAGraphNode()
    {
        // A kept .c #includes a generated table with a non-source extension (.inc). It is not a graph
        // node (the carve never scanned it), so the include-closure can't keep it — but the emitted tree
        // won't compile without it. The emitter must copy it verbatim, and recurse into what it includes.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-emit-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(Path.Combine(srcDir, "vm"));
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "vm", "core.c"),
                "#include \"core.gen.inc\"\nint kept(void){return 0;}\n");
            File.WriteAllText(Path.Combine(srcDir, "vm", "core.gen.inc"),
                "/* generated */\n#include \"nested.def\"\n");
            File.WriteAllText(Path.Combine(srcDir, "vm", "nested.def"), "#define TABLE 1\n");
            File.WriteAllText(Path.Combine(srcDir, "drop.c"), "int dead(void){return 1;}\n");

            var b = new GraphBuilder();
            var kept = b.Func("kept", "vm/core.c");
            _ = b.Func("dead", "drop.c");
            var plan = ReachabilityEngine.Compute(b.Graph, new[] { new Root(kept, RootKind.ExplicitSymbol) });

            var result = FileTreeEmitter.Emit(plan, srcDir, outDir);

            Assert.True(File.Exists(Path.Combine(outDir, "vm", "core.c")));
            Assert.True(File.Exists(Path.Combine(outDir, "vm", "core.gen.inc")));   // included by core.c
            Assert.True(File.Exists(Path.Combine(outDir, "vm", "nested.def")));     // included by the .inc (recursed)
            Assert.False(File.Exists(Path.Combine(outDir, "drop.c")));              // dropped, not resurrected
            Assert.Contains("vm/core.gen.inc", result.Written);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void BuildSupport_CopiesLinkerScriptsAndStartupAsm_AndAuxGlobs()
    {
        // An embedded carve must also ship the files that make it LINK — the linker script and startup
        // assembly, which are not C translation units and so aren't graph files. Plus --aux for the rest.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-sup-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(Path.Combine(srcDir, "board"));
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "main.c"), "int main(void){return 0;}\n");
            File.WriteAllText(Path.Combine(srcDir, "flash.ld"), "MEMORY { FLASH : ORIGIN = 0, LENGTH = 64K }\n");
            File.WriteAllText(Path.Combine(srcDir, "startup.s"), ".global _start\n_start:\n");
            File.WriteAllText(Path.Combine(srcDir, "board", "vectors.S"), ".word reset\n");
            File.WriteAllText(Path.Combine(srcDir, "Makefile"), "all:\n\tgcc main.c\n");

            var res = BuildSupportEmitter.Copy(srcDir, outDir,
                alreadyEmittedRel: new[] { "main.c" },              // main.c already emitted by the tree carve
                excludeDirs: System.Array.Empty<string>(),
                auxGlobs: new[] { "Makefile" });

            Assert.True(File.Exists(Path.Combine(outDir, "flash.ld")));            // linker script
            Assert.True(File.Exists(Path.Combine(outDir, "startup.s")));           // startup assembly
            Assert.True(File.Exists(Path.Combine(outDir, "board", "vectors.S")));  // .S, nested layout preserved
            Assert.True(File.Exists(Path.Combine(outDir, "Makefile")));            // via --aux
            Assert.False(File.Exists(Path.Combine(outDir, "main.c")));             // already emitted — not re-copied
            Assert.Equal(4, res.Count);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void BuildSupport_RespectsExcludedDirs()
    {
        var work = Path.Combine(Path.GetTempPath(), "codecarver-sup-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(Path.Combine(srcDir, "stm32f7"));
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "flash.ld"), "x\n");
            File.WriteAllText(Path.Combine(srcDir, "stm32f7", "startup_f7.s"), "y\n"); // other-board variant

            var res = BuildSupportEmitter.Copy(srcDir, outDir,
                alreadyEmittedRel: System.Array.Empty<string>(),
                excludeDirs: new[] { "stm32f7" },
                auxGlobs: System.Array.Empty<string>());

            Assert.True(File.Exists(Path.Combine(outDir, "flash.ld")));
            Assert.False(File.Exists(Path.Combine(outDir, "stm32f7", "startup_f7.s"))); // excluded variant
            Assert.Equal(1, res.Count);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void EmitPruned_KeepsConstructor_AndWholeTemplatePrefix()
    {
        // Two C++ pruning-soundness regressions (found in simdjson):
        //  1) a CONSTRUCTOR (function whose name == a class/struct type) is invoked implicitly, never via a
        //     traced call, so it looks unreached — but removing it makes the class's implicit default ctor
        //     ill-formed. Constructors are never pruned.
        //  2) a templated method's `template<...>` line sits ABOVE the captured function_definition; pruning
        //     it must remove the template prefix too, or `template<int N>\n};` dangles.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-ctor-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            const string impl = """
                struct Thing {
                    Thing() {}
                    template<int N>
                    int scale() const { return N; }
                    int keep() const { return 1; }
                };
                int run() { Thing t; return t.keep(); }
                """;
            File.WriteAllText(Path.Combine(srcDir, "impl.cpp"), impl);

            using var fe = new CodeCarver.Frontend.CppFrontEnd();
            var graph = fe.BuildGraph(new[] { ("impl.cpp", impl) });
            var roots = new ExplicitRootProvider(symbols: new[] { "run" }).Discover(graph)
                .Concat(new ConstructorRootProvider().Discover(graph)).ToList();  // constructors are roots
            var plan = ReachabilityEngine.Compute(graph, roots);
            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            var carved = File.ReadAllText(Path.Combine(outDir, "impl.cpp"));
            Assert.Contains("Thing()", carved);                         // constructor kept (via ConstructorRootProvider)
            Assert.DoesNotContain("template<int N>", carved);           // template prefix removed with its method
            Assert.DoesNotContain("scale", carved);                     // the unreached templated method is gone
            Assert.Contains("keep", carved);                            // reached method kept
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void EmitPruned_DropInsideNamespaceIfdef_KeepsEnclosingBraceAndSiblings()
    {
        // Real pugixml bug (exposed once specifier-macro methods were captured): dropping a function that
        // sits just below `namespace X {` immediately followed by `#ifndef` made the emitter's "extend up
        // for a dual-signature #if/#else" heuristic mistake the NAMESPACE's open brace for a shared
        // signature brace (a `#` directive sits between them) and swallow the `{` AND a kept sibling above
        // the drop -> `namespace X` with no `{` -> the carved file won't compile. Extending up must only
        // happen when the orphaned line is a real signature (has parens), never a bare scope-opener.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-nsbrace-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            const string impl = """
                namespace ns
                {
                #ifndef NO_EXC
                	int keeper(int r)
                	{
                		return r + 1;
                	}

                	const char* dropme()
                	{
                		return "x";
                	}

                	int alsokeep()
                	{
                		return 2;
                	}
                #endif
                }
                """;
            File.WriteAllText(Path.Combine(srcDir, "impl.cpp"), impl);

            using var fe = new CodeCarver.Frontend.CppFrontEnd();
            var graph = fe.BuildGraph(new[] { ("impl.cpp", impl) });
            var roots = new ExplicitRootProvider(symbols: new[] { "keeper", "alsokeep" }).Discover(graph).ToList();
            var plan = ReachabilityEngine.Compute(graph, roots);
            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            var carved = File.ReadAllText(Path.Combine(outDir, "impl.cpp"));
            Assert.Contains("namespace ns", carved);
            Assert.Equal(                                                   // braces still balanced
                carved.Split('{').Length, carved.Split('}').Length);
            Assert.Contains("keeper", carved);                             // kept sibling above the drop survived
            Assert.Contains("alsokeep", carved);                           // kept sibling below survived
            Assert.DoesNotContain("dropme", carved);                       // the unreached function is gone
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
