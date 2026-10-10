namespace CodeCarver.Cli;

/// <summary>One aggressiveness tier to emit. <see cref="Name"/> is "" for the implicit single carve (no
/// <c>[stages]</c> in the config) — which writes straight under <c>outputDirectory/</c> — or the stage's name,
/// which writes under <c>outputDirectory/&lt;name&gt;/</c>.</summary>
public sealed record ResolvedStage(string Name, bool CarveSourceFileContents, bool CarveHeaderFileContents, bool StubUnexecuted = false);

/// <summary>The carve config resolved into flat engine inputs: the selected builds/runs UNIONED, the open/closed
/// world DERIVED, entryPoints expanded (incl. entryPointsFile), and the stage list chosen. This is the whole
/// translation from the sectioned TOML to what the engine consumes — kept separate from <c>RunCore</c> so it's
/// unit-testable without touching the pipeline.</summary>
public sealed class ResolvedCarve
{
    public List<string> EntryPoints = new();
    public List<string> Languages = new();
    public string OutputDirectory = "";
    public bool AnalysisOnly;
    public List<string> BuildLogs = new();
    public List<string> CompilerNames = new();
    public bool DropUnobservedCmm;                  // every selected run with a file trace opted in (D-C)    // extra driver names for text build logs
    public List<string> Compilers = new();        // from each selected build that named one
    public List<string> Defines = new();
    public List<string> BuildTraceFiles = new();
    public bool EveryBuildTraced;                  // every selected build has a build trace: its opens cover the whole build
    public bool SkipFilesNotBuilt = true;          // [advanced] then: code files no build opened are never read or parsed
    public bool PlaceholderFiles = true;           // [advanced] a dropped file the build compiled is emitted as a stand-in
    public List<string> RunTraceFiles = new();
    public List<string> RunTraceLogs = new();
    public List<string> ExcludeDirectories = new();
    public List<string> ForceKeepFiles = new();
    public bool ClosedWorld;                       // derived: a selected build has a build log or a compiler
    public string WorldReason = "";                // one-line explanation for the report
    public List<ResolvedStage> Stages = new();
    // [advanced] escape hatches (null = engine default).
    public long? MaxParseBytes;
    public List<(string From, string To)> PathMap = new();
    public bool AllowUnmatchedTraces;
    public bool PruneGarbage = true;
    public int? ParseTimeout;                      // seconds
    public int? MaxSymbolsPerFile;
}

public static class CarveResolver
{
    public sealed record Result(ResolvedCarve? Carve, IReadOnlyList<string> Errors);

    /// <summary>Resolve a loaded config into engine inputs. <paramref name="stageName"/> is the <c>--stage</c>
    /// selection (null = all defined stages, or the single implicit carve when none are defined).</summary>
    public static Result Resolve(CarveTomlConfig cfg, string? stageName)
    {
        var errors = new List<string>();
        var r = new ResolvedCarve();

        r.AnalysisOnly = cfg.AnalysisOnly ?? false;
        if (string.IsNullOrWhiteSpace(cfg.OutputDirectory))
            errors.Add("outputDirectory is required (top of the config) — where the carved tree + reports go.");
        else
            r.OutputDirectory = cfg.OutputDirectory;

        // entryPoints = inline list + (optional) file, one symbol per line (# comments / blanks skipped).
        r.EntryPoints.AddRange(cfg.Common.EntryPoints);
        if (!string.IsNullOrWhiteSpace(cfg.Common.EntryPointsFile))
        {
            try
            {
                foreach (var raw in File.ReadAllLines(cfg.Common.EntryPointsFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    r.EntryPoints.Add(line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { errors.Add($"entryPointsFile '{cfg.Common.EntryPointsFile}': could not read ({ex.GetType().Name})."); }
        }
        r.EntryPoints = r.EntryPoints.Distinct(StringComparer.Ordinal).ToList();
        if (r.EntryPoints.Count == 0)
            errors.Add("no entryPoints — list the entry symbols to keep in [common] entryPoints (or entryPointsFile).");

        r.Languages = cfg.Common.Languages.Count > 0
            ? cfg.Common.Languages.Select(l => l.ToLowerInvariant() switch { "cs" or "c#" => "csharp", "c++" or "cxx" => "cpp", var x => x })
                                  .Distinct().ToList()
            : new List<string> { "c" };
        var knownLangs = new[] { "c", "cpp", "csharp" };
        foreach (var l in r.Languages)
            if (!knownLangs.Contains(l))
                errors.Add($"languages: '{l}' is not supported (use c, cpp, csharp). .cmm is handled via run traces, not here.");
        // Multi-language carves merge into ONE graph, which only works for the C family (the C++ grammar is a
        // superset of C, so both parse together). C# has its own graph shape and can't be merged — run it separately.
        if (r.Languages.Count > 1)
        {
            var family = new[] { "c", "cpp" };
            var outside = r.Languages.Where(l => !family.Contains(l)).ToList();
            if (outside.Count > 0)
                errors.Add($"languages: multiple languages can only be merged for the C family (c + cpp). "
                    + $"Cannot merge {string.Join(", ", outside)} into the same graph — run a separate carve for those.");
        }

        r.ExcludeDirectories = cfg.Common.ExcludeDirectories.ToList();
        r.ForceKeepFiles = cfg.Common.ForceKeepFiles.ToList();
        r.MaxParseBytes = cfg.MaxParseBytes;
        r.PathMap = cfg.PathMap;
        r.AllowUnmatchedTraces = cfg.AllowUnmatchedTraces;
        r.PruneGarbage = cfg.PruneGarbage;
        r.ParseTimeout = cfg.ParseTimeout;
        r.MaxSymbolsPerFile = cfg.MaxSymbolsPerFile;

        // Selected builds/runs (UNION). Null selection = all defined.
        var buildNames = cfg.UseBuilds ?? cfg.Builds.Keys.ToList();
        var runNames = cfg.UseRuns ?? cfg.Runs.Keys.ToList();
        foreach (var b in buildNames)
        {
            if (!cfg.Builds.TryGetValue(b, out var bs)) continue; // loader already errored on a bad [use]
            r.BuildLogs.AddRange(bs.BuildLogs);
            r.Defines.AddRange(bs.Defines);
            r.BuildTraceFiles.AddRange(bs.BuildTraceFiles);
            if (!string.IsNullOrWhiteSpace(bs.Compiler)) r.Compilers.Add(bs.Compiler!);
            r.CompilerNames.AddRange(bs.CompilerNames);
        }
        var selectedBuilds = buildNames.Where(b => cfg.Builds.ContainsKey(b)).ToList();
        r.EveryBuildTraced = selectedBuilds.Count > 0 && selectedBuilds.All(b => cfg.Builds[b].BuildTraceFiles.Count > 0);
        r.SkipFilesNotBuilt = cfg.SkipFilesNotBuilt;
        r.PlaceholderFiles = cfg.PlaceholderFiles;
        var traced = runNames.Where(rn => cfg.Runs.TryGetValue(rn, out var x) && x.RunTraceFiles.Count > 0).ToList();
        r.DropUnobservedCmm = traced.Count > 0 && traced.All(rn => cfg.Runs[rn].DropUnobservedCmm);
        foreach (var rn in runNames)
        {
            if (!cfg.Runs.TryGetValue(rn, out var rs)) continue;
            r.RunTraceFiles.AddRange(rs.RunTraceFiles);
            r.RunTraceLogs.AddRange(rs.RunTraceLogs);
        }
        r.BuildLogs = r.BuildLogs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        r.Defines = r.Defines.Distinct(StringComparer.Ordinal).ToList();
        r.BuildTraceFiles = r.BuildTraceFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        r.RunTraceFiles = r.RunTraceFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        r.RunTraceLogs = r.RunTraceLogs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        r.Compilers = r.Compilers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Derive open/closed #ifdef world: a build log or a compiler means we know the real define set.
        if (r.BuildLogs.Count > 0 || r.Compilers.Count > 0)
        {
            r.ClosedWorld = true;
            var why = r.BuildLogs.Count > 0 ? $"{r.BuildLogs.Count} build log(s)" : null;
            if (r.Compilers.Count > 0) why = why is null ? $"compiler {r.Compilers[0]}" : $"{why} + compiler {r.Compilers[0]}";
            r.WorldReason = $"closed-world (dead #ifdef branches dropped) — have {why}";
        }
        else
        {
            r.ClosedWorld = false;
            r.WorldReason = "open-world (both #ifdef branches kept) — no build log or compiler given";
        }

        // --stage needs stages to pick from, and analysisOnly emits no stage at all (review U7).
        if (stageName is not null && r.AnalysisOnly)
            errors.Add("--stage has no effect with analysisOnly = true (nothing is emitted). Remove one of them.");
        else if (stageName is not null && cfg.Stages.Count == 0)
            errors.Add($"--stage '{stageName}' given, but the config defines no [stages.X] sections.");

        // Stages: explicit [stages.*], else one implicit carve from the [common] toggles.
        if (cfg.Stages.Count == 0)
        {
            r.Stages.Add(new ResolvedStage("", cfg.Common.CarveSourceFileContents, cfg.Common.CarveHeaderFileContents));
        }
        else if (stageName is not null)
        {
            if (cfg.Stages.TryGetValue(stageName, out var st))
                r.Stages.Add(new ResolvedStage(stageName, st.CarveSourceFileContents, st.CarveHeaderFileContents, st.StubUnexecuted));
            else
                errors.Add($"--stage '{stageName}' is not defined. Stages: {string.Join(", ", cfg.Stages.Keys)}.");
        }
        else
        {
            // No --stage: run them all, ordered by aggressiveness (least first) so comparisons read naturally.
            foreach (var kv in cfg.Stages.OrderBy(k => (k.Value.CarveSourceFileContents ? 1 : 0) + (k.Value.CarveHeaderFileContents ? 1 : 0) + (k.Value.StubUnexecuted ? 2 : 0)))
                r.Stages.Add(new ResolvedStage(kv.Key, kv.Value.CarveSourceFileContents, kv.Value.CarveHeaderFileContents, kv.Value.StubUnexecuted));
        }

        // Stubbing keeps what ran: without a function trace there is nothing to tell it what ran.
        foreach (var s in r.Stages.Where(s => s.StubUnexecuted))
            if (r.RunTraceLogs.Count == 0)
                errors.Add($"[stages.{s.Name}]: stubUnexecuted = true needs a function trace (runTraceLogs in a selected [runs.X]) — "
                    + "it stubs the functions the run did not execute.");
            else if (!r.Languages.Contains("c"))
                errors.Add($"[stages.{s.Name}]: stubUnexecuted = true applies to C (languages must include \"c\").");

        return new Result(errors.Count == 0 ? r : null, errors);
    }
}
