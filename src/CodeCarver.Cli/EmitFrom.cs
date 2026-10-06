using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeCarver.Core.Emit;

namespace CodeCarver.Cli;

/// <summary>
/// <c>carve &lt;dir&gt; --config carve.toml --emit-from &lt;analysis output&gt;</c>: write the carved tree a prior
/// <c>analysisOnly</c> run decided on, without parsing again. At real-tree scale the parse IS the carve (work eval,
/// 1.0.159: most of an hour), so "dry run, then emit" used to cost two full runs.
///
/// The analysis run saves <c>codecarver/emit-plan.json</c>: the file-level plan (kept and dropped files), the
/// observed files, its verify result and summary, plus fingerprints of the configuration and of every file under
/// the source root (size and last-write time). <c>--emit-from</c> refuses when either fingerprint differs —
/// a stale plan would emit a tree that does not match the source. Only file-level stages can be replayed:
/// carving inside files needs the parsed graph. The link check is not re-run (it needs the parsed build
/// configuration); its result is carried over from the analysis run, which checked the same kept files.
/// </summary>
public static class EmitFrom
{
    public const string PlanFile = "emit-plan.json";
    private const int Format = 1;

    private sealed record Plan(int Format, string CodecarverVersion, string ConfigHash, string SourceHash, int SourceFiles,
                               List<string> KeptFiles, List<string> DroppedFiles, List<string> DroppedCmm,
                               List<string> ObservedFiles, bool VerifyFailed, Dictionary<string, JsonElement> Summary);

    /// <summary>A hash of every setting that shapes the plan (not outputDirectory, analysisOnly or the stages,
    /// which only shape the emit), including the size and time of each input file the config names.</summary>
    public static string ConfigHash(ResolvedCarve cv)
    {
        var sb = new StringBuilder();
        void L(string k, IEnumerable<string> v) => sb.Append(k).Append('=').AppendJoin('\u001f', v).Append('\n');
        L("entryPoints", cv.EntryPoints);
        L("languages", cv.Languages);
        L("defines", cv.Defines);
        L("compilers", cv.Compilers);
        L("compilerNames", cv.CompilerNames);
        L("exclude", cv.ExcludeDirectories);
        L("forceKeep", cv.ForceKeepFiles);
        L("pathMap", cv.PathMap.Select(p => p.From + "->" + p.To));
        L("flags", new[] { cv.ClosedWorld, cv.DropUnobservedCmm, cv.AllowUnmatchedTraces, cv.PruneGarbage }.Select(b => b ? "1" : "0"));
        L("advanced", new[] { cv.MaxParseBytes?.ToString() ?? "", cv.ParseTimeout?.ToString() ?? "", cv.MaxSymbolsPerFile?.ToString() ?? "" });
        foreach (var f in cv.BuildLogs.Concat(cv.BuildTraceFiles).Concat(cv.RunTraceFiles).Concat(cv.RunTraceLogs))
        {
            var info = new FileInfo(f);
            L("input", new[] { Path.GetFullPath(f), info.Exists ? info.Length.ToString() : "-", info.Exists ? info.LastWriteTimeUtc.Ticks.ToString() : "-" });
        }
        return Hex(sb.ToString());
    }

    /// <summary>A hash over every file under <paramref name="dir"/>: relative path, size, last-write time.</summary>
    public static (string Hash, int Files) SourceHash(string dir)
    {
        var rows = new List<string>();
        foreach (var p in CodeCarver.Core.Util.SourceWalk.Files(dir))
        {
            try
            {
                var info = new FileInfo(p);
                rows.Add($"{Path.GetRelativePath(dir, p).Replace('\\', '/')}\u001f{info.Length}\u001f{info.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rows.Add(p + "\u001f?"); }
        }
        rows.Sort(StringComparer.Ordinal);
        return (Hex(string.Join('\n', rows)), rows.Count);
    }

    private static string Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Written by an analysis-only run, beside its manifest.</summary>
    public static void WritePlan(string ccDir, string dir, ResolvedCarve cv, string version,
                                 IReadOnlyList<string> kept, IReadOnlyList<string> dropped, IReadOnlyList<string> droppedCmm,
                                 IEnumerable<string> observed, bool verifyFailed, IReadOnlyDictionary<string, object?> summary)
    {
        var (sourceHash, files) = SourceHash(dir);
        var plan = new
        {
            format = Format, codecarverVersion = version, configHash = ConfigHash(cv), sourceHash, sourceFiles = files,
            keptFiles = kept, droppedFiles = dropped, droppedCmm, observedFiles = observed.OrderBy(f => f, StringComparer.Ordinal),
            verifyFailed, summary,
        };
        File.WriteAllText(Path.Combine(ccDir, PlanFile), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Replay a saved plan into the configured stages. Exit codes as for a carve: 0, 2 (refused), 3
    /// (the analysis run's verify failed).</summary>
    public static int Run(string dir, ResolvedCarve cv, string priorOutput, string version, TextWriter @out, TextWriter err)
    {
        var planPath = Path.Combine(priorOutput, "codecarver", PlanFile);
        if (!File.Exists(planPath))
        {
            err.WriteLine($"--emit-from: no {PlanFile} in '{Path.Combine(priorOutput, "codecarver")}'. Run the carve with "
                + "analysisOnly = true first (this version writes the plan there).");
            return 2;
        }
        Plan? plan;
        try { plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { err.WriteLine($"--emit-from: could not read '{planPath}' ({ex.GetType().Name}: {ex.Message})."); return 2; }
        if (plan is null || plan.Format != Format)
        { err.WriteLine($"--emit-from: '{planPath}' is not a format-{Format} plan. Re-run the analysis with this version."); return 2; }
        if (plan.CodecarverVersion != version)
        {
            err.WriteLine($"--emit-from: the plan was made by CodeCarver {plan.CodecarverVersion}, this is {version}. "
                + "A different version may decide differently; re-run the analysis with this version.");
            return 2;
        }
        if (plan.ConfigHash != ConfigHash(cv))
        {
            err.WriteLine("--emit-from: the configuration changed since the analysis run (a setting, or a build log or trace "
                + "file it names). Re-run the analysis.");
            return 2;
        }
        @out.WriteLine("  emit-from: checking the source tree against the analysis run...");
        var (sourceHash, files) = SourceHash(dir);
        if (sourceHash != plan.SourceHash)
        {
            err.WriteLine($"--emit-from: the source tree changed since the analysis run ({plan.SourceFiles:N0} files then, "
                + $"{files:N0} now, or a file's size or time differs). Re-run the analysis.");
            return 2;
        }
        var inside = cv.Stages.Where(s => s.CarveSourceFileContents || s.CarveHeaderFileContents).Select(s => s.Name.Length == 0 ? "(single)" : s.Name).ToList();
        if (inside.Count > 0)
        {
            err.WriteLine($"--emit-from: stage(s) {string.Join(", ", inside)} carve inside files, which needs the parsed graph. "
                + "Select file-level stages with --stage, or run a normal carve.");
            return 2;
        }

        @out.WriteLine($"CodeCarver {version} — emit from the analysis run in {priorOutput} (no parse)");
        @out.WriteLine($"  plan    : {plan.KeptFiles.Count:N0} kept, {plan.DroppedFiles.Count:N0} dropped code file(s); source and config unchanged");
        var droppedForInfra = plan.DroppedFiles.Concat(plan.DroppedCmm).ToList();
        var exclude = cv.ExcludeDirectories.ToList();
        var aux = cv.ForceKeepFiles.ToList();
        for (var i = 0; i < cv.Stages.Count; i++)
        {
            var stage = cv.Stages[i];
            var baseDir = stage.Name.Length == 0 ? cv.OutputDirectory : Path.Combine(cv.OutputDirectory, stage.Name);
            var outDir = Path.Combine(baseDir, "carved");
            var ccDir = Path.Combine(baseDir, "codecarver");
            using var staged = StagedOutput.Begin(outDir);
            CancelHook.Track(staged);
            var res = FileTreeEmitter.Emit(plan.KeptFiles, plan.DroppedFiles, dir, staged.Dir);
            var infra = InfrastructureEmitter.Copy(dir, staged.Dir, res.Written, droppedForInfra, exclude, aux, cv.PruneGarbage, plan.ObservedFiles);
            foreach (var w in infra.Warnings) err.WriteLine($"  warn    : {w}");
            try { CancelHook.Promote(staged); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                err.WriteLine($"  error   : writing {outDir} failed ({ex.GetType().Name}: {ex.Message}). "
                    + (ex is PromoteFailedException { Torn: true } ? $"{outDir} is PARTIALLY updated — re-run." : $"{outDir} is unchanged."));
                return 1;
            }
            @out.WriteLine($"  stage   : {(stage.Name.Length == 0 ? "(single)" : stage.Name)}  emitted {res.FilesWritten:N0} code + "
                + $"{infra.Count:N0} infrastructure file(s) -> {outDir}");

            Directory.CreateDirectory(ccDir);
            var summary = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in plan.Summary) summary[k] = v;
            var key = $"stage{i}";
            summary["emitFrom"] = true;
            summary[$"{key}.keptFiles"] = plan.KeptFiles.Count;
            summary[$"{key}.droppedFiles"] = plan.DroppedFiles.Count;
            summary[$"{key}.emittedCodeFiles"] = res.FilesWritten;
            summary[$"{key}.infrastructureFiles"] = infra.Count;
            summary[$"{key}.garbageFilesExcluded"] = infra.Garbage.Count;
            summary[$"{key}.bytesAfter"] = res.BytesWritten + infra.Bytes;
            foreach (var (k, v) in plan.Summary.Where(kv => kv.Key.StartsWith("run.verify.", StringComparison.Ordinal)).ToList())
                summary[key + k["run".Length..]] = v;
            summary["exitCode"] = plan.VerifyFailed ? 3 : 0;
            File.WriteAllText(Path.Combine(ccDir, "summary.txt"),
                "# CodeCarver summary — numbers only: no path, file name or symbol. Safe to send back.\n"
                + string.Concat(summary.Select(kv => $"{kv.Key} = {Fmt(kv.Value)}\n")));
            var manifest = new
            {
                codecarverVersion = version, root = dir, stage = stage.Name, emittedFrom = Path.GetFullPath(priorOutput),
                keptFiles = plan.KeptFiles, droppedFiles = plan.DroppedFiles, droppedCmm = plan.DroppedCmm,
                includeClosureFiles = res.Written.Except(plan.KeptFiles, CodeCarver.Core.Util.PathComparer.Default)
                                                 .OrderBy(f => f, StringComparer.Ordinal).ToArray(),
                infrastructureFiles = infra.Files, removedGarbageFiles = infra.Garbage, observedFiles = plan.ObservedFiles,
            };
            File.WriteAllText(Path.Combine(ccDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            @out.WriteLine($"  summary : {Path.Combine(ccDir, "summary.txt")}");
        }
        @out.WriteLine(plan.VerifyFailed
            ? "  verify  : FAILED in the analysis run (carried over; see its verify.txt) — the carved tree will not link"
            : "  verify  : OK in the analysis run (carried over — same kept files)");
        return plan.VerifyFailed ? 3 : 0;

        static string Fmt(object? v) => v switch
        {
            bool b => b ? "true" : "false",
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            JsonElement e => e.ToString(),
            null => "",
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }
}
