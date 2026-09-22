using CodeCarver.Core.Emit;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;

// CodeCarver CLI — early scaffold. Real subcommands (ingest compile_commands, carve, emit) land as the
// front-end is built. For now `demo` exercises the deterministic engine end-to-end so the pipeline is
// runnable and observable from day one.

var cmd = args.Length > 0 ? args[0] : "demo";
switch (cmd)
{
    case "demo":
        RunDemo();
        return 0;
    case "carve":
        return RunCarve(args);
    case "scan-log":
        return RunScanLog(args);
    case "--version":
    case "version":
        Console.WriteLine("CodeCarver 0.0.1 (scaffold)");
        return 0;
    default:
        Console.Error.WriteLine($"unknown command '{cmd}'. try: carve <dir> --roots a,b | demo | version");
        return 2;
}

static int RunScanLog(string[] args)
{
    if (args.Length < 2 || !File.Exists(args[1]))
    {
        Console.Error.WriteLine("usage: scan-log <build-log-file>");
        return 2;
    }

    var cmds = BuildLogScraper.Parse(File.ReadAllText(args[1]));
    var units = cmds.Select(c => c.File).Distinct().Count();
    var defines = cmds.SelectMany(c => c.Defines).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    var includes = cmds.SelectMany(c => c.Includes).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    Console.WriteLine($"scan-log {args[1]}");
    Console.WriteLine($"  compile commands : {cmds.Count}");
    Console.WriteLine($"  translation units: {units}");
    Console.WriteLine($"  distinct defines : {(defines.Count == 0 ? "(none)" : string.Join(", ", defines.Take(25)))}");
    Console.WriteLine($"  distinct includes: {(includes.Count == 0 ? "(none)" : string.Join(", ", includes.Take(25)))}");
    foreach (var c in cmds.Take(5))
        Console.WriteLine($"    {c.File}  -D[{string.Join(" ", c.Defines)}]  -I[{string.Join(" ", c.Includes)}]");
    return 0;
}

static int RunCarve(string[] args)
{
    if (args.Length < 2 || !Directory.Exists(args[1]))
    {
        Console.Error.WriteLine("usage: carve <dir> --roots sym1,sym2");
        return 2;
    }
    var dir = args[1];

    var roots = Array.Empty<string>();
    string? outDir = null;
    var prune = false;
    var pruneHeaders = false;
    var verify = false;
    var dumpSpans = false;
    string? whySymbol = null;
    var lang = "c";
    var defineSpecs = new List<string>();
    string? buildLog = null;
    var closedWorld = false;
    string? manifestPath = null;
    var excludeDirs = new List<string>();
    string? probeCompiler = null;
    long maxParseBytes = 20_000_000; // files bigger than this (e.g. multi-GB generated register headers)
                                     // skip the parser and are kept whole via #include-closure.
    int? parseTimeoutMs = null;      // per-file parse budget backstop (ms); null = front-end default.

    // A --config JSON file supplies defaults; explicit CLI flags below override it.
    for (var i = 2; i < args.Length - 1; i++)
    {
        if (args[i] != "--config") continue;
        var cfg = System.Text.Json.JsonSerializer.Deserialize<CarveConfig>(File.ReadAllText(args[i + 1]),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        if (cfg is null) continue;
        if (cfg.Roots is not null) roots = cfg.Roots;
        if (cfg.Lang is not null) lang = cfg.Lang.ToLowerInvariant();
        if (cfg.Out is not null) outDir = cfg.Out;
        if (cfg.Prune is not null) prune = cfg.Prune.Value;
        if (cfg.Defines is not null) defineSpecs.AddRange(cfg.Defines);
        if (cfg.BuildLog is not null) buildLog = cfg.BuildLog;
        if (cfg.AssumeDefinesComplete is not null) closedWorld = cfg.AssumeDefinesComplete.Value;
        if (cfg.Exclude is not null) excludeDirs.AddRange(cfg.Exclude);
        if (cfg.Manifest is not null) manifestPath = cfg.Manifest;
        if (cfg.MaxParseBytes is not null) maxParseBytes = cfg.MaxParseBytes.Value;
        if (cfg.PruneHeaders is not null) pruneHeaders = cfg.PruneHeaders.Value;
    }

    for (var i = 2; i < args.Length; i++)
    {
        if (args[i] == "--config") { i++; continue; } // already loaded above
        if (args[i] == "--roots" && i + 1 < args.Length)
            roots = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else if (args[i] == "--out" && i + 1 < args.Length)
            outDir = args[++i];
        else if (args[i] == "--prune")
            prune = true;
        else if (args[i] == "--prune-headers")
            pruneHeaders = true;
        else if (args[i] == "--verify")
            verify = true;
        else if (args[i] == "--lang" && i + 1 < args.Length)
            lang = args[++i].ToLowerInvariant();
        else if (args[i] == "--define" && i + 1 < args.Length)
            defineSpecs.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (args[i] == "--build-log" && i + 1 < args.Length)
            buildLog = args[++i];
        else if (args[i] == "--assume-defines-complete")
            closedWorld = true;
        else if (args[i] == "--manifest" && i + 1 < args.Length)
            manifestPath = args[++i];
        else if (args[i] == "--exclude" && i + 1 < args.Length)
            excludeDirs.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (args[i] == "--probe" && i + 1 < args.Length)
            probeCompiler = args[++i];
        else if (args[i] == "--max-parse-bytes" && i + 1 < args.Length)
            long.TryParse(args[++i], out maxParseBytes);
        else if (args[i] == "--parse-timeout" && i + 1 < args.Length)
            parseTimeoutMs = int.TryParse(args[++i], out var pt) ? pt * 1000 : null;
        else if (args[i] == "--dump-spans")
            dumpSpans = true;
        else if (args[i] == "--why" && i + 1 < args.Length)
            whySymbol = args[++i];
    }

    // Preprocessor config: explicit --define plus any -D flags scraped from a --build-log.
    if (buildLog is not null && File.Exists(buildLog))
        defineSpecs.AddRange(BuildLogScraper.Parse(File.ReadAllText(buildLog)).SelectMany(c => c.Defines));
    var defines = defineSpecs.Count > 0 ? MacroTable.FromDefines(defineSpecs) : null;

    // --probe: ask a real compiler for its complete macro set (predefined + target + -D) and resolve
    // #ifdefs against that in closed-world mode — accurate, no "is my define list complete?" guessing.
    if (probeCompiler is not null)
    {
        var probed = MacroProbe.Probe(probeCompiler, defineSpecs.Select(d => "-D" + d));
        if (probed is not null) { defines = probed; closedWorld = true; }
        else Console.Error.WriteLine($"  warning : --probe '{probeCompiler}' could not run; ignoring it (no probe-based #ifdef resolution)");
    }

    var exts = lang switch
    {
        "cpp" => new[] { ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".h" },
        "csharp" or "cs" => new[] { ".cs" },
        "cmm" => new[] { ".cmm" },
        _ => new[] { ".c", ".h" },
    };

    // Intra-file pruning is only compile-verifiable for C/C++; other languages carve file-level.
    if (prune && lang is not ("c" or "cpp"))
    {
        Console.WriteLine($"  note    : --prune is C/C++ only (needs compile verification); using file-level carve for '{lang}'.");
        prune = false;
    }

    if (roots.Length == 0)
    {
        Console.Error.WriteLine("carve needs --roots sym1,sym2 (the entry symbols to keep)");
        return 2;
    }

    var paths = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
        .Where(p => exts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        .Where(p => excludeDirs.Count == 0 ||
                    !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (paths.Count == 0)
    {
        Console.Error.WriteLine($"no {lang} source files found under {dir}");
        return 2;
    }

    // Oversized files (multi-GB auto-generated register headers) are never read into a string or parsed:
    // they'd blow past .NET's ~2GB string limit and explode tree-sitter memory. For languages with
    // include/DO-closure (C/C++/.cmm) we register them as File nodes with null text — kept whole when a
    // kept unit includes them, copied verbatim by the emitter. C# has no such closure, so it's exempt.
    var closureLang = lang is "c" or "cpp" or "cmm";
    var bigFiles = new List<(string Rel, long Bytes)>();
    if (closureLang)
        foreach (var p in paths)
        {
            var len = new FileInfo(p).Length;
            if (len > maxParseBytes)
                bigFiles.Add((Path.GetRelativePath(dir, p).Replace('\\', '/'), len));
        }
    var bigSet = bigFiles.Select(b => b.Rel).ToHashSet(StringComparer.Ordinal);

    var inputs = paths.Select(p =>
    {
        var rel = Path.GetRelativePath(dir, p).Replace('\\', '/');
        return (rel, bigSet.Contains(rel) ? "" : File.ReadAllText(p)); // "" = don't read/parse; keep whole
    });
    using ICarveFrontEnd fe = lang switch
    {
        "cpp" => new CppFrontEnd(),
        "csharp" or "cs" => new CSharpFrontEnd(),
        "cmm" => new CmmFrontEnd(),
        _ => new CFrontEnd(),
    };
    if (parseTimeoutMs is not null && fe is TreeSitterFrontEnd tsfe) tsfe.ParseBudgetMs = parseTimeoutMs.Value;
    var graph = fe.BuildGraph(inputs, defines, closedWorld);

    // Non-fatal diagnostics (kept-whole fragments, unresolved/ambiguous .cmm DO). Surfacing these avoids
    // the "silent 100% smaller" trap. Capped so a tree with hundreds of dynamic DOs doesn't flood output.
    if (fe.Warnings.Count > 0)
    {
        const int cap = 12;
        foreach (var w in fe.Warnings.Take(cap)) Console.Error.WriteLine("  warn    : " + w);
        if (fe.Warnings.Count > cap) Console.Error.WriteLine($"  warn    : (+{fe.Warnings.Count - cap} more warnings)");
    }

    var explicitRoots = new ExplicitRootProvider(symbols: roots).Discover(graph).ToList();
    if (roots.Length > 0 && explicitRoots.Count == 0)
    {
        Console.Error.WriteLine($"none of the requested roots were found as symbols: {string.Join(", ", roots)}");
        return 1;
    }
    // Implicit roots (constructor/used/init-array) are ALWAYS added: the runtime/linker keep them
    // regardless of any call, so a from-main closure that dropped them would ship a broken image.
    var implicitRoots = new AttributeRootProvider().Discover(graph).ToList();

    // Assembly startup (.s/.S) references C handlers by name (vector table `.word Handler`) — root them.
    var asmRoots = new List<Root>();
    if (lang is "c" or "cpp")
    {
        var asmTexts = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".s" or ".asm")
            .Where(p => excludeDirs.Count == 0 ||
                        !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase)))
            .Where(p => new FileInfo(p).Length <= maxParseBytes)
            .Select(File.ReadAllText).ToList();
        if (asmTexts.Count > 0)
            asmRoots = new AsmReferenceRootProvider(asmTexts).Discover(graph).ToList();
    }

    // A symbol placed in a custom section that the linker script KEEP()s (initcall / registration
    // tables) is collected by the linker, never called — root it so a from-main closure can't drop it.
    // Gated on a linker script actually being present, so non-embedded trees pay nothing.
    var sectionRoots = new List<Root>();
    if (lang is "c" or "cpp")
    {
        bool Included(string p) => excludeDirs.Count == 0 ||
            !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase));
        var linkerScripts = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".ld" or ".lds" or ".ldscript")
            .Where(Included).Where(p => new FileInfo(p).Length <= maxParseBytes)
            .Select(File.ReadAllText).ToList();
        if (linkerScripts.Count > 0)
        {
            var srcTexts = paths.Where(p => new FileInfo(p).Length <= maxParseBytes)
                                .Select(File.ReadAllText).ToList();
            sectionRoots = new LinkerSectionRootProvider(srcTexts, linkerScripts).Discover(graph).ToList();
        }
    }

    var rootSet = explicitRoots.Concat(implicitRoots).Concat(asmRoots).Concat(sectionRoots).ToList();
    if (rootSet.Count == 0)
    {
        Console.Error.WriteLine("no roots to carve from: name entry symbols with --roots");
        return 1;
    }

    var plan = ReachabilityEngine.Compute(graph, rootSet);
    var s = plan.Stats;

    if (dumpSpans)
    {
        foreach (var n in graph.Nodes
                     .Where(n => n.Kind is NodeKind.Function or NodeKind.Global or NodeKind.Macro)
                     .OrderBy(n => n.FilePath, StringComparer.Ordinal)
                     .ThenBy(n => n.Span.StartLine))
            Console.WriteLine($"  {(plan.IsKept(n.Id) ? "KEEP" : "drop")} {n.Kind} {n.FilePath}:{n.Span}\t{n.Name}");
        return 0;
    }

    // --why <symbol>: explain the keep-chain (or that it was carved) for a named symbol — for debugging
    // a carve against a real tree ("why is this huge thing still here?" / "why did this get dropped?").
    if (whySymbol is not null)
    {
        var matches = graph.Nodes.Where(n => n.Kind != NodeKind.File && n.Name == whySymbol).ToList();
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"no symbol named '{whySymbol}' was found");
            return 1;
        }
        foreach (var n in matches.OrderBy(n => n.FilePath, StringComparer.Ordinal).ThenBy(n => n.Span.StartLine))
            Console.WriteLine($"  {n.Kind} {n.Name} @ {n.FilePath}:{n.Span}\n    {plan.Explain(n.Id)}");
        return 0;
    }

    // Size accounting: the whole scanned source vs. what the carve keeps (the headline number).
    long originalBytes = 0;
    var sizeByRel = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var p in paths)
    {
        var len = new FileInfo(p).Length;
        originalBytes += len;
        sizeByRel[Path.GetRelativePath(dir, p).Replace('\\', '/')] = len;
    }

    Console.WriteLine($"CodeCarver — carve of {dir}");
    Console.WriteLine($"  roots   : {string.Join(", ", roots)}");
    if (implicitRoots.Count > 0)
        Console.WriteLine($"  implicit: {implicitRoots.Count} constructor/used/init-array symbol(s) auto-kept: "
                          + Summarize(implicitRoots.Select(r => r.Note ?? r.Node.ToString()).Distinct().ToList()));
    if (asmRoots.Count > 0)
        Console.WriteLine($"  asm     : {asmRoots.Count} symbol(s) referenced from .s startup auto-kept: "
                          + Summarize(asmRoots.Select(r => r.Note ?? r.Node.ToString()).Distinct().ToList()));
    if (sectionRoots.Count > 0)
        Console.WriteLine($"  section : {sectionRoots.Count} symbol(s) in linker KEEP()'d section(s) auto-kept: "
                          + Summarize(sectionRoots.Select(r => r.Note ?? r.Node.ToString()).Distinct().ToList()));
    if (defines is not null)
        Console.WriteLine($"  config  : {defineSpecs.Distinct().Count()} define(s), #ifdef resolution ON" +
                          (closedWorld ? " (closed-world: absent macros treated as undefined)" : " (open-world: unknown branches kept)"));
    Console.WriteLine($"  nodes   : {s.ReachedNodes}/{s.TotalNodes} kept ({s.NodeKeepRatio:P0}), {s.DroppedNodes} carved");
    Console.WriteLine($"  files   : {s.KeptFiles}/{s.TotalFiles} kept, {s.DroppedFiles} dropped");
    if (bigFiles.Count > 0)
        Console.WriteLine($"  big     : {bigFiles.Count} file(s) > {maxParseBytes:N0} B not parsed (kept whole via #include-closure): "
                          + Summarize(bigFiles.OrderByDescending(b => b.Bytes).Select(b => $"{b.Rel} ({b.Bytes:N0} B)").ToList()));
    if (plan.DroppedFiles.Count > 0)
        Console.WriteLine("  dropped : " + Summarize(plan.DroppedFiles));

    long carvedBytes;
    if (outDir is not null)
    {
        var res = prune
            ? FileTreeEmitter.EmitPruned(plan, graph, dir, outDir)
            : FileTreeEmitter.Emit(plan, dir, outDir);
        carvedBytes = res.BytesWritten;
        var how = prune ? "pruned (intra-file: unreached functions removed)" : "file-level (whole kept files)";
        Console.WriteLine($"  emitted : {res.FilesWritten} files -> {outDir}  [{how}]");
        if (prune)
            Console.WriteLine("  note    : --prune is EXPERIMENTAL — always build-verify. File-level (omit --prune) is the sound default.");

        // --prune-headers: strip unused #defines from the giant register headers we kept whole (C/C++ only).
        if (pruneHeaders && lang is "c" or "cpp")
        {
            var keptBig = bigFiles.Select(b => b.Rel).Where(plan.KeptFiles.Contains).ToList();
            if (keptBig.Count > 0)
            {
                var hc = HeaderCarver.Carve(outDir, keptBig);
                carvedBytes -= hc.BytesBefore - hc.BytesAfter; // those files shrank on disk
                var hpct = hc.BytesBefore > 0 ? (double)(hc.BytesBefore - hc.BytesAfter) / hc.BytesBefore : 0;
                Console.WriteLine($"  headers : {keptBig.Count} big header(s) carved — {hc.DefinesKept:N0} #defines kept, "
                                  + $"{hc.DefinesDropped:N0} dropped; {hc.BytesBefore:N0} B -> {hc.BytesAfter:N0} B ({hpct:P0} smaller)");
                Console.WriteLine("  note    : --prune-headers is EXPERIMENTAL (drops unused #defines from kept headers) — always build-verify.");
            }
        }
    }
    else
    {
        carvedBytes = plan.KeptFiles.Sum(f => sizeByRel.TryGetValue(f, out var b) ? b : 0);
        Console.WriteLine("  (analysis only — pass --out <dir> [--prune] to write the carved tree)");
    }

    var saved = originalBytes - carvedBytes;
    var pct = originalBytes > 0 ? (double)saved / originalBytes : 0;
    Console.WriteLine($"  size    : {originalBytes:N0} B -> {carvedBytes:N0} B  ({pct:P0} smaller, saved {saved:N0} B)");

    // --verify: compiler-free soundness gate — no KEPT function may call an in-scope function that was
    // carved out (it wouldn't link). Catches an edge our model missed (a blind spot). C/C++ only.
    var verifyFailed = false;
    if (verify && fe is TreeSitterFrontEnd tsv)
    {
        var violations = SoundnessCheck.KeptCallingDropped(graph, plan, tsv.CallSites);
        if (violations.Count == 0)
            Console.WriteLine("  verify  : OK — every in-scope callee of a kept function is kept");
        else
        {
            verifyFailed = true;
            Console.WriteLine($"  verify  : {violations.Count} UNSOUND call(s) — a kept function calls an in-scope function that was carved out:");
            foreach (var v in violations.Take(20))
                Console.WriteLine($"            {v.Caller}() -> {v.Callee}()  [{v.File}]");
            if (violations.Count > 20) Console.WriteLine($"            (+{violations.Count - 20} more)");
        }
    }
    else if (verify)
        Console.WriteLine($"  verify  : (not available for --lang {lang}; C/C++ only)");

    if (manifestPath is not null)
    {
        var manifest = new
        {
            root = dir,
            roots,
            lang,
            defines = defineSpecs.Distinct().ToArray(),
            closedWorld,
            pruned = prune,
            stats = new
            {
                s.TotalNodes, s.ReachedNodes, s.DroppedNodes,
                s.TotalFiles, s.KeptFiles, s.DroppedFiles,
                originalBytes, carvedBytes, savedBytes = saved,
            },
            keptFiles = plan.KeptFiles,
            droppedFiles = plan.DroppedFiles,
        };
        File.WriteAllText(manifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  manifest: {manifestPath}");
    }
    return verifyFailed ? 3 : 0; // non-zero so --verify is usable as a gate in scripts
}

static string Summarize(IReadOnlyList<string> files, int max = 12)
    => files.Count <= max
        ? string.Join(", ", files)
        : string.Join(", ", files.Take(max)) + $", … (+{files.Count - max} more)";

static void RunDemo()
{
    // A tiny embedded-flavoured graph that contains every hazard we discussed, so the output shows
    // the engine handling each: a vector-table ISR, a function-pointer dispatch, a used macro, and
    // genuinely dead debug code.
    var b = new GraphBuilder();

    var main = b.Func("main", "main.c", NodeFlags.None, line: 10);
    var init = b.Func("init", "main.c", line: 30);
    var runLoop = b.Func("run_loop", "main.c", line: 50);
    var readSensor = b.Func("read_sensor", "sensor.c", line: 12);
    var sensorType = b.Type("sensor_t", "sensor.h", line: 4);
    var scaleMacro = b.Macro("SCALE_MV", "sensor.h", line: 9);

    // A command handler reached ONLY by taking its address and dispatching through a pointer.
    var dispatch = b.Func("dispatch", "main.c", line: 70);
    var cmdHandler = b.Func("cmd_handler", "cmd.c", NodeFlags.AddressTaken, line: 20);

    // An ISR reached ONLY via the interrupt vector table — never called in source.
    var timerIsr = b.Func("Timer_ISR", "isr.c", NodeFlags.AddressTaken, line: 15);

    // Dead code: present in the repo, referenced by nothing reachable.
    var debugDump = b.Func("unused_debug_dump", "debug.c", line: 8);
    var debugFmt = b.Func("fmt_hex", "debug.c", line: 40);

    b.Calls(main, init);
    b.Calls(main, runLoop);
    b.Calls(main, dispatch);
    b.Calls(runLoop, readSensor);
    b.Refs(readSensor, sensorType);
    b.Expands(readSensor, scaleMacro);
    b.AddressTaken(dispatch, cmdHandler); // conservative edge: keeps the pointer's target
    b.Calls(debugDump, debugFmt);         // dead subgraph

    var roots = new List<Root>
    {
        new(main, RootKind.EntryPoint, "image entry"),
        new(timerIsr, RootKind.VectorTable, "IRQ7 -> Timer_ISR"),
    };

    var safe = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.Safe);
    var minimal = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.MinimalUnsafe);

    Console.WriteLine("CodeCarver demo — carve of a toy embedded image\n");
    Console.WriteLine($"  nodes total   : {safe.Stats.TotalNodes}");
    Console.WriteLine($"  nodes kept    : {safe.Stats.ReachedNodes}  ({safe.Stats.NodeKeepRatio:P0})");
    Console.WriteLine($"  nodes carved  : {safe.Stats.DroppedNodes}");
    Console.WriteLine($"  files kept    : {string.Join(", ", safe.KeptFiles)}");
    Console.WriteLine($"  files dropped : {string.Join(", ", safe.DroppedFiles)}");

    Console.WriteLine("\n  why the tricky ones survived:");
    Console.WriteLine("    " + safe.Explain(timerIsr));
    Console.WriteLine("    " + safe.Explain(cmdHandler));

    Console.WriteLine("\n  dead code correctly carved:");
    Console.WriteLine("    " + safe.Explain(debugDump));

    var tax = safe.Stats.ReachedNodes - minimal.Stats.ReachedNodes;
    Console.WriteLine($"\n  indirection tax (safe - minimal): {tax} node(s) kept only because of");
    Console.WriteLine("    unresolved function pointers / vtables. Sound carve keeps them; the");
    Console.WriteLine("    minimal set would have dropped cmd_handler and broken the image.");
}

sealed class CarveConfig
{
    public string[]? Roots { get; set; }
    public string? Lang { get; set; }
    public string? Out { get; set; }
    public bool? Prune { get; set; }
    public string[]? Defines { get; set; }
    public string? BuildLog { get; set; }
    public bool? AssumeDefinesComplete { get; set; }
    public string[]? Exclude { get; set; }
    public string? Manifest { get; set; }
    public long? MaxParseBytes { get; set; }
    public bool? PruneHeaders { get; set; }
}
