using System.Reflection;
using CodeCarver.Core.Diagnostics;
using CodeCarver.Core.Emit;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;

namespace CodeCarver.Cli;

/// <summary>
/// The <c>carve</c> subcommand. Extracted from the top-level <c>Program</c> so it can be driven IN-PROCESS
/// via <see cref="Run"/> with injected <see cref="TextWriter"/>s: coverlet only instruments in-process code,
/// so the process-spawning CLI integration tests validate behavior but don't MEASURE it — an in-process
/// entry point lets the test suite exercise (and measure) the whole pipeline. <c>Program</c>'s dispatcher is
/// a thin wrapper over <see cref="Run"/>.
/// </summary>
public static class CarveCommand
{
    /// <summary>
    /// Run a carve. <paramref name="args"/> is the full argv (<c>args[0]=="carve"</c>, <c>args[1]</c> = source
    /// dir). Output goes to the injected writers (Console in production, a capture buffer under test). Wraps the
    /// core with the crash-diagnostic safety net that used to live in the dispatcher: an UNEXPECTED exception
    /// must not dump a raw stack trace — report cleanly and, if a diagnostic report exists, still write a
    /// source-free package capturing the failure so a crash is diagnosable too.
    /// </summary>
    public static int Run(string[] args, TextWriter @out, TextWriter err)
    {
        try { return RunCore(args, @out, err); }
        catch (Exception ex)
        {
            err.WriteLine($"carve failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
            // Auto-diagnose ANY unhandled crash, even without --diag: a one-time failure shouldn't force the
            // user to reproduce it just to capture a report. Write to the user's --diag path if given, else a
            // default temp path. Source-free, so it's always safe to auto-write and safe to share.
            if (DiagState.Report is { } rpt)
            {
                rpt.SetFailure(ex);
                var dp = DiagState.Path ?? DiagState.DefaultPath
                         ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeCarver_crash.zip");
                if (rpt.TryWritePackage(dp, out var zp, out var derr))
                    err.WriteLine($"  diag    : diagnostic package written -> {zp}\n"
                        + "            it contains no source (only what the tool did) — send this file to report the bug.");
                else
                    err.WriteLine($"  warn    : could not write diagnostic package ({derr})");
            }
            return 1;
        }
    }

    // The build stamps the git commit into AssemblyInformationalVersion (see the .csproj StampGitSha target),
    // so this is `<Version>+<sha>` (or `+<sha>-dirty`) -- a pulled build reports EXACTLY what was built, which
    // is what a work-machine session should cite instead of a bare commit. Falls back to the bare version if a
    // build had no git (offline).
    internal static string Version()
    {
        var info = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(info) ? "0.1.0" : info;
    }

    /// <summary>
    /// `init [path]` — write the annotated TOML config template (the redesigned config; see redesign-spec-v2).
    /// Defaults to <c>carve.toml</c>. Refuses to overwrite an existing file (so you can't clobber an edited
    /// config). This is the one-liner that bootstraps a carve: write it, fill it in, then
    /// <c>carve &lt;source-dir&gt; --config carve.toml</c>.
    /// </summary>
    public static int Init(string[] args, TextWriter @out, TextWriter err)
    {
        var path = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "carve.toml";
        if (File.Exists(path))
        {
            err.WriteLine($"'{path}' already exists — refusing to overwrite. Delete it or choose another path.");
            return 2;
        }
        try
        {
            File.WriteAllText(path, ConfigLoader.Template);
            @out.WriteLine($"wrote {path}");
            @out.WriteLine($"edit it, then run:  carve <source-dir> --config {path}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or System.Security.SecurityException)
        {
            err.WriteLine($"could not write '{path}' ({ex.GetType().Name}: {ex.Message})");
            return 2;
        }
    }

    static int RunCore(string[] args, TextWriter @out, TextWriter err)
    {
        // carve <source-dir> --config carve.toml [--stage <name>] [--why <symbol>]
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            err.WriteLine("usage: carve <source-dir> --config carve.toml [--stage <name>] [--why <symbol>]");
            err.WriteLine("       run 'init' to write an annotated carve.toml.");
            return 2;
        }
        var dir = args[1];

        string? configPath = null, stageName = null, whySymbol = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] == "--stage" && i + 1 < args.Length) stageName = args[++i];
            else if (args[i] == "--why" && i + 1 < args.Length) whySymbol = args[++i];
            else
            {
                err.WriteLine($"unknown or incomplete option '{args[i]}'. usage: carve <source-dir> --config carve.toml "
                    + "[--stage <name>] [--why <symbol>].  Everything else lives in the config — run 'init' for a template.");
                return 2;
            }
        }
        if (configPath is null) { err.WriteLine("carve needs --config <carve.toml>. Run 'init' to create one."); return 2; }

        // Load + validate the TOML config, then resolve it (union selected builds/runs, derive world, pick stages).
        var loaded = ConfigLoader.Load(configPath);
        foreach (var w in loaded.Warnings) err.WriteLine($"  warn    : {w}");
        if (loaded.Config is null) { foreach (var e in loaded.Errors) err.WriteLine(e); return 2; }
        var resolution = CarveResolver.Resolve(loaded.Config, stageName);
        if (resolution.Carve is null) { foreach (var e in resolution.Errors) err.WriteLine(e); return 2; }
        var cv = resolution.Carve;

        // Resolved config -> engine inputs.
        var roots = cv.EntryPoints.ToArray();
        var lang = cv.Languages[0];
        if (cv.Languages.Count > 1)
            err.WriteLine($"  note    : languages [{string.Join(", ", cv.Languages)}] given; carving '{lang}' this build "
                + "(C+C++ single-graph merge is a later phase).");
        var defineSpecs = cv.Defines.ToList();
        var buildLogs = cv.BuildLogs.ToList();
        var closedWorld = cv.ClosedWorld;
        var excludeDirs = cv.ExcludeDirectories.ToList();
        var auxGlobs = cv.ForceKeepFiles.ToList();          // forceKeepFiles
        var probeCompiler = cv.Compilers.Count > 0 ? cv.Compilers[0] : null;
        var traceList = cv.RunTraceLogs.ToList();           // function-execution trace(s)
        var buildFileTraces = cv.BuildTraceFiles.ToList();
        var runFileTraces = cv.RunTraceFiles.ToList();
        var outputDirectory = cv.OutputDirectory;

        // Fixed, auto-handled settings (no longer user-facing flags).
        var pruneGarbage = true;        // auto-exclude provable non-inputs; forceKeepFiles un-drops
        var strictRoots = true;         // a missing NAMED entry point always fails (soundness check always runs below)
        string? traceFormat = null;     // standard trace-log format only (no regex knob)
        string? fileTraceFormat = null; // file-access traces auto-detect
        long maxParseBytes = cv.MaxParseBytes ?? 20_000_000;            // [advanced] escape hatches
        int? parseTimeoutMs = cv.ParseTimeout is { } pt ? pt * 1000 : null;
        int? maxSymbolsPerFile = cv.MaxSymbolsPerFile;
        var dumpSpans = false;
        string? diagPath = null;        // no --diag flag; crash auto-diag still works via DiagState
        // Per-stage; declared here for the diag snapshot, set inside the emit loop.
        var prune = false;
        var pruneHeaders = false;

        // Fail fast if any referenced INPUT file is missing (a typo'd path must not carve with less than intended).
        {
            var inputFiles = buildLogs.Concat(traceList).Concat(buildFileTraces).Concat(runFileTraces)
                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = inputFiles.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0)
            {
                err.WriteLine($"missing {missing.Count} input file(s) referenced in the config — fix the path(s):");
                foreach (var m in missing) err.WriteLine($"  not found: {m}");
                return 2;
            }
        }

        // outputDirectory must be OUTSIDE the source tree (we write a complete tree there, atomically). Because
        // it's disjoint from source, our own codecarver/ output is never under the scanned tree — no need to
        // exclude it by name (and excluding a bare "codecarver" would wrongly match a repo/path segment of that name).
        string outputFull;
        try { outputFull = Path.GetFullPath(outputDirectory); }
        catch (Exception ex) { err.WriteLine($"outputDirectory '{outputDirectory}' is not a usable path ({ex.GetType().Name}: {ex.Message})"); return 2; }
        if (OutputPath.Overlaps(dir, outputFull))
        { err.WriteLine($"outputDirectory must be OUTSIDE the source tree (source '{Path.GetFullPath(dir)}' overlaps '{outputFull}')."); return 2; }
        if (File.Exists(outputFull))
        { err.WriteLine($"outputDirectory '{outputFull}' is a file, not a directory."); return 2; }

        // Diagnostic collector for this run: a source-free snapshot (version/env/params/stats/warnings/timings)
        // written to ONE shareable .zip on request via --diag, or automatically on an unhandled failure (the
        // top-level handler in the dispatcher reads DiagState). Cheap to build unconditionally so breadcrumbs
        // accumulate; only WRITTEN when --diag is set. Paths are redacted at write time (no username leaks).
        var diag = DiagnosticReport.Start();
        DiagState.Report = diag;
        DiagState.Path = diagPath;
        // Default landing spot for an auto-written package (unhandled crash, or --diag-repro/--diag-verbose used
        // without an explicit --diag path). Session-stamped so concurrent runs don't collide.
        DiagState.DefaultPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeCarver_diag_{diag.SessionId}.zip");
        diag.Set("codecarverVersion", Version());
        diag.Set("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        diag.Set("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        diag.Set("processArch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        // System capacity — source-free numbers that matter for the failures we actually see (the 90 GB OOM
        // class): core count, memory available to the process, and free/total space on the source volume.
        diag.Set("cpuCores", Environment.ProcessorCount);
        try { diag.Set("totalMemoryBytes", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes); } catch { /* best effort */ }
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir))!);
            diag.Set("sourceDriveFreeBytes", drive.AvailableFreeSpace);
            diag.Set("sourceDriveTotalBytes", drive.TotalSize);
        }
        catch { /* a network share / odd root may refuse — omit rather than fail */ }
        // Toolchain identity (source-free): the compiler's own --version banner, so a build/config issue can be
        // tied to a specific toolchain. Only when a compiler was named (--probe); never runs an unknown binary.
        if (probeCompiler is not null)
        {
            var ver = ToolchainVersion(probeCompiler);
            if (ver is not null) diag.Set("probeCompilerVersion", ver);
        }
        // PRIVACY: the diagnostic package is meant to be shareable, so it must NOT carry the proprietary source
        // path, output/build-log/config paths, or the root SYMBOL NAMES. Record the command line as flags with
        // their values elided (which flags were used is the useful debug signal), classify the source root instead
        // of storing it, and record only the ROOT COUNT — never the names. (Redact() is a further backstop for any
        // stray path in free text like exception messages.)
        diag.Set("commandLine", SanitizeCommandLine(args));
        diag.Set("sourceRootKind", ClassifyRoot(dir));
        diag.Set("lang", lang);
        diag.Set("rootCount", roots.Length);
        diag.Set("closedWorld", closedWorld);
        diag.Set("stageCount", cv.Stages.Count);
        diag.Event("args parsed");

        // Preprocessor config: explicit --define plus -D flags scraped from EVERY --build-log (parsed once here
        // and reused for include-dir resolution below). A named-but-missing log is a silent-config trap -> warn.
        var buildCmds = new List<CompileCommand>();
        foreach (var bl in buildLogs.Distinct())
        {
            if (!File.Exists(bl)) { err.WriteLine($"  warn    : --build-log file not found: {bl} (skipped)"); continue; }
            buildCmds.AddRange(BuildLogScraper.Parse(File.ReadAllText(bl)));
        }
        // Per-TU preprocessor config from the build log. A build can compile the SAME file in multiple configs;
        // unioning all TUs' -D and applying it globally would mark a macro "defined" for a file that was compiled
        // WITHOUT it, dropping the #else branch that TU really compiles (UNSOUND — eval-#9). Instead: resolve each
        // command to its carve-relative file and, PER FILE, keep only the defines CONSISTENT across all of that
        // file's commands (a macro defined in some-but-not-all -> UNKNOWN -> both branches kept). Files not
        // individually logged use the UNIVERSAL set (consistent across EVERY command). Manual --define/--config
        // are global user assertions applied to every file.
        var manualDefines = defineSpecs.Distinct().ToList();
        Func<string, MacroTable?>? perFileDefines = null;
        MacroTable? defines;

        static string SpecName(string spec) { var eq = spec.IndexOf('='); return eq < 0 ? spec : spec[..eq]; }
        // Split a group of compile commands into: CONSISTENT (defined in EVERY command with one value -> definitely
        // defined) and VARYING (defined in some-but-not-all, or with conflicting values -> UNKNOWN, keep both
        // branches even under closed-world). A name never mentioned in the group stays absent (closed-world =
        // undefined). This is the crux of per-TU soundness with --probe (eval-#12): "absent" and "varies" differ.
        static (List<string> Consistent, List<string> Varying) AnalyzeDefines(IReadOnlyList<CompileCommand> cmds)
        {
            var specsByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var defCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cc in cmds)
                foreach (var name in cc.Defines.Select(SpecName).Distinct())
                {
                    defCount[name] = defCount.GetValueOrDefault(name) + 1;
                    if (!specsByName.TryGetValue(name, out var set)) specsByName[name] = set = new(StringComparer.Ordinal);
                    foreach (var d in cc.Defines) if (SpecName(d) == name) set.Add(d);
                }
            var consistent = new List<string>();
            var varying = new List<string>();
            foreach (var kv in specsByName)
            {
                if (defCount[kv.Key] == cmds.Count && kv.Value.Count == 1) consistent.Add(kv.Value.First());
                else varying.Add(kv.Key);   // some-but-not-all, or conflicting values
            }
            return (consistent, varying);
        }
        string? CmdRel(CompileCommand cc)
        {
            try
            {
                var bd = Path.IsPathFullyQualified(cc.Directory) ? cc.Directory : Path.GetFullPath(Path.Combine(dir, cc.Directory));
                var abs = Path.IsPathFullyQualified(cc.File) ? cc.File : Path.GetFullPath(Path.Combine(bd, cc.File));
                var rel = Path.GetRelativePath(dir, abs).Replace('\\', '/');
                return rel.StartsWith("..", StringComparison.Ordinal) ? null : rel; // outside the carve tree
            }
            catch { return null; }
        }

        // Per-file (consistent, varying) define sets from the build log (manual --define joins the shared base
        // below, so it is applied to every file regardless).
        var perFileSpecs = new Dictionary<string, (List<string> Consistent, List<string> Varying)>(StringComparer.OrdinalIgnoreCase);
        var universalConsistent = new List<string>();
        var universalVarying = new List<string>();
        var byFileCount = 0;
        if (buildCmds.Count > 0)
        {
            var byFile = new Dictionary<string, List<CompileCommand>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cc in buildCmds)
            {
                var rel = CmdRel(cc);
                if (rel is null) continue;
                if (!byFile.TryGetValue(rel, out var l)) byFile[rel] = l = new List<CompileCommand>();
                l.Add(cc);
            }
            foreach (var kv in byFile) perFileSpecs[kv.Key] = AnalyzeDefines(kv.Value);
            (universalConsistent, universalVarying) = AnalyzeDefines(buildCmds);
            byFileCount = byFile.Count;
        }

        // --probe: ask a real compiler for its PREDEFINED + target macros (plus manual --define) as a closed-world
        // BASE. Deliberately WITHOUT the build log's per-TU -D: unioning those into one global probe would drop
        // the #else branch a differently-configured TU compiles (the eval-#9 union bug, back under --probe —
        // eval-#11). The per-file consistent build-log defines are layered ON TOP of this base, per file.
        MacroTable? probeBase = null;
        if (probeCompiler is not null)
        {
            probeBase = MacroProbe.Probe(probeCompiler, manualDefines.Select(d => "-D" + d));
            if (probeBase is not null) closedWorld = true;
            else err.WriteLine($"  warning : --probe '{probeCompiler}' could not run; ignoring it (no probe-based #ifdef resolution)");
        }

        // Shared base for every file's table: the probed macros (if any) else the manual --define set. A group's
        // CONSISTENT specs are defined on top; its VARYING names are marked UNKNOWN so their #ifdef branches stay
        // live even under closed-world (--probe / --assume-defines-complete). Absent names still follow closed-world.
        MacroTable BuildTable(List<string> consistent, List<string> varying)
        {
            var t = probeBase is not null ? probeBase.Clone() : MacroTable.FromDefines(manualDefines);
            foreach (var s in consistent) t.Define(s);
            foreach (var n in varying) t.MarkUnknown(n);
            return t;
        }

        // A logged .c that is ALSO #included by another TU (unity/jumbo build, or "#include the .c" for a table
        // /template) is compiled under the includer's config too — its own per-file config would be unsound
        // (eval-#11), so fall back to the UNIVERSAL set for it. Populated after the file scan (needs the include
        // text); consulted by the closure below.
        var includedCFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var haveDefines = manualDefines.Count > 0 || probeBase is not null
                          || universalConsistent.Count > 0 || universalVarying.Count > 0
                          || perFileSpecs.Values.Any(v => v.Consistent.Count > 0 || v.Varying.Count > 0);
        if (buildCmds.Count > 0 && haveDefines)
        {
            var universalTable = BuildTable(universalConsistent, universalVarying);
            var perFile = perFileSpecs.ToDictionary(kv => kv.Key, kv => BuildTable(kv.Value.Consistent, kv.Value.Varying),
                                                    StringComparer.OrdinalIgnoreCase);
            perFileDefines = f => includedCFiles.Contains(f) ? universalTable
                                  : (perFile.TryGetValue(f, out var t) ? t : universalTable);
            defines = universalTable;                 // fallback (files absent from the log; non-TreeSitter paths)
            defineSpecs = manualDefines.Concat(universalConsistent).Distinct().ToList(); // summary/manifest

            var naive = buildCmds.SelectMany(c => c.Defines.Select(SpecName)).Distinct().Count();
            err.WriteLine($"  build   : {buildLogs.Distinct().Count()} build-log(s), {buildCmds.Count} compile command(s), "
                + $"{byFileCount} file(s); per-file #ifdef config (universal {universalConsistent.Count}/{naive} macro(s); "
                + "the rest vary per TU -> both branches kept)" + (probeBase is not null ? " on a probed base" : ""));
        }
        else
        {
            defines = haveDefines ? BuildTable(new List<string>(), new List<string>()) : null; // probe/manual only, or none
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
            @out.WriteLine($"  note    : --prune is C/C++ only (needs compile verification); using file-level carve for '{lang}'.");
            prune = false;
        }

        if (roots.Length == 0)
        {
            err.WriteLine("carve needs --roots sym1,sym2 (the entry symbols to keep)");
            return 2;
        }

        // All source-tree scans go through SourceWalk (Core): skips unreadable dirs (network shares; eval-#7)
        // and does not recurse into directory junctions/symlinks (loop / double-scan) while still returning
        // symlinked source FILES (dropping one would be unsound).
        var paths = CodeCarver.Core.Util.SourceWalk.Files(dir)
            .Where(p => exts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(p => excludeDirs.Count == 0 ||
                        !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (paths.Count == 0)
        {
            err.WriteLine($"no {lang} source files found under {dir}");
            return 2;
        }

        // Oversized files (multi-GB auto-generated register headers) are never read into a string or parsed:
        // they'd blow past .NET's ~2GB string limit and explode tree-sitter memory. For languages with
        // include/DO-closure (C/C++/.cmm) we register them as File nodes with null text — kept whole when a
        // kept unit includes them, copied verbatim by the emitter. C# has no such closure, so it's exempt.
        var closureLang = lang is "c" or "cpp" or "cmm";
        // One stat per file: capture every source file's size ONCE here (rel path -> bytes) and reuse the map
        // for big-file detection now and the final size accounting later, instead of stat'ing every file twice
        // (a full extra pass of syscalls over the whole tree — noticeable on a large/network source root).
        // Content-aware skip: a large, macro-DENSE header (chip/register/IO definitions — thousands of
        // #define constants) is enormously expensive to parse (giant AST + one graph node per #define + retained
        // text) yet yields almost nothing useful, because headers are kept WHOLE anyway (never intra-file pruned).
        // A real product tree's transitively-included register headers (5-16 MB each) sit UNDER --max-parse-bytes
        // and drove a real run to ~20 GB / 70+ min in the parser (eval #14). So detect them by a cheap prefix
        // sample and route them to the SAME keep-whole path as oversized files — no parse, no node explosion, no
        // retained text, stream-copied at emit. Sound: the header is still kept whole via include-closure. This is
        // automatic (no --max-parse-bytes tuning); the byte cap remains only as an explicit escape hatch.
        const long AutoSkipMinBytes = CodeCarver.Core.Preprocess.MacroDensity.MinBytesToSample; // only sample big files — cheap
        var sizeByRel = new Dictionary<string, long>(StringComparer.Ordinal);
        var fullByRel = new Dictionary<string, string>(StringComparer.Ordinal); // rel -> on-disk path (for the lazy reader)
        var parseRels = new List<string>(paths.Count);                          // every source rel, in walk order
        long originalBytes = 0;
        var bigFiles = new List<(string Rel, long Bytes)>();       // > --max-parse-bytes
        var denseFiles = new List<(string Rel, long Bytes)>();     // macro-dense, auto-skipped
        foreach (var p in paths)
        {
            var rel = Path.GetRelativePath(dir, p).Replace('\\', '/');
            long len;
            try { len = new FileInfo(p).Length; } catch { len = 0; } // a vanished/locked file: size 0, still tracked
            sizeByRel[rel] = len;
            fullByRel[rel] = p;
            parseRels.Add(rel);
            originalBytes += len;
            if (closureLang && len > maxParseBytes) bigFiles.Add((rel, len));
            else if (closureLang && len >= AutoSkipMinBytes && CodeCarver.Core.Preprocess.MacroDensity.IsMacroDenseHeader(p)) denseFiles.Add((rel, len));
        }
        // Both populations skip the parser and are kept whole via include-closure.
        var skipParse = new HashSet<string>(bigFiles.Select(b => b.Rel).Concat(denseFiles.Select(d => d.Rel)),
                                            StringComparer.Ordinal);

        // STREAMING ingestion: instead of reading the whole tree's text into a list up front (peak memory = every
        // source byte at once), we read each file ON DEMAND via ReadRel and release it before the next. Returns ""
        // for a skip/keep-whole file (big/dense) — the front-end registers a File node and copies it verbatim,
        // never parsing it. A file that can't be read is ALSO kept whole (empty text) rather than silently dropped
        // — the sounder choice; the emitter's copy is best-effort so an unreadable kept file can't crash the emit.
        // ReadRel may be called more than once per file (a scope-macro pre-pass, the parse pass, include scans);
        // the OS file cache serves the re-reads, so peak memory — not I/O — is what this trades for.
        var readErrors = 0;
        var readWarned = new HashSet<string>(StringComparer.Ordinal);
        string ReadRel(string rel)
        {
            if (skipParse.Contains(rel)) return "";
            if (!fullByRel.TryGetValue(rel, out var full)) return "";
            try { return File.ReadAllText(full); }
            catch (Exception ex)   // an unreadable/locked/odd file must not sink the whole run
            {
                if (readWarned.Add(rel))
                {
                    readErrors++;
                    if (readErrors <= 12) err.WriteLine($"  warn    : could not read {rel} ({ex.GetType().Name}) — kept whole, not parsed");
                }
                return "";
            }
        }

        // Reference-only includes: local #included files with a NON-source extension (.inc/.def/generated
        // tables) that are textually part of a .c but which we don't parse as C. A function/global called only
        // from such a table (LLVM-style GenDisassemblerTables.inc) would otherwise be dropped and left dangling
        // once the emitter copies the include. Gather them (recursively) so the front-end keeps what they name.
        var refIncludes = new List<(string Rel, string Text)>();
        if (closureLang && lang is "c" or "cpp")
        {
            var rootFull = Path.GetFullPath(dir);
            var have = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gathered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var incRe = new System.Text.RegularExpressions.Regex("^\\s*#\\s*include\\s+\"([^\"]+)\"",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            // Search dirs for non-sibling resolution: the -I/-isystem dirs from EVERY --build-log (the real build
            // finds an .inc via -I from ANOTHER subdirectory — resolving only beside the includer dropped it
            // silently and the carved tree wouldn't compile: eval-#4 BUG 1). Reuses the already-parsed buildCmds.
            //
            // CRITICAL: resolve each command's -I against THAT COMMAND'S working directory (compile_commands
            // 'directory', or a text log's leading `cd`), NOT the carve root. A relative `-Icfg` from
            // proj/src/app means proj/src/app/cfg — resolving it against the carve root would silently pick a
            // same-named proj/cfg and emit the WRONG include, making a DB-supplied carve LESS sound than the
            // basename fallback (eval-#9 HIGH). An absolute 'directory' (the JSON-DB norm) is used as-is; a
            // relative or "." directory is taken under the carve root. Out-of-tree resolutions are dropped by
            // the in-tree check in TryCand below, falling back to the (sound, warning) basename search.
            var searchDirs = new List<string>();
            foreach (var c in buildCmds)
            {
                string baseDir;
                try { baseDir = Path.IsPathFullyQualified(c.Directory) ? c.Directory : Path.GetFullPath(Path.Combine(dir, c.Directory)); }
                catch { baseDir = Path.GetFullPath(dir); }
                foreach (var incDir in c.Includes)
                    try { var f = Path.GetFullPath(Path.Combine(baseDir, incDir)); if (Directory.Exists(f)) searchDirs.Add(f); } catch { }
            }
            searchDirs = searchDirs.Distinct().ToList();

            // Last-resort basename index of EVERY file in the tree (names only — cheap even on a huge tree),
            // built lazily on the first include that neither a sibling nor a -I dir resolves.
            Dictionary<string, List<string>>? byBase = null;
            Dictionary<string, List<string>> BaseIndex()
            {
                if (byBase is null)
                {
                    byBase = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in CodeCarver.Core.Util.SourceWalk.Files(rootFull))
                    {
                        var bn = Path.GetFileName(f);
                        if (!byBase.TryGetValue(bn, out var l)) byBase[bn] = l = new List<string>();
                        l.Add(f);
                    }
                }
                return byBase;
            }

            var unresolved = new HashSet<(string, string)>();
            // Stream the include scan too: the queue holds PATHS, not text — each includer is read, scanned, and
            // released before the next, so this phase also never holds the whole tree in memory. Seeds are the
            // parsed source files (skip/keep-whole files aren't scanned for includes, matching the prior behavior).
            var queue = new Queue<string>();
            foreach (var rel in parseRels)
                if (!skipParse.Contains(rel)) queue.Enqueue(fullByRel[rel]);
            while (queue.Count > 0)
            {
                var fromFull = queue.Dequeue();
                string text;
                try { text = File.ReadAllText(fromFull); } catch { continue; }
                var fromDir = Path.GetDirectoryName(fromFull) ?? dir;
                foreach (System.Text.RegularExpressions.Match m in incRe.Matches(text))
                {
                    var inc = m.Groups[1].Value;
                    if (exts.Any(e => inc.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue; // .h: parsed already

                    // Resolve in order: beside the includer, then each -I dir, then (last resort) by basename
                    // anywhere in the tree. Over-approximate: take every in-tree match (sound for building).
                    var cands = new List<string>();
                    void TryCand(string cand)
                    {
                        try { var f = Path.GetFullPath(cand); if (f.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(f)) cands.Add(f); } catch { }
                    }
                    TryCand(Path.Combine(fromDir, inc));
                    foreach (var sd in searchDirs) TryCand(Path.Combine(sd, inc));
                    if (cands.Count == 0 && BaseIndex().TryGetValue(Path.GetFileName(inc), out var hits))
                    {
                        cands.AddRange(hits);
                        // The basename fallback is over-approximate: if a name occurs in several dirs it emits
                        // ALL of them (sound for building, but a source of bloat). Note it so an over-keep is
                        // attributable -- supply -I via --build-log to resolve it exactly.
                        if (hits.Count > 1 && unresolved.Add(("ambig:" + Path.GetFileName(inc), inc)))
                            err.WriteLine($"  warn    : #include \"{inc}\" matched {hits.Count} files by basename "
                                                    + "(kept all — sound but may over-keep; supply -I via --build-log to disambiguate)");
                    }

                    if (cands.Count == 0)
                    {
                        var fromRel = Path.GetRelativePath(dir, fromFull).Replace('\\', '/');
                        if (unresolved.Add((fromRel, inc)))
                            err.WriteLine($"  warn    : {fromRel}: #include \"{inc}\" resolved to no file in the tree — "
                                                    + "the carved tree may not compile (supply -I via --build-log)");
                        continue;
                    }
                    foreach (var target in cands)
                    {
                        if (have.Contains(target) || !gathered.Add(target) || !File.Exists(target)) continue;
                        if (new FileInfo(target).Length > maxParseBytes) continue;
                        var itext = File.ReadAllText(target);
                        refIncludes.Add((Path.GetRelativePath(dir, target).Replace('\\', '/'), itext));
                        queue.Enqueue(target); // an .inc may include another — re-read on dequeue (cache-hot)
                    }
                }
            }
        }

        using ICarveFrontEnd fe = lang switch
        {
            "cpp" => new CppFrontEnd(),
            "csharp" or "cs" => new CSharpFrontEnd(),
            "cmm" => new CmmFrontEnd(),
            _ => new CFrontEnd(),
        };

        // eval-#11 fix #1: a logged .c that is #included by ANOTHER TU (unity/jumbo, or "#include the .c") is
        // compiled under that includer's config too, so its own per-file define set is unsound. Detect such
        // files (any source-extension local include whose basename matches a logged file) and route them to the
        // universal set via includedCFiles. Over-approximate by basename = sound (universal keeps both branches).
        if (perFileSpecs.Count > 0)
        {
            var srcExts = new[] { ".c", ".cc", ".cpp", ".cxx", ".c++" };
            var keysByBase = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in perFileSpecs.Keys)
            {
                var b = Path.GetFileName(k);
                if (!keysByBase.TryGetValue(b, out var l)) keysByBase[b] = l = new List<string>();
                l.Add(k);
            }
            var incRe = new System.Text.RegularExpressions.Regex("^\\s*#\\s*include\\s+\"([^\"]+)\"",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            foreach (var rel in parseRels)
            {
                var text = ReadRel(rel);
                if (text.Length == 0 || !text.Contains("#include", StringComparison.Ordinal)) continue;
                foreach (System.Text.RegularExpressions.Match m in incRe.Matches(text))
                {
                    var incBase = Path.GetFileName(m.Groups[1].Value);
                    if (!srcExts.Any(e => incBase.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    if (keysByBase.TryGetValue(incBase, out var keys))
                        foreach (var k in keys) if (!string.Equals(k, rel, StringComparison.OrdinalIgnoreCase)) includedCFiles.Add(k);
                }
            }
            if (includedCFiles.Count > 0)
                err.WriteLine($"  build   : {includedCFiles.Count} logged .c file(s) are #included by another TU "
                    + "-> using the universal #ifdef config for them (unity/jumbo-safe)");
        }

        if (fe is TreeSitterFrontEnd tsfe)
        {
            if (parseTimeoutMs is not null) tsfe.ParseBudgetMs = parseTimeoutMs.Value;
            if (maxSymbolsPerFile is not null) tsfe.PerFileSymbolBudget = maxSymbolsPerFile.Value;
            if (refIncludes.Count > 0) tsfe.ReferenceOnlyIncludes = refIncludes;
            if (perFileDefines is not null) tsfe.PerFileDefines = perFileDefines; // per-TU #ifdef config from the build log
        }
        // Opt-in phase timing (CODECARVER_TIMING=1) to stderr — used for the performance work.
        var _tsw = System.Diagnostics.Stopwatch.StartNew();
        var _timing = Environment.GetEnvironmentVariable("CODECARVER_TIMING") is not null;
        void Mark(string phase)
        {
            if (_timing) err.WriteLine($"  timing  : {phase,-14} {_tsw.ElapsedMilliseconds,7} ms");
            diag.Event($"phase {phase}: {_tsw.ElapsedMilliseconds} ms"); // breadcrumb for the diagnostic package
            _tsw.Restart();
        }
        Mark("input+refscan"); // time spent gathering inputs + reference includes above

        // Sign of life for a large tree so a multi-minute analyze isn't a silent black box (and you can see
        // how far it got if it's interrupted). CODECARVER_TIMING=1 adds a per-phase + slow-file breakdown.
        // Byte total from file SIZES (not held text) — the parsed files are those not skipped/oversized.
        var scanBytes = parseRels.Where(r => !skipParse.Contains(r)).Sum(r => sizeByRel.TryGetValue(r, out var s) ? s : 0L);
        var scanFiles = parseRels.Count(r => !skipParse.Contains(r));
        if (scanFiles > 500 || scanBytes > 50_000_000)
            err.WriteLine($"  scanning: {scanFiles:N0} files (~{scanBytes / 1_000_000.0:N0} MB)"
                                    + (bigFiles.Count > 0 ? $" + {bigFiles.Count} big-file(s) kept whole" : "")
                                    + (denseFiles.Count > 0 ? $" + {denseFiles.Count} dense-header(s) kept whole" : "") + " -- analyzing...");

        // Live, self-calibrating parse ETA for a large tree. Parsing dominates the run and scales ~linearly with
        // bytes, so measured throughput (bytesDone/elapsed) x known remaining bytes = a real, refining estimate —
        // not a guess. Printed to stderr ~every 3 s so it never pollutes --dump-spans / manifest stdout.
        if (fe is TreeSitterFrontEnd tsp && (scanFiles > 500 || scanBytes > 50_000_000))
        {
            var psw = System.Diagnostics.Stopwatch.StartNew();
            var lastPrint = 0.0;                                // 0 => first ETA only after a ~3 s warmup (calibrated rate)
            tsp.OnParseProgress = (filesDone, filesTotal, bytesDone, bytesTotal) =>
            {
                var el = psw.Elapsed.TotalSeconds;
                var last = filesDone >= filesTotal;
                if (bytesDone <= 0 || el <= 0.001) return;
                if (!last && el - lastPrint < 3.0) return;     // warmup + throttle; always emit the final 100% line
                lastPrint = el;
                var rate = bytesDone / el;                     // bytes/sec, measured on THIS run
                var etaSec = rate > 0 ? (bytesTotal - bytesDone) / rate : -1;
                err.WriteLine($"  parsing : {100.0 * bytesDone / bytesTotal,3:N0}% "
                    + $"({filesDone:N0}/{filesTotal:N0} files, {rate / 1_000_000.0:N1} MB/s) -- ETA {FormatEta(etaSec)}");
            };
        }
        var graph = fe.BuildGraph(parseRels, ReadRel, defines, closedWorld);
        Mark("build-graph");
        if (readErrors > 12) err.WriteLine($"  warn    : (+{readErrors - 12} more unreadable files kept whole)");

        // Non-fatal diagnostics (kept-whole fragments, unresolved/ambiguous .cmm DO). Surfacing these avoids
        // the "silent 100% smaller" trap. Capped so a tree with hundreds of dynamic DOs doesn't flood output.
        if (fe.Warnings.Count > 0)
        {
            const int cap = 12;
            foreach (var w in fe.Warnings.Take(cap)) err.WriteLine("  warn    : " + w);
            if (fe.Warnings.Count > cap) err.WriteLine($"  warn    : (+{fe.Warnings.Count - cap} more warnings)");
        }
        foreach (var w in fe.Warnings) diag.Warn(w); // full set (uncapped) into the diagnostic package

        var explicitRoots = new ExplicitRootProvider(symbols: roots).Discover(graph).ToList();
        // Per-root resolution. A firmware root set is a long hand-maintained list of ISRs/exported API
        // ("the whole game", WORKREPO.md §0); a SINGLE typo among valid names would otherwise carve that
        // symbol away silently and look like a clean success plus a bigger size win. So report every
        // requested name that resolved to nothing, and — with --strict-roots — fail the run.
        var resolvedNames = new HashSet<string>(
            explicitRoots.Where(r => r.Kind == RootKind.ExplicitSymbol && r.Note is not null).Select(r => r.Note!),
            StringComparer.Ordinal);
        var unresolvedRoots = roots.Where(r => !resolvedNames.Contains(r)).ToList();
        if (roots.Length > 0 && explicitRoots.Count == 0)
        {
            err.WriteLine($"none of the requested roots were found as symbols: {string.Join(", ", roots)}");
            return 1;
        }
        if (unresolvedRoots.Count > 0)
        {
            foreach (var u in unresolvedRoots)
                err.WriteLine($"  warn    : requested root '{u}' was NOT found as a symbol — nothing rooted for it " +
                                        "(typo? macro-defined signature? excluded/other-variant file?)");
            if (strictRoots)
            {
                err.WriteLine($"strict-roots: {unresolvedRoots.Count} of {roots.Length} requested roots unresolved — failing (drop --strict-roots to proceed anyway).");
                return 1;
            }
        }
        // Implicit roots (constructor/used/init-array) are ALWAYS added: the runtime/linker keep them
        // regardless of any call, so a from-main closure that dropped them would ship a broken image.
        var implicitRoots = new AttributeRootProvider().Discover(graph).ToList();

        // Guarded read for the root-discovery scans below. Like ReadRel, an unreadable/locked/vanished file must
        // not sink the run: a bare File.ReadAllText inside these LAZY projections would throw mid-Discover (after
        // the graph was already built) on a file locked between the walk and the read — routine on a live tree.
        string SafeRead(string full)
        {
            try { return File.ReadAllText(full); }
            catch (Exception ex)
            {
                if (readWarned.Add(full)) err.WriteLine($"  warn    : could not read {Path.GetFileName(full)} for root scan ({ex.GetType().Name}) — skipped");
                return "";
            }
        }

        // Assembly startup (.s/.S) references C handlers by name (vector table `.word Handler`) — root them.
        var asmRoots = new List<Root>();
        if (lang is "c" or "cpp")
        {
            var asmPaths = CodeCarver.Core.Util.SourceWalk.Files(dir)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".s" or ".asm")
                .Where(p => excludeDirs.Count == 0 ||
                            !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase)))
                .Where(p => new FileInfo(p).Length <= maxParseBytes)
                .ToList(); // paths only (cheap); texts are streamed one file at a time below, never all held at once
            if (asmPaths.Count > 0)
                asmRoots = new AsmReferenceRootProvider(asmPaths.Select(SafeRead)).Discover(graph).ToList();
        }

        // A symbol placed in a custom section that the linker script KEEP()s (initcall / registration
        // tables) is collected by the linker, never called — root it so a from-main closure can't drop it.
        // Gated on a linker script actually being present, so non-embedded trees pay nothing.
        var sectionRoots = new List<Root>();
        if (lang is "c" or "cpp")
        {
            bool Included(string p) => excludeDirs.Count == 0 ||
                !excludeDirs.Any(x => p.Replace('\\', '/').Contains("/" + x + "/", StringComparison.OrdinalIgnoreCase));
            var linkerScripts = CodeCarver.Core.Util.SourceWalk.Files(dir)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".ld" or ".lds" or ".ldscript")
                .Where(Included).Where(p => new FileInfo(p).Length <= maxParseBytes)
                .Select(SafeRead).ToList();
            if (linkerScripts.Count > 0)
            {
                // Stream the source text one file at a time — the provider consumes this lazily; a .ToList() here
                // materialised the whole tree's text at once (~1.6 GB UTF-16 on the death corpus) and OOM'd after
                // the graph was already built. Reuse the SAME filter + hardened, cache-hot reader as the graph
                // scan: skip the big/dense headers (skipParse) — ReadRel returns "" for them, they carry no
                // section attributes, and re-reading + regex-scanning them here was a second full-tree pass over
                // exactly the multi-MB files skipParse exists to avoid (the ~20 GB / 70+ min blowup shape).
                var srcTexts = parseRels.Where(r => !skipParse.Contains(r)).Select(ReadRel);
                sectionRoots = new LinkerSectionRootProvider(srcTexts, linkerScripts).Discover(graph).ToList();
            }
        }

        // C++ constructors run on every instantiation (untraceable) and can't be pruned, so root them: the
        // constructor AND whatever it calls in its init-list/body must survive (a real pugixml dangling bug).
        var ctorRoots = lang is "cpp" ? new ConstructorRootProvider().Discover(graph).ToList() : new List<Root>();

        // A file whose extraction threw was kept whole (not analysed) — root it so its code is emitted intact
        // rather than silently dropped. Sound over-approximation for the "couldn't parse it" case.
        var forceKeep = fe is TreeSitterFrontEnd tsk ? tsk.ForceKeepFiles : Array.Empty<string>();
        var forceKeepRoots = forceKeep.Count > 0
            ? new ExplicitRootProvider(files: forceKeep).Discover(graph).ToList() : new List<Root>();

        // Runtime trace (functions a real run executed): root them so the carve is guaranteed to keep what ran,
        // including dynamic-dispatch / function-pointer edges static reachability can't see. Trace names are
        // machine-generated (many external/libc), so they do NOT go through the --strict-roots per-root warnings;
        // instead the summary reports how many resolved in-scope.
        var traceRoots = new List<Root>();
        var traceTotal = 0;
        if (traceList.Count > 0)
        {
            System.Text.RegularExpressions.Regex? pat = null;
            if (traceFormat is not null)
                // A user-supplied pattern is applied to every line of a possibly huge trace; cap each match so a
                // pathological (catastrophic-backtracking) pattern surfaces as a clean error rather than hanging.
                try { pat = new System.Text.RegularExpressions.Regex(traceFormat, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { err.WriteLine($"--trace-format is not a valid regex: {ex.Message}"); return 2; }
            // Union the function names across every trace (the files were validated/pruned up front).
            var traceNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tp in traceList)
            {
                if (!File.Exists(tp)) continue; // defensive; missing ones were already handled
                try { foreach (var n in TraceFile.FunctionNames(TraceFile.Parse(File.ReadAllText(tp), pat))) traceNames.Add(n); }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                { err.WriteLine("--trace-format took too long to match a trace line (catastrophic backtracking?) — simplify the pattern"); return 2; }
            }
            traceTotal = traceNames.Count;
            traceRoots = new ExplicitRootProvider(symbols: traceNames).Discover(graph).ToList();
            if (traceTotal == 0)
                err.WriteLine("  warn    : --trace produced 0 function names (does --trace-format have a named 'fn' group?)");
        }

        // File-access traces: the files the OS actually opened under the repo during a real build/run. The OS
        // reports the CONCRETE path regardless of how it was computed, so this captures the loader/orchestration +
        // data layer (CMM scripts, loaded binaries, data tables) that a function trace can't see and static
        // analysis can't resolve (&var paths). Every candidate path is mapped to a carve-relative file and kept
        // only if it is UNDER the carve root and exists — that selective filter turns a tolerant extraction
        // (ProcMon CSV / strace / plain list) into "repo files genuinely touched", dropping all the process-name /
        // out-of-tree noise. Observed CODE files become file-level roots (keep the file + its closure); the whole
        // observed set is threaded into the emitter (never garbage-prune an observed file) and the report
        // (attribution + flag the kept-but-unobserved infra as drop-candidates).
        var observedRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileTraceRoots = new List<Root>();
        var observedExternalSrc = 0;   // source-like files read OUTSIDE the carve root (a real missing dependency)
        if (buildFileTraces.Count + runFileTraces.Count > 0)
        {
            System.Text.RegularExpressions.Regex? fpat = null;
            if (fileTraceFormat is not null)
                try { fpat = new System.Text.RegularExpressions.Regex(fileTraceFormat, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { err.WriteLine($"--file-trace-format is not a valid regex: {ex.Message}"); return 2; }

            var rootFull = Path.GetFullPath(dir);
            void Ingest(List<string> traces)
            {
                foreach (var tp in traces)
                {
                    if (!File.Exists(tp)) continue; // defensive; missing ones were already handled up front
                    string content;
                    try { content = File.ReadAllText(tp); }
                    catch (Exception ex) { err.WriteLine($"  warn    : could not read file-trace {Path.GetFileName(tp)} ({ex.GetType().Name}) — skipped"); continue; }
                    IReadOnlyCollection<string> cands;
                    try { cands = FileAccessTrace.Paths(content, fpat); }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                    { err.WriteLine("--file-trace-format took too long to match a line (catastrophic backtracking?) — simplify the pattern"); cands = Array.Empty<string>(); }
                    foreach (var cand in cands)
                    {
                        string full;
                        try { full = Path.IsPathFullyQualified(cand) ? Path.GetFullPath(cand) : Path.GetFullPath(Path.Combine(rootFull, cand)); }
                        catch { continue; } // not a usable path token (noise)
                        if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                            observedRel.Add(Path.GetRelativePath(rootFull, full).Replace('\\', '/'));
                        else if (exts.Any(e => cand.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                            observedExternalSrc++; // a real source file, but outside the carve root — slice is missing it
                    }
                }
            }
            Ingest(buildFileTraces);
            Ingest(runFileTraces);

            var observedCode = observedRel.Where(r => exts.Any(e => r.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToList();
            if (observedCode.Count > 0)
                fileTraceRoots = new ExplicitRootProvider(files: observedCode).Discover(graph).ToList();
            err.WriteLine($"  files   : {observedRel.Count} observed in-tree from {buildFileTraces.Count} build + {runFileTraces.Count} run file-trace(s)"
                + $" ({observedCode.Count} code rooted)"
                + (observedExternalSrc > 0 ? $"; {observedExternalSrc} source file(s) read OUTSIDE the carve root (missing dependency?)" : ""));
        }

        var rootSet = explicitRoots.Concat(implicitRoots).Concat(asmRoots).Concat(sectionRoots)
                                   .Concat(ctorRoots).Concat(forceKeepRoots).Concat(traceRoots).Concat(fileTraceRoots).ToList();
        if (rootSet.Count == 0)
        {
            err.WriteLine("no roots to carve from: name entry symbols with --roots");
            return 1;
        }

        Mark("roots");
        var plan = ReachabilityEngine.Compute(graph, rootSet);
        Mark("reachability");
        var s = plan.Stats;

        if (dumpSpans)
        {
            foreach (var n in graph.Nodes
                         .Where(n => n.Kind is NodeKind.Function or NodeKind.Global or NodeKind.Macro)
                         .OrderBy(n => n.FilePath, StringComparer.Ordinal)
                         .ThenBy(n => n.Span.StartLine))
                @out.WriteLine($"  {(plan.IsKept(n.Id) ? "KEEP" : "drop")} {n.Kind} {n.FilePath}:{n.Span}\t{n.Name}");
            return 0;
        }

        // --why <symbol>: explain the keep-chain (or that it was carved) for a named symbol — for debugging
        // a carve against a real tree ("why is this huge thing still here?" / "why did this get dropped?").
        if (whySymbol is not null)
        {
            var matches = graph.Nodes.Where(n => n.Kind != NodeKind.File && n.Name == whySymbol).ToList();
            if (matches.Count == 0)
            {
                err.WriteLine($"no symbol named '{whySymbol}' was found");
                return 1;
            }
            foreach (var n in matches.OrderBy(n => n.FilePath, StringComparer.Ordinal).ThenBy(n => n.Span.StartLine))
                @out.WriteLine($"  {n.Kind} {n.Name} @ {n.FilePath}:{n.Span}\n    {plan.Explain(n.Id)}");
            return 0;
        }

        // Size accounting uses the sizeByRel map + originalBytes computed once during the input scan above
        // (the whole scanned source vs. what the carve keeps — the headline number). Support-file bytes are
        // added to originalBytes below so verbatim-copied .ld/.s are delta-neutral.

        @out.WriteLine($"CodeCarver {Version()} — carve of {dir}");
        // Show what actually rooted; call out unresolved names inline so a partial resolution can't read as
        // a clean success (see the per-root warnings above).
        @out.WriteLine($"  roots   : {string.Join(", ", roots.Where(resolvedNames.Contains))}"
                          + (unresolvedRoots.Count > 0 ? $"   [{unresolvedRoots.Count} UNRESOLVED: {string.Join(", ", unresolvedRoots)}]" : ""));
        if (implicitRoots.Count > 0)
            @out.WriteLine($"  implicit: {implicitRoots.Count} constructor/used/init-array symbol(s) auto-kept: "
                              + Summarize(implicitRoots.Select(r => r.Note ?? r.Node.ToString()).Distinct().ToList()));
        if (asmRoots.Count > 0)
            @out.WriteLine($"  asm     : {asmRoots.Count} symbol(s) referenced from .s startup auto-kept: "
                              + Summarize(asmRoots.Select(r => r.Note ?? r.Node.ToString()).Distinct().ToList()));
        if (sectionRoots.Count > 0)
            @out.WriteLine($"  section : {sectionRoots.Count} symbol(s) in linker KEEP()'d section(s) auto-kept: "
                              + Summarize(sectionRoots.Select(r => r.Note ?? r.Node.ToString()).Distinct().ToList()));
        if (traceList.Count > 0)
        {
            var traceResolved = traceRoots.Select(r => r.Note).Where(n => n is not null).Distinct().Count();
            @out.WriteLine($"  trace   : {traceTotal} function(s) from {traceList.Count} trace(s) rooted; {traceResolved} resolved in-scope"
                              + (traceTotal > traceResolved ? $", {traceTotal - traceResolved} not found (external/inlined/not captured)" : ""));
        }
        if (defines is not null)
            @out.WriteLine($"  config  : {defineSpecs.Distinct().Count()} define(s), #ifdef resolution ON" +
                              (closedWorld ? " (closed-world: absent macros treated as undefined)" : " (open-world: unknown branches kept)"));
        @out.WriteLine($"  nodes   : {s.ReachedNodes}/{s.TotalNodes} kept ({s.NodeKeepRatio:P0}), {s.DroppedNodes} carved");
        @out.WriteLine($"  files   : {s.KeptFiles}/{s.TotalFiles} kept, {s.DroppedFiles} dropped");
        if (bigFiles.Count > 0)
            @out.WriteLine($"  big     : {bigFiles.Count} file(s) > {maxParseBytes:N0} B not parsed (kept whole via #include-closure): "
                              + Summarize(bigFiles.OrderByDescending(b => b.Bytes).Select(b => $"{b.Rel} ({b.Bytes:N0} B)").ToList()));
        if (denseFiles.Count > 0)
            @out.WriteLine($"  dense   : {denseFiles.Count} macro-dense header(s) auto-kept-whole (skipped parse — would explode parser memory): "
                              + Summarize(denseFiles.OrderByDescending(d => d.Bytes).Select(d => $"{d.Rel} ({d.Bytes:N0} B)").ToList()));
        var budgetKept = fe is TreeSitterFrontEnd tsb ? tsb.SymbolBudgetKeptWhole : Array.Empty<(string Path, int Symbols)>();
        if (budgetKept.Count > 0)
            @out.WriteLine($"  budget  : {budgetKept.Count} file(s) over the per-file symbol budget auto-kept-whole (would explode the graph — a shape the dense/big skips missed): "
                              + Summarize(budgetKept.OrderByDescending(b => b.Symbols).Select(b => $"{b.Path} ({b.Symbols:N0} symbols)").ToList()));
        if (plan.DroppedFiles.Count > 0)
            @out.WriteLine("  dropped : " + Summarize(plan.DroppedFiles));

        // --- Soundness check (plan-based; identical for every stage): a KEPT fn must not call an in-scope DROPPED
        // fn (it wouldn't link). Always on now; folded into the normal output + each stage's report. ---
        var verifyFailed = false;
        if (fe is TreeSitterFrontEnd tsv)
        {
            var violations = SoundnessCheck.KeptCallingDropped(graph, plan, tsv.CallSites);
            if (violations.Count == 0)
                @out.WriteLine("  verify  : OK — every in-scope callee of a kept function is kept");
            else
            {
                verifyFailed = true;
                @out.WriteLine($"  verify  : {violations.Count} UNSOUND call(s) — a kept function calls an in-scope function that was carved out:");
                foreach (var v in violations.Take(20)) @out.WriteLine($"            {v.Caller}() -> {v.Callee}()  [{v.File}]");
                if (violations.Count > 20) @out.WriteLine($"            (+{violations.Count - 20} more)");
            }
        }
        else
            @out.WriteLine($"  verify  : (soundness check is C/C++ only; skipped for language '{lang}')");
        @out.WriteLine($"  world   : {cv.WorldReason}");

        void WriteArtifact(string path, string content, string what)
        {
            try { File.WriteAllText(path, content); @out.WriteLine($"  {what,-8}: {path}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or System.Security.SecurityException)
            { err.WriteLine($"  warn    : could not write {what} '{path}' ({ex.GetType().Name}: {ex.Message})"); }
        }

        // Analysis-only (no carved tree): compute the decision and write report + manifest, skipping the (possibly
        // huge) emit. The WORKREPO "dry run" and the ground-truth oracle use this to inspect kept/dropped fast.
        if (cv.AnalysisOnly)
        {
            var ccDir = Path.Combine(outputDirectory, "codecarver");
            Directory.CreateDirectory(ccDir);
            @out.WriteLine("  mode    : analysis only — plan + report + manifest, no carved tree emitted");
            var carvedCode = plan.KeptFiles.Sum(f => sizeByRel.TryGetValue(f, out var b) ? b : 0);
            @out.WriteLine($"  size    : {originalBytes:N0} B scanned, {carvedCode:N0} B in kept code files");
            @out.WriteLine($"  buckets : {plan.KeptFiles.Count:N0} reachable-code + {plan.DroppedFiles.Count:N0} dead-code file(s) "
                + "(infrastructure + include closure are enumerated only when emitting)");
            var report = CarveReport.Render(new CarveReport.Inputs(
                SourceRoot: dir, Roots: roots, BuildRequired: plan.KeptFiles, KeptCode: plan.KeptFiles,
                RemovedDeadCode: plan.DroppedFiles, Infrastructure: Array.Empty<string>(), ExcludedDirs: excludeDirs,
                CodeBytesBefore: originalBytes, CodeBytesAfter: carvedCode, InfraBytes: 0, InfraEnumerated: false,
                RemovedGarbage: Array.Empty<string>(), GarbageBytes: 0, Observed: observedRel.ToList()));
            WriteArtifact(Path.Combine(ccDir, "report.txt"), report, "report");
            var m = new
            {
                codecarverVersion = Version(), root = dir, roots, lang, analysisOnly = true,
                defines = defineSpecs.Distinct().ToArray(), closedWorld,
                stats = new { s.TotalNodes, s.ReachedNodes, s.DroppedNodes, s.TotalFiles, s.KeptFiles, s.DroppedFiles },
                keptFiles = plan.KeptFiles, droppedFiles = plan.DroppedFiles,
                observedFiles = observedRel.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            };
            WriteArtifact(Path.Combine(ccDir, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "manifest");
            Mark("analyze");
            diag.Set("analysisOnly", true);
            diag.Set("totalNodes", s.TotalNodes); diag.Set("keptFiles", s.KeptFiles); diag.Set("droppedFiles", s.DroppedFiles);
            diag.Set("verifyFailed", verifyFailed); diag.Event("run complete");
            return verifyFailed ? 3 : 0;
        }

        // --- Emit each stage. The graph + reachability plan are SHARED across stages; only the emit granularity
        // (carveSourceFileContents / carveHeaderFileContents) and the output subdir differ. Each stage writes a
        // complete buildable project under <outputDirectory>/[<stage>/]carved plus report/manifest/resolved-config
        // under the sibling codecarver/. ---
        var originalCodeBytes = originalBytes;  // code-only base; do NOT mutate across stages
        var configText = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        foreach (var stage in cv.Stages)
        {
            prune = stage.CarveSourceFileContents;
            pruneHeaders = stage.CarveHeaderFileContents;
            var baseDir = stage.Name.Length == 0 ? outputDirectory : Path.Combine(outputDirectory, stage.Name);
            var outDir = Path.Combine(baseDir, "carved");
            var ccDir = Path.Combine(baseDir, "codecarver");
            @out.WriteLine($"  stage   : {(stage.Name.Length == 0 ? "(single)" : stage.Name)}  "
                + $"[source-contents={(prune ? "carved" : "whole")}, header-contents={(pruneHeaders ? "carved" : "whole")}]");

            // Crash-safe: stage into a private dir, promote atomically only after every step succeeds.
            using var staged = StagedOutput.Begin(outDir);
            var stageDir = staged.Dir;
            Console.CancelKeyPress += (_, _) => { try { staged.Dispose(); } catch { } };

            var res = prune ? FileTreeEmitter.EmitPruned(plan, graph, dir, stageDir) : FileTreeEmitter.Emit(plan, dir, stageDir);
            var carvedBytes = res.BytesWritten;
            var buildRequiredFiles = res.Written;
            @out.WriteLine($"  emitted : {res.FilesWritten} files -> {outDir}  [{(prune ? "intra-file (unused functions removed)" : "file-level (whole kept files)")}]");
            if (prune) @out.WriteLine("  note    : carveSourceFileContents is EXPERIMENTAL — always build-verify.");

            if (pruneHeaders && lang is "c" or "cpp")
            {
                var keptBig = bigFiles.Select(b => b.Rel).Concat(denseFiles.Select(d => d.Rel)).Where(plan.KeptFiles.Contains).ToList();
                if (keptBig.Count > 0)
                {
                    var hc = HeaderCarver.Carve(stageDir, keptBig);
                    carvedBytes -= hc.BytesBefore - hc.BytesAfter;
                    var hpct = hc.BytesBefore > 0 ? (double)(hc.BytesBefore - hc.BytesAfter) / hc.BytesBefore : 0;
                    @out.WriteLine($"  headers : {keptBig.Count} big header(s) carved — {hc.DefinesKept:N0} kept, {hc.DefinesDropped:N0} dropped; "
                        + $"{hc.BytesBefore:N0} B -> {hc.BytesAfter:N0} B ({hpct:P0} smaller)");
                }
            }

            // Keep-by-default: copy every non-code file verbatim so the output is a COMPLETE buildable project
            // (the only omissions are emitted code, proven-dead code, and auto-excluded non-inputs).
            var infra = InfrastructureEmitter.Copy(dir, stageDir, res.Written, plan.DroppedFiles, excludeDirs, auxGlobs, pruneGarbage, observedRel);
            carvedBytes += infra.Bytes;
            var origTotal = originalCodeBytes + infra.Bytes;   // delta-neutral passthrough (both sides)
            if (infra.Count > 0)
                @out.WriteLine($"  passthru: {infra.Count:N0} non-code file(s) copied verbatim ({infra.Bytes:N0} B) — complete buildable project");
            if (infra.Garbage.Count > 0)
                @out.WriteLine($"  excluded: {infra.Garbage.Count:N0} non-input file(s) NOT copied ({infra.GarbageBytes:N0} B) — VCS/scratch/editor (forceKeepFiles to keep)");
            foreach (var w in infra.Warnings) err.WriteLine($"  warn    : {w}");

            try { staged.Promote(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var torn = ex is PromoteFailedException { Torn: true };
                err.WriteLine($"  error   : carve succeeded ({res.FilesWritten} files staged) but writing {outDir} failed "
                    + $"({ex.GetType().Name}: {ex.Message}). " + (torn ? $"{outDir} is PARTIALLY updated — re-run." : $"{outDir} is unchanged.")
                    + " Usual cause: a process holding a file open inside it, or a transient AV/indexer lock.");
                diag.SetFailure(ex);
                return 1;
            }

            var saved = origTotal - carvedBytes;
            var pct = origTotal > 0 ? (double)saved / origTotal : 0;
            @out.WriteLine($"  size    : {origTotal:N0} B -> {carvedBytes:N0} B  ({pct:P0} smaller, saved {saved:N0} B)");
            @out.WriteLine($"  buckets : {buildRequiredFiles.Count:N0} required-to-build + {infra.Count:N0} infrastructure kept, "
                + $"{plan.DroppedFiles.Count:N0} dead-code" + (infra.Garbage.Count > 0 ? $" + {infra.Garbage.Count:N0} auto-excluded" : "") + " file(s) removed");

            // Always write report + manifest + resolved-config into codecarver/.
            Directory.CreateDirectory(ccDir);
            var report = CarveReport.Render(new CarveReport.Inputs(
                SourceRoot: dir, Roots: roots, BuildRequired: buildRequiredFiles, KeptCode: plan.KeptFiles,
                RemovedDeadCode: plan.DroppedFiles, Infrastructure: infra.Files, ExcludedDirs: excludeDirs,
                CodeBytesBefore: origTotal - infra.Bytes, CodeBytesAfter: carvedBytes - infra.Bytes,
                InfraBytes: infra.Bytes, InfraEnumerated: true, RemovedGarbage: infra.Garbage, GarbageBytes: infra.GarbageBytes,
                Observed: observedRel.ToList()));
            WriteArtifact(Path.Combine(ccDir, "report.txt"), report, "report");

            var manifest = new
            {
                codecarverVersion = Version(), root = dir, roots, lang,
                defines = defineSpecs.Distinct().ToArray(), closedWorld,
                stage = stage.Name, carveSourceFileContents = prune, carveHeaderFileContents = pruneHeaders,
                stats = new { s.TotalNodes, s.ReachedNodes, s.DroppedNodes, s.TotalFiles, s.KeptFiles, s.DroppedFiles,
                    originalBytes = origTotal, carvedBytes, savedBytes = saved },
                keptFiles = plan.KeptFiles, droppedFiles = plan.DroppedFiles,
                infrastructureFiles = infra.Files, removedGarbageFiles = infra.Garbage,
                observedFiles = observedRel.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            };
            WriteArtifact(Path.Combine(ccDir, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "manifest");
            WriteArtifact(Path.Combine(ccDir, "resolved-config.toml"),
                $"# CodeCarver resolved config — stage '{stage.Name}'\n# {cv.WorldReason}\n"
                + $"# entryPoints={roots.Length}  languages={string.Join(",", cv.Languages)}  buildLogs={buildLogs.Count}  "
                + $"runTraceFiles={runFileTraces.Count}  runTraceLogs={traceList.Count}\n\n{configText}", "config");
        }
        Mark("emit");

        // --- Diagnostic stats (once; numbers only — feeds an auto-written crash package). ---
        diag.Set("totalNodes", s.TotalNodes);
        diag.Set("keptNodes", s.ReachedNodes);
        diag.Set("totalFiles", s.TotalFiles);
        diag.Set("keptFiles", s.KeptFiles);
        diag.Set("droppedFiles", s.DroppedFiles);
        diag.Set("observedFiles", observedRel.Count);
        diag.Set("rootsUnresolved", unresolvedRoots.Count);
        diag.Set("bigFilesKeptWhole", bigFiles.Count);
        diag.Set("denseHeadersKeptWhole", denseFiles.Count);
        diag.Set("symbolBudgetKeptWhole", budgetKept.Count);
        diag.Set("stages", cv.Stages.Count);
        diag.Set("verifyFailed", verifyFailed);
        diag.Set("exitCode", verifyFailed ? 3 : 0);
        diag.Event("run complete");

        return verifyFailed ? 3 : 0; // non-zero so the soundness check is usable as a CI gate
    }

    // Human ETA from a seconds estimate. "?" when not yet computable (no throughput sample yet).
    static string FormatEta(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "?";
        var s = (long)Math.Round(seconds);
        if (s < 60) return $"~{s}s";
        if (s < 3600) return $"~{s / 60}m {s % 60:00}s";
        return $"~{s / 3600}h {(s % 3600) / 60:00}m";
    }

    static string Summarize(IReadOnlyList<string> files, int max = 12)
        => files.Count <= max
            ? string.Join(", ", files)
            : string.Join(", ", files.Take(max)) + $", … (+{files.Count - max} more)";

    // Reconstruct the command line for the diagnostic package with all VALUES elided — the source path
    // (positional), --out/--build-log/etc. paths, and the --roots symbol names must not ship. Keeps the flag
    // names and structure so a developer can see exactly what was run without any proprietary identifiers.
    // Value-carrying flags: the argument is elided (a path or a symbol list); the flag NAME is kept (which
    // options were used is the useful, non-sensitive signal).
    static string SanitizeCommandLine(string[] a)
    {
        var valueFlags = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            // Only the value-carrying flags the CLI still accepts (inputs/tuning moved to --config, whose path is
            // elided too). The config FILE's contents are never read into the diag package, so no values leak.
            "--roots", "--out", "--config", "--manifest", "--diag", "--report", "--lang", "--why",
        };
        var sb = new System.Text.StringBuilder("carve <source>");
        for (var i = 2; i < a.Length; i++)   // a[0]="carve", a[1]=source dir (already shown as <source>)
        {
            var t = a[i];
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                sb.Append(' ').Append(t);
                if (valueFlags.Contains(t) && i + 1 < a.Length) { sb.Append(" <elided>"); i++; }
            }
            else sb.Append(" <arg>");
        }
        return sb.ToString();
    }

    // Classify the source root for diagnostics WITHOUT leaking the path: local drive (a bare drive letter isn't
    // sensitive), a UNC network share, or a WSL mount. Enough to reason about I/O behavior; nothing identifying.
    static string ClassifyRoot(string d)
    {
        try
        {
            if (d.StartsWith(@"\\", StringComparison.Ordinal) || d.StartsWith("//", StringComparison.Ordinal))
                return "UNC network share";
            var full = System.IO.Path.GetFullPath(d);
            if (full.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase)) return "WSL-mounted drive";
            var root = System.IO.Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? "relative path" : $"local drive {root}";
        }
        catch { return "unclassified"; }
    }

    // The compiler's own --version banner (first non-empty line) — a source-free way to tie a build/config
    // issue to a specific toolchain. Best-effort and BOUNDED: a missing binary or a hang yields null, never a
    // crash or a stuck process (5 s cap, then killed). Only ever run on a compiler the user explicitly named.
    static string? ToolchainVersion(string compiler)
    {
        try
        {
            var exe = File.Exists(compiler) ? Path.GetFullPath(compiler) : compiler; // resolve relative; bare name hits PATH
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--version");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            var errp = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(entireProcessTree: true); } catch { /* ignore */ } return null; }
            var text = string.IsNullOrWhiteSpace(outp) ? errp : outp; // some toolchains print --version to stderr
            return text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        }
        catch { return null; }
    }

}

// Ambient handle to the active run's diagnostic collector so the top-level exception handler can write a
// failure package without threading the report object out of the carve. Single-threaded CLI: one run, one
// report; set at the start of the carve.
static class DiagState
{
    public static DiagnosticReport? Report;
    public static string? Path;
    /// <summary>Where an auto-written package lands when the user gave no --diag path (unhandled crash, or
    /// --diag-repro/--diag-verbose used alone). Session-stamped; set at the start of the carve.</summary>
    public static string? DefaultPath;
}
