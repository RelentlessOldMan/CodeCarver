using System.Diagnostics;
using CodeCarver.Core.Emit;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.BuildVerify;

/// <summary>
/// The must-build guarantee, tested for real: carve + prune a fixture, then COMPILE the output with a
/// pinned toolchain and assert it builds. Skips (does not fail) when the toolchain isn't fetched, so
/// the pure-engine suite still runs everywhere; the pinned GCC lives in <c>.toolchains/</c> (see
/// docs/TESTING.md) and makes this the strongest regression we have.
/// </summary>
public class BuildVerifyTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public BuildVerifyTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string AppC = """
        #include "util.h"

        int used(void) {
            return helper();
        }

        int unused(void) {
            return 999;
        }

        int main(void) {
            return used();
        }
        """;

    private const string UtilH = """
        #ifndef UTIL_H
        #define UTIL_H
        int helper(void);
        int used(void);
        #endif
        """;

    private const string UtilC = """
        #include "util.h"

        int helper(void) {
            return 1;
        }

        int orphan(void) {
            return 7;
        }
        """;

    [Fact]
    public void PrunedCarve_StillCompilesAndLinks()
    {
        var gcc = FindGcc();
        if (gcc is null) return; // toolchain not fetched — skip (pure-engine suite still covers logic)

        var work = Path.Combine(Path.GetTempPath(), "codecarver-bv-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "app.c"), AppC);
            File.WriteAllText(Path.Combine(srcDir, "util.h"), UtilH);
            File.WriteAllText(Path.Combine(srcDir, "util.c"), UtilC);

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[]
            {
                ("app.c", AppC), ("util.h", UtilH), ("util.c", UtilC),
            });
            var main = graph.Nodes.First(n => n.Name == "main").Id;
            var plan = ReachabilityEngine.Compute(graph, new[] { new Root(main, RootKind.EntryPoint) });

            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            // The pruning actually happened...
            var prunedApp = File.ReadAllText(Path.Combine(outDir, "app.c"));
            Assert.DoesNotContain("unused", prunedApp);
            Assert.DoesNotContain("orphan", File.ReadAllText(Path.Combine(outDir, "util.c")));

            // ...and the result still builds and links.
            var (code, output) = Run(gcc, new[] { "app.c", "util.c", "-I.", "-o", "out.exe" }, outDir);
            Assert.True(code == 0, $"pruned carve failed to build:\n{output}");
            Assert.True(File.Exists(Path.Combine(outDir, "out.exe")));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private const string DispatchC = """
        typedef void (*fn_t)(void);

        static int g_count;
        static void handler_a(void) { g_count += 1; }
        static void handler_b(void) { g_count += 2; }
        static void never_used(void) { g_count += 999; }

        static fn_t table[] = { handler_a, handler_b };

        void run(void) {
            for (int i = 0; i < 2; i++) table[i]();
        }
        """;

    [Fact]
    public void PrunedCarve_KeepsFileScopeTableHandlers_AndBuilds()
    {
        // Regression for the embedded vector/dispatch-table case: handlers referenced only from a
        // file-scope table must not be pruned, or the table initializer references a missing symbol.
        var gcc = FindGcc();
        if (gcc is null) return;

        var work = Path.Combine(Path.GetTempPath(), "codecarver-disp-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "dispatch.c"), DispatchC);

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[] { ("dispatch.c", DispatchC) });
            var run = graph.Nodes.First(n => n.Name == "run").Id;
            var plan = ReachabilityEngine.Compute(graph, new[] { new Root(run, RootKind.ExplicitSymbol) });

            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            var pruned = File.ReadAllText(Path.Combine(outDir, "dispatch.c"));
            Assert.DoesNotContain("never_used", pruned);   // genuinely dead → pruned
            Assert.Contains("handler_a", pruned);          // kept via the table

            File.WriteAllText(Path.Combine(outDir, "_verify.c"),
                "void run(void); int main(void){ run(); return 0; }\n");
            var (code, output) = Run(gcc, new[] { "dispatch.c", "_verify.c", "-o", "v.exe" }, outDir);
            Assert.True(code == 0, $"pruned dispatch table failed to build:\n{output}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void PrunedCarve_OfRealRepo_cJSON_StillCompiles() =>
        AssertRealRepoPrunedCompiles(
            repoName: "cJSON",
            files: new[] { "cJSON.c", "cJSON.h" },
            roots: new[] { "cJSON_Parse", "cJSON_Delete" },
            driver: "#include \"cJSON.h\"\nint main(void){cJSON*j=cJSON_Parse(\"{}\");cJSON_Delete(j);return 0;}\n",
            gccExtra: new[] { "-lm" });

    [Fact]
    public void PrunedCarve_OfRealRepo_printf_StillCompiles() =>
        AssertRealRepoPrunedCompiles(
            repoName: "printf",
            files: new[] { "printf.c", "printf.h" },
            roots: new[] { "snprintf_", "vsnprintf_" },
            driver: "#include \"printf.h\"\nint main(void){char b[64]; snprintf_(b,sizeof b,\"%d\",7); return 0;}\n",
            gccExtra: Array.Empty<string>());

    /// <summary>Carve+prune a real corpus repo to the given roots and assert the output compiles+links.
    /// Skips (returns) unless both the pinned toolchain and the fetched corpus repo are present.</summary>
    private void AssertRealRepoPrunedCompiles(string repoName, string[] files, string[] roots,
                                              string driver, string[] gccExtra)
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        var repo = FindUp(Path.Combine(".corpus", repoName));
        if (repo is null) return;
        if (files.Any(f => !File.Exists(Path.Combine(repo, f)))) return;

        var work = Path.Combine(Path.GetTempPath(), $"codecarver-{repoName}-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            var inputs = new List<(string, string)>();
            foreach (var f in files)
            {
                var text = File.ReadAllText(Path.Combine(repo, f));
                File.WriteAllText(Path.Combine(srcDir, f), text);
                inputs.Add((f, text));
            }

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(inputs);
            var rootSet = new ExplicitRootProvider(symbols: roots).Discover(graph).ToList();
            var plan = ReachabilityEngine.Compute(graph, rootSet);
            FileTreeEmitter.EmitPruned(plan, graph, srcDir, outDir);

            File.WriteAllText(Path.Combine(outDir, "_verify.c"), driver);

            var gccArgs = files.Where(f => f.EndsWith(".c", StringComparison.OrdinalIgnoreCase))
                .Append("_verify.c").Append("-I.").Concat(gccExtra).Append("-o").Append("v.exe").ToArray();
            var (code, output) = Run(gcc, gccArgs, outDir);
            Assert.True(code == 0, $"pruned {repoName} failed to build:\n{output}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    // Whole-repo intra-file pruning soundness: carve+prune a real repo's public API, then compile EVERY
    // kept .c. This is the harness that has caught most real bugs (parenthesized names, macro-hidden
    // calls, uncaptured macro-prefixed functions).

    [Fact]
    public void PrunedCarve_Lua_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("lua", "lua.h",
            new[] { "luaL_newstate", "luaL_openlibs", "lua_close", "luaL_loadstring", "lua_pcallk" },
            exclude: new[] { "onelua.c" }); // amalgamation re-#includes .c files

    [Fact]
    public void PrunedCarve_Zlib_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("zlib", "zlib.h",
            new[] { "compress2", "uncompress" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Sqlite_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("sqlite", "sqlite3.h",
            new[] { "sqlite3_open", "sqlite3_exec", "sqlite3_close", "sqlite3_prepare_v2",
                    "sqlite3_step", "sqlite3_finalize", "sqlite3_column_text" },
            exclude: Array.Empty<string>()); // huge single-file amalgamation stress test

    [Fact]
    public void PrunedCarve_Inih_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("inih", "ini.h",
            new[] { "ini_parse", "ini_parse_string" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Sds_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("sds", "sds.h",
            new[] { "sdsnew", "sdscat", "sdsfree", "sdscatprintf" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Tomlc99_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("tomlc99", "toml.h",
            new[] { "toml_parse", "toml_free" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Parson_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("parson", "parson.h",
            new[] { "json_parse_string", "json_value_free", "json_serialize_to_string" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Mpc_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("mpc", "mpc.h",
            new[] { "mpc_parse", "mpc_new", "mpc_delete" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Monocypher_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile(Path.Combine("Monocypher", "src"), "monocypher.h",
            new[] { "crypto_blake2b", "crypto_x25519", "crypto_wipe" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Tinyexpr_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("tinyexpr", "tinyexpr.h",
            new[] { "te_interp", "te_compile", "te_eval", "te_free" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_TinyRegex_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("tiny-regex-c", "re.h", // char-literal-heavy state machine ('{','\\')
            new[] { "re_compile", "re_match", "re_matchp" }, exclude: Array.Empty<string>());

    [Fact]
    public void PrunedCarve_Qrcodegen_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile(Path.Combine("qrcodegen", "c"), "qrcodegen.h", // big data tables
            new[] { "qrcodegen_encodeText", "qrcodegen_encodeBinary", "qrcodegen_getModule" },
            exclude: new[] { "qrcodegen-demo.c", "qrcodegen-test.c" });

    [Fact]
    public void PrunedCarve_Heatshrink_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("heatshrink", "heatshrink_common.h", // embedded state machine
            new[] { "heatshrink_encoder_sink", "heatshrink_encoder_poll", "heatshrink_encoder_finish",
                    "heatshrink_decoder_sink", "heatshrink_decoder_poll", "heatshrink_decoder_finish" },
            exclude: new[] { "test_heatshrink_dynamic.c", "test_heatshrink_dynamic_theft.c",
                             "test_heatshrink_static.c", "heatshrink.c" });

    [Fact]
    public void PrunedCarve_Rax_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile("rax", "rax.h", // goto-heavy radix tree
            new[] { "raxNew", "raxInsert", "raxRemove", "raxFind", "raxFree" },
            exclude: new[] { "rax-test.c", "rax-oom-test.c" });

    [Fact]
    public void PrunedCarve_LogC_AllKeptFilesCompile() =>
        AssertRepoPrunedFilesCompile(Path.Combine("log.c", "src"), "log.h", // file-scope callback dispatch table
            new[] { "log_log", "log_set_level", "log_add_callback" }, exclude: Array.Empty<string>());

    private void AssertRepoPrunedFilesCompile(string repoName, string sentinel, string[] roots, string[] exclude)
    {
        var gcc = FindGcc();
        if (gcc is null) return;
        var repo = FindUp(Path.Combine(".corpus", repoName));
        if (repo is null || !File.Exists(Path.Combine(repo, sentinel))) return;

        var work = Path.Combine(Path.GetTempPath(), $"codecarver-{repoName}-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(work);
        try
        {
            var inputs = Directory.EnumerateFiles(repo, "*.*")
                .Where(p => p.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Path.GetFileName(p), File.ReadAllText(p)))
                .ToList();

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(inputs);
            var plan = ReachabilityEngine.Compute(graph,
                new ExplicitRootProvider(symbols: roots).Discover(graph).ToList());
            FileTreeEmitter.EmitPruned(plan, graph, repo, outDir);

            var failures = new List<string>();
            foreach (var c in Directory.EnumerateFiles(outDir, "*.c"))
            {
                if (exclude.Contains(Path.GetFileName(c))) continue;
                var (code, output) = Run(gcc, new[] { "-c", "-I.", Path.GetFileName(c), "-o", "o.o" }, outDir);
                if (code != 0) failures.Add($"{Path.GetFileName(c)}: {output.Split('\n').FirstOrDefault()}");
            }
            Assert.True(failures.Count == 0, $"pruned {repoName} files failed to compile:\n" + string.Join("\n", failures));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    // The headline metric is IMAGE size (what's loaded onto the target), not source bytes. These measure
    // the real compiled footprint — text+data of the -Os object code, the parts that occupy flash — for
    // the full build vs the carved build, and assert the carve never grows it. Numbers are surfaced via
    // test output. Chosen repos have a genuine intra-file win (a few API entry points out of many funcs).
    [Fact]
    public void CompiledImageSize_Shrinks_cJSON() =>
        AssertCarveShrinksCompiledSize("cJSON", "cJSON.h",
            new[] { "cJSON_Parse", "cJSON_Print", "cJSON_Delete" }, exclude: new[] { "test.c" });

    [Fact]
    public void CompiledImageSize_Shrinks_Tinyexpr() =>
        AssertCarveShrinksCompiledSize("tinyexpr", "tinyexpr.h",
            new[] { "te_interp" }, exclude: Array.Empty<string>());

    [Fact]
    public void CompiledImageSize_Shrinks_Qrcodegen() =>
        AssertCarveShrinksCompiledSize(Path.Combine("qrcodegen", "c"), "qrcodegen.h",
            new[] { "qrcodegen_encodeText", "qrcodegen_getModule" },
            exclude: new[] { "qrcodegen-demo.c", "qrcodegen-test.c" });

    /// <summary>
    /// Carve+prune a repo, then compile BOTH the original and the carved .c files with the pinned gcc at
    /// <c>-Os</c> and compare their real code+data footprint (text+data from <c>size</c>) — the honest
    /// "how much smaller is the image" number, independent of any linker --gc-sections. Asserts the carve
    /// never grows the image and reports the reduction.
    /// </summary>
    private void AssertCarveShrinksCompiledSize(string repoName, string sentinel, string[] roots, string[] exclude)
    {
        var gcc = FindGcc();
        var size = gcc is null ? null : Path.Combine(Path.GetDirectoryName(gcc)!, "size.exe");
        if (gcc is null || size is null || !File.Exists(size)) return;
        var repo = FindUp(Path.Combine(".corpus", repoName));
        if (repo is null || !File.Exists(Path.Combine(repo, sentinel))) return;

        var work = Path.Combine(Path.GetTempPath(), $"codecarver-size-{repoName}-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(work);
        try
        {
            var inputs = Directory.EnumerateFiles(repo, "*.*")
                .Where(p => p.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Path.GetFileName(p), File.ReadAllText(p)))
                .ToList();

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(inputs);
            var plan = ReachabilityEngine.Compute(graph,
                new ExplicitRootProvider(symbols: roots).Discover(graph).ToList());
            FileTreeEmitter.EmitPruned(plan, graph, repo, outDir);

            // Full footprint: every original .c that isn't excluded. Carved footprint: whatever the carve
            // emitted (dropped files simply aren't there → contribute 0). Same compiler + flags for both.
            long full = ImageBytes(gcc, size, repo, Directory.EnumerateFiles(repo, "*.c"), exclude, repo);
            long carved = ImageBytes(gcc, size, outDir, Directory.EnumerateFiles(outDir, "*.c"), exclude, repo);

            var pct = full > 0 ? 100.0 * (full - carved) / full : 0;
            _out.WriteLine($"{repoName}: compiled image (text+data, -Os)  full {full:N0} B -> carved {carved:N0} B  ({pct:F0}% smaller)");
            Assert.True(carved <= full, $"{repoName}: carve GREW the compiled image ({full} -> {carved})");
            Assert.True(carved < full, $"{repoName}: carve saved nothing (both {full} B) — expected an intra-file win");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Sum of text+data (bytes that land in the image) across the given .c files, compiled -Os.</summary>
    private long ImageBytes(string gcc, string size, string cwd, IEnumerable<string> cFiles, string[] exclude, string includeDir)
    {
        long total = 0;
        var i = 0;
        foreach (var c in cFiles)
        {
            if (exclude.Contains(Path.GetFileName(c))) continue;
            var obj = $"m{i++}.o";
            var (code, _) = Run(gcc, new[] { "-c", "-Os", "-g0", "-I.", "-I" + includeDir, Path.GetFileName(c), "-o", obj }, cwd);
            if (code != 0) continue; // an include-only TU (e.g. mimalloc) — skip; both sides skip it identically
            var (sc, so) = Run(size, new[] { obj }, cwd);
            if (sc == 0) total += ParseSizeTextData(so);
        }
        return total;
    }

    /// <summary>Parse binutils <c>size</c> default output; return text+data of the data row.</summary>
    private static long ParseSizeTextData(string sizeOutput)
    {
        // Two lines: "   text   data    bss    dec ...", then the numbers. Sum text+data.
        var lines = sizeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return 0;
        var cols = lines[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return cols.Length >= 2 && long.TryParse(cols[0], out var t) && long.TryParse(cols[1], out var d) ? t + d : 0;
    }

    [Fact]
    public void HeaderCarve_OfBigRegisterHeader_ShrinksAndStillCompiles()
    {
        // End-to-end: a big auto-generated register header (passed empty = "too big to parse", kept whole
        // via #include-closure) then carved down to only the #defines the code transitively needs — and
        // the result must still compile. Offsets built from a base address exercise the closure.
        var gcc = FindGcc();
        if (gcc is null) return;

        var sb = new System.Text.StringBuilder();
        sb.Append("#ifndef CHIP_H\n#define CHIP_H\n#define CHIP_BASE 0x40000000\n");
        for (var i = 0; i < 5000; i++) sb.Append($"#define REG_{i} (CHIP_BASE + 0x{i * 4:X})\n");
        sb.Append("#endif\n");
        var chipH = sb.ToString();
        const string appC = "#include \"chip.h\"\nint use(void){ return REG_2000; }\nint dead(void){ return 0; }\n";

        var work = Path.Combine(Path.GetTempPath(), "codecarver-hc-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "app.c"), appC);
            File.WriteAllText(Path.Combine(src, "chip.h"), chipH);

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[] { ("app.c", appC), ("chip.h", "") }); // chip.h empty = not parsed
            var plan = ReachabilityEngine.Compute(graph,
                new ExplicitRootProvider(symbols: new[] { "use" }).Discover(graph).ToList());
            FileTreeEmitter.EmitPruned(plan, graph, src, outDir); // copies chip.h whole, prunes app.c's dead()

            var before = new FileInfo(Path.Combine(outDir, "chip.h")).Length;
            var res = HeaderCarver.Carve(outDir, new[] { "chip.h" });
            var carved = File.ReadAllText(Path.Combine(outDir, "chip.h"));

            Assert.Contains("#define REG_2000", carved);   // needed
            Assert.Contains("#define CHIP_BASE", carved);  // pulled in by REG_2000's body
            Assert.DoesNotContain("#define REG_2001", carved); // unused -> dropped
            Assert.True(res.BytesAfter < before / 10, $"expected big shrink, {before} -> {res.BytesAfter}");

            var (code, output) = Run(gcc, new[] { "-c", "-I.", "app.c", "-o", "o.o" }, outDir);
            Assert.True(code == 0, $"carved-header output failed to compile:\n{output}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ConfigResolvedCarve_DropsDeadBranchFile_AndBuilds()
    {
        // Closed-world #ifdef resolution: with USE_EXTRA absent, the branch that calls into extra.c is
        // dead, so extra.c is dropped — and the result must still build (the #else branch compiles).
        var gcc = FindGcc();
        if (gcc is null) return;

        const string mainC = """
            #if defined(USE_EXTRA)
            extern int extra_feature(void);
            int run(void) { return extra_feature(); }
            #else
            int run(void) { return 0; }
            #endif
            """;
        const string extraC = "int extra_feature(void) { return 42; }\n";

        var work = Path.Combine(Path.GetTempPath(), "codecarver-cfg-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllText(Path.Combine(srcDir, "main.c"), mainC);
            File.WriteAllText(Path.Combine(srcDir, "extra.c"), extraC);

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[] { ("main.c", mainC), ("extra.c", extraC) },
                new MacroTable(), closedWorldDefines: true); // USE_EXTRA absent => #if branch dead
            var run = graph.Nodes.First(n => n.Name == "run").Id;
            var plan = ReachabilityEngine.Compute(graph, new[] { new Root(run, RootKind.ExplicitSymbol) });

            Assert.Contains("extra.c", plan.DroppedFiles); // dead-branch dependency dropped

            FileTreeEmitter.Emit(plan, srcDir, outDir);
            Assert.False(File.Exists(Path.Combine(outDir, "extra.c")));

            File.WriteAllText(Path.Combine(outDir, "_verify.c"),
                "int run(void); int main(void){ return run(); }\n");
            var (code, output) = Run(gcc, new[] { "main.c", "_verify.c", "-o", "v.exe" }, outDir);
            Assert.True(code == 0, $"config-resolved carve failed to build:\n{output}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void PrunedCarve_DualSignatureSharedBody_CompilesClean()
    {
        // Regression (sqlite shell.c main/wmain): an `#if X / TYPE a(...){ / #else / TYPE b(...){ /
        // #endif ... shared body ... }` gives two signatures ONE closing brace. tree-sitter captures
        // only the #else definition, so pruning it (unreached) would either orphan the #if signature's
        // `{` OR keep it whole and dangle its dropped callees. The emitter must remove the WHOLE
        // construct — both signatures, the shared body, and the callee — leaving a clean carve.
        var gcc = FindGcc();
        if (gcc is null) return;

        const string dualC = """
            int keep_me(void);

            #if defined(WIDECHAR)
            int alt_main(int argc, unsigned short **wargv){
            #else
            int prog_main(int argc, char **argv){
            #endif
              return only_from_main(argc);
            }

            int only_from_main(int n){ return n + 1; }
            int keep_me(void){ return 7; }
            """;

        var work = Path.Combine(Path.GetTempPath(), "codecarver-dual-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(work);
        try
        {
            File.WriteAllText(Path.Combine(work, "dual.c"), dualC);
            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(new[] { ("dual.c", dualC) });
            var plan = ReachabilityEngine.Compute(graph,
                new ExplicitRootProvider(symbols: new[] { "keep_me" }).Discover(graph).ToList());
            FileTreeEmitter.EmitPruned(plan, graph, work, outDir);

            var (code, output) = Run(gcc, new[] { "-c", "dual.c", "-o", "o.o" }, outDir);
            Assert.True(code == 0, $"dual-signature carve failed to compile:\n{output}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void EmbeddedFirmware_CarvesSound_AndLinksWithArmGcc()
    {
        // The real target is an ARM image loaded via TRACE32. Carve a real Cortex-M3 firmware (vector
        // table, weak-alias handlers, linker script), then LINK it with arm-none-eabi-gcc and confirm:
        // it links, the unused functions are physically gone from the image, and the ISRs + alias target
        // (reached only via the table) survived. Skips cleanly without the ARM toolchain / fixture.
        var armgcc = FindArmGcc();
        if (armgcc is null) return;
        var fixture = FindUp(Path.Combine("examples", "cortexm-firmware"));
        if (fixture is null || !File.Exists(Path.Combine(fixture, "firmware.ld"))) return;

        var work = Path.Combine(Path.GetTempPath(), "codecarver-fw-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(work);
        try
        {
            var srcs = new[] { "startup.c", "main.c", "handlers.c" };
            var inputs = srcs.Select(f => (f, File.ReadAllText(Path.Combine(fixture, f)))).ToList();

            using var fe = new CFrontEnd();
            var graph = fe.BuildGraph(inputs);
            var plan = ReachabilityEngine.Compute(graph,
                new ExplicitRootProvider(symbols: new[] { "Reset_Handler" }).Discover(graph).ToList());
            FileTreeEmitter.EmitPruned(plan, graph, fixture, outDir);
            File.Copy(Path.Combine(fixture, "firmware.ld"), Path.Combine(outDir, "firmware.ld"), overwrite: true);

            var args = new[] { "-mcpu=cortex-m3", "-mthumb", "-ffreestanding", "-nostdlib",
                               "-Wl,-T,firmware.ld", "-o", "carved.elf" }
                       .Concat(srcs).ToArray();
            var (code, output) = Run(armgcc, args, outDir);
            Assert.True(code == 0, $"carved firmware failed to link with arm-none-eabi-gcc:\n{output}");

            var nm = Path.Combine(Path.GetDirectoryName(armgcc)!, "arm-none-eabi-nm.exe");
            var (_, syms) = Run(nm, new[] { "carved.elf" }, outDir);
            Assert.DoesNotContain("unused_helper", syms);       // dead app fn — carved out of the image
            Assert.DoesNotContain("really_unused_isr", syms);   // dead ISR (not in any table) — carved out
            Assert.Contains("SysTick_Handler", syms);           // reached only via the vector table
            Assert.Contains("Default_Handler", syms);           // weak-alias target — must survive
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Walk up from the test binary to the repo's fetched toolchain, if present.</summary>
    private static string? FindGcc() => FindUp(Path.Combine(".toolchains", "w64devkit", "bin", "gcc.exe"), file: true);

    /// <summary>Find the fetched portable arm-none-eabi-gcc (version is in the folder name), if present.</summary>
    private static string? FindArmGcc()
    {
        var tools = FindUp(".toolchains");
        return tools is null ? null
            : Directory.EnumerateFiles(tools, "arm-none-eabi-gcc.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>Find a file or directory by walking up from the test binary; null if not found.</summary>
    private static string? FindUp(string relative, bool file = false)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, relative);
            if (file ? File.Exists(cand) : Directory.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    private static (int Code, string Output) Run(string exe, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, so + se);
    }
}
