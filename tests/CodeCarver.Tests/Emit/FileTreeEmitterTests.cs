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
    public void BuildSupport_AuxSeparatorGlob_NoCrash_ResolvesAndWarns()
    {
        // eval-#4 BUG 2: a --aux glob with a path separator ("gen/*.inc", "/*.inc") was passed straight to
        // Directory.EnumerateFiles, which accepts a FILENAME pattern only, so it threw and crashed the
        // process (exit -532462766). It must normalize dir/pattern globs, strip leading separators, never
        // throw, and warn (not silently copy nothing) when a glob matches no files.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-glob-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        Directory.CreateDirectory(Path.Combine(srcDir, "gen"));
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "gen", "tables.inc"), "1\n");
            File.WriteAllText(Path.Combine(srcDir, "top.inc"), "2\n");
            string[] none = System.Array.Empty<string>();

            // separator glob -> resolves the subdir file, no crash, no warning
            var o1 = Path.Combine(work, "o1");
            var r1 = BuildSupportEmitter.Copy(srcDir, o1, none, none, new[] { "gen/*.inc" });
            Assert.True(File.Exists(Path.Combine(o1, "gen", "tables.inc")));
            Assert.Empty(r1.Warnings);

            // leading-separator glob normalizes to a root pattern (recursive), no crash
            var o2 = Path.Combine(work, "o2");
            var r2 = BuildSupportEmitter.Copy(srcDir, o2, none, none, new[] { "/*.inc" });
            Assert.True(File.Exists(Path.Combine(o2, "top.inc")));

            // no-match glob -> warns instead of copying nothing silently
            var o3 = Path.Combine(work, "o3");
            var r3 = BuildSupportEmitter.Copy(srcDir, o3, none, none, new[] { "*.nomatch" });
            Assert.Contains(r3.Warnings, w => w.Contains("*.nomatch"));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void MatchGlob_StarStarAnywhere_AndBasenameRecursion()
    {
        // eval-#6: a naive Replace("**/","") turned a/**/b/*.inc into the literal a/b/*.inc and SILENTLY
        // dropped a/q/b/y.inc (a partial match read as success). MatchGlob now regex-matches the relative
        // path, so ** works mid-path; a separator-less pattern still matches by basename at any depth.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-mg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "a", "b"));
        Directory.CreateDirectory(Path.Combine(work, "a", "q", "b"));
        try
        {
            File.WriteAllText(Path.Combine(work, "top.inc"), "1");
            File.WriteAllText(Path.Combine(work, "a", "b", "x.inc"), "1");
            File.WriteAllText(Path.Combine(work, "a", "q", "b", "y.inc"), "1");
            string[] Names(string glob) => BuildSupportEmitter.MatchGlob(work, glob)
                .Select(f => Path.GetFileName(f)!).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { "x.inc", "y.inc" }, Names("a/**/b/*.inc"));            // mid-path ** (the regression)
            Assert.Equal(new[] { "top.inc", "x.inc", "y.inc" }, Names("*.inc"));        // basename, any depth
            Assert.Equal(new[] { "top.inc", "x.inc", "y.inc" }, Names("**/*.inc"));     // leading **/ = any depth
            Assert.Equal(new[] { "x.inc" }, Names("a/b/*.inc"));                        // anchored: one segment
            Assert.Empty(Names("nope/*.zzz"));                                          // no match, no throw
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
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
    public void EmitPruned_DroppedFunctionOverlappingKeptNestedDef_KeepsWholeFile()
    {
        // Real tinyxml2 xmltest.cpp bug (found by the corpus compile-diff sweep): a local class/lambda
        // inside a big function whose method is reached (by name / virtual dispatch) while the ENCLOSING
        // function is not reached. The enclosing function's span can't be cleanly removed (it contains a
        // kept node), so it's kept whole -- but it may CALL sibling functions reachability dropped, and
        // pruning those leaves the kept-whole function calling an undeclared symbol. When a dropped span
        // overlaps a kept one, we keep the WHOLE FILE (sound). Here big_fn (dropped) contains Local::shared
        // (kept via the 'shared' root) and calls helper (dropped) -> helper must survive.
        var work = Path.Combine(Path.GetTempPath(), "codecarver-overlap-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            const string impl = """
                int shared() { return 1; }
                int helper() { return 7; }
                int big_fn() {
                    struct Local { int shared() { return 5; } };
                    Local l;
                    return l.shared() + helper();
                }
                int run() { return shared(); }
                """;
            File.WriteAllText(Path.Combine(srcDir, "impl.cpp"), impl);

            using var fe = new CodeCarver.Frontend.CppFrontEnd();
            var graph = fe.BuildGraph(new[] { ("impl.cpp", impl) });
            var roots = new ExplicitRootProvider(symbols: new[] { "run" }).Discover(graph).ToList();
            var plan = ReachabilityEngine.Compute(graph, roots);
            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            var carved = File.ReadAllText(Path.Combine(outDir, "impl.cpp"));
            Assert.Contains("helper", carved);   // sibling that the kept-whole big_fn calls survived (no dangling)
            Assert.Contains("big_fn", carved);   // the overlap-kept enclosing function is present
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
