using System.Text;

namespace CodeCarver.Core.Emit;

/// <summary>
/// The carve report: a source-free (paths only, no file contents), deterministic account of what the
/// carve did to every file, in the buckets a reviewer actually needs to trust the result —
///   1. KEPT — required to build: the reachable code and the include closure it pulls in;
///   2. REMOVED — not needed to build: code files the carve modelled and proved unreachable (dead);
///   3. KEPT — infrastructure / non-code: everything else, passed through verbatim so <c>--out</c> is a
///      complete buildable project (build files, linker scripts, startup asm, data tables, configs, …);
///   4. REMOVED — garbage: files that cannot be a build/run input by universal convention (VCS metadata,
///      compiler/IDE scratch, dep/coverage artifacts, editor/OS junk, logs/temp).
/// Bucket 3 is grouped by build ROLE (build-system / data+resources / other) then by extension, so an over-
/// or under-keep — and anything that looks like a stray build output — is easy to eyeball. Rendering is
/// stable (ordinal sort) so two runs over the same carve produce byte-identical reports.
///
/// Bucket 3 (and the include-closure split of bucket 1) is only known once the tree is actually emitted:
/// the include closure is discovered during emit, and infrastructure is what emit passed through. So in
/// analysis-only mode (no <c>--out</c>, <see cref="Inputs.InfraEnumerated"/> = false) the report shows the
/// carve DECISION (reachable code vs dead code) and states plainly that infrastructure + include-closure
/// are enumerated only with <c>--out</c> — rather than guessing and mislabelling build-required includes.
/// </summary>
public static class CarveReport
{
    public readonly record struct Inputs(
        string SourceRoot,
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> BuildRequired,   // FileTreeEmitter.Written — kept code + include closure (emit only)
        IReadOnlyList<string> KeptCode,        // plan.KeptFiles — files with a reached node
        IReadOnlyList<string> RemovedDeadCode, // plan.DroppedFiles
        IReadOnlyList<string> Infrastructure,  // InfrastructureEmitter.Files (emit only)
        IReadOnlyList<string> ExcludedDirs,
        long CodeBytesBefore, long CodeBytesAfter, long InfraBytes,
        bool InfraEnumerated,                  // true once --out emitted (closure + infra are real)
        IReadOnlyList<string> RemovedGarbage,  // InfrastructureEmitter.Garbage — VCS/scratch/editor/coverage (emit only)
        long GarbageBytes,
        IReadOnlyList<string>? Observed = null);// files a build/run file-trace observed being opened (attribution)

    public static string Render(Inputs x)
    {
        var sb = new StringBuilder();

        // Match the emit pipeline's path identity: rel paths are compared case-insensitively everywhere
        // (InfrastructureEmitter skip/dropped sets, CopyUnscannedIncludes' known set, BuildSupportEmitter).
        // A List.Contains here would be both O(n*m) at 68k files AND ordinal/case-sensitive — mis-splitting
        // the buckets on Windows. HashSet(OrdinalIgnoreCase) fixes both.
        var keptSet = new HashSet<string>(x.KeptCode, StringComparer.OrdinalIgnoreCase);
        var buildReqSet = new HashSet<string>(x.BuildRequired, StringComparer.OrdinalIgnoreCase);

        // BuildRequired = reachable code that was actually written ∪ its include closure. Split it cleanly so
        // the two counts always sum to BuildRequired.Count (no double-count, no gap).
        var includeClosure = x.BuildRequired.Where(f => !keptSet.Contains(f))
                                            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        // Reachable code present in the emitted set. A kept file absent/locked on disk is in KeptCode but not
        // BuildRequired (FileTreeEmitter skips it) — list those separately so counts stay honest (M2).
        var reachableCode = (x.InfraEnumerated ? x.KeptCode.Where(f => buildReqSet.Contains(f)) : x.KeptCode)
                            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var keptAbsent = x.InfraEnumerated
            ? x.KeptCode.Where(f => !buildReqSet.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();

        var codeSaved = x.CodeBytesBefore - x.CodeBytesAfter;
        var pct = x.CodeBytesBefore > 0 ? (double)codeSaved / x.CodeBytesBefore : 0;

        sb.AppendLine("CodeCarver carve report  (paths only - NO file contents)");
        sb.AppendLine($"source : {x.SourceRoot}");
        sb.AppendLine($"roots  : {(x.Roots.Count == 0 ? "(none)" : string.Join(", ", x.Roots))}");
        if (x.ExcludedDirs.Count > 0) sb.AppendLine($"exclude: {string.Join(", ", x.ExcludedDirs)}");
        if (!x.InfraEnumerated)
            sb.AppendLine("mode   : analysis-only (analysisOnly = true) - infrastructure + include-closure are enumerated only when emitting");
        sb.AppendLine();

        sb.AppendLine("== Summary ==");
        if (x.InfraEnumerated)
            sb.AppendLine($"  KEPT - required to build : {x.BuildRequired.Count,7} file(s)  "
                          + $"({reachableCode.Count} code + {includeClosure.Count} include-closure)");
        else
            sb.AppendLine($"  KEPT - reachable code    : {reachableCode.Count,7} file(s)  (include-closure resolved only when emitting)");
        sb.AppendLine($"  REMOVED - dead code      : {x.RemovedDeadCode.Count,7} file(s)");
        if (x.InfraEnumerated)
            sb.AppendLine($"  KEPT - infrastructure    : {x.Infrastructure.Count,7} file(s)  (non-code, passed through verbatim)");
        else
            sb.AppendLine($"  KEPT - infrastructure    :   (n/a) file(s)  (enumerated only when emitting; every non-code file is passed through)");
        if (x.InfraEnumerated && x.RemovedGarbage.Count > 0)
            sb.AppendLine($"  REMOVED - garbage        : {x.RemovedGarbage.Count,7} file(s)  (VCS/scratch/editor/coverage - not a build/run input)");
        if (keptAbsent.Count > 0)
            sb.AppendLine($"  NOTE                     : {keptAbsent.Count,7} kept file(s) were NOT written (absent/locked on disk) - listed below");
        sb.AppendLine($"  code size                : {x.CodeBytesBefore:N0} B -> {x.CodeBytesAfter:N0} B "
                      + $"({pct:0%} smaller, saved {codeSaved:N0} B by dropping dead code)");
        if (x.InfraEnumerated)
            sb.AppendLine($"  infrastructure size      : {x.InfraBytes:N0} B (unchanged - copied verbatim)");
        if (x.InfraEnumerated && x.RemovedGarbage.Count > 0)
            sb.AppendLine($"  garbage not copied       : {x.GarbageBytes:N0} B (excluded from the code-carve % above)");
        sb.AppendLine();

        var buildTitle = x.InfraEnumerated
            ? $"== KEPT - required to build ({x.BuildRequired.Count}) =="
            : $"== KEPT - reachable code ({reachableCode.Count}) ==";
        sb.AppendLine(buildTitle);
        sb.AppendLine($"  -- reachable code ({reachableCode.Count}) --");
        foreach (var f in reachableCode) sb.AppendLine($"    {f}");
        if (includeClosure.Count > 0)
        {
            sb.AppendLine($"  -- include closure, non-source ({includeClosure.Count}) --");
            foreach (var f in includeClosure) sb.AppendLine($"    {f}");
        }
        sb.AppendLine();

        if (keptAbsent.Count > 0)
        {
            sb.AppendLine($"== KEPT but NOT WRITTEN - absent/locked on disk ({keptAbsent.Count}) ==");
            foreach (var f in keptAbsent) sb.AppendLine($"    {f}");
            sb.AppendLine();
        }

        sb.AppendLine($"== REMOVED - not needed to build ({x.RemovedDeadCode.Count}) ==");
        foreach (var f in x.RemovedDeadCode.OrderBy(f => f, StringComparer.Ordinal)) sb.AppendLine($"    {f}");
        sb.AppendLine();

        if (!x.InfraEnumerated)
        {
            sb.AppendLine("== KEPT - infrastructure / other, not code ==");
            sb.AppendLine("  (not enumerated in analysisOnly mode; keep-by-default passes through EVERY non-code file verbatim)");
            return sb.ToString();
        }

        var observedSet = new HashSet<string>(x.Observed ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        sb.AppendLine($"== KEPT - infrastructure / other, not code ({x.Infrastructure.Count}) ==");
        if (observedSet.Count > 0)
        {
            var observedInfra = x.Infrastructure.Count(observedSet.Contains);
            sb.AppendLine($"  (file-trace: {observedInfra} of {x.Infrastructure.Count} observed being opened; "
                + $"{x.Infrastructure.Count - observedInfra} NOT observed - candidates to drop if the trace(s) covered a full build+run)");
        }
        // Group by build ROLE first (what's there to build vs data/resources vs everything else), then by
        // extension within each role, so a reviewer sees the build/run/other split at a glance.
        foreach (var (role, title) in new[]
                 {
                     (InfraRole.BuildSystem, "build system & toolchain (make/cmake/linker/asm/project) - needed to build"),
                     (InfraRole.Data,        "data / resources (config, tables, device trees, prebuilt binaries) - may be needed to run"),
                     (InfraRole.Other,       "other (docs, images, unknown) - kept for safety"),
                 })
        {
            var inRole = x.Infrastructure.Where(f => InfraClassifier.RoleOf(f) == role).ToList();
            if (inRole.Count == 0) continue;
            sb.AppendLine($"  == {title} ({inRole.Count}) ==");
            var byExt = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var f in inRole)
            {
                var ext = Path.GetExtension(f);
                var key = string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();
                if (!byExt.TryGetValue(key, out var l)) byExt[key] = l = new List<string>();
                l.Add(f);
            }
            foreach (var kv in byExt)
            {
                sb.AppendLine($"    -- {kv.Key} ({kv.Value.Count}) {DescribeExt(kv.Key)} --");
                foreach (var f in kv.Value.OrderBy(f => f, StringComparer.Ordinal))
                    sb.AppendLine($"      {f}{(observedSet.Contains(f) ? "  [observed]" : "")}");
            }
        }

        // Review note: kept files that LOOK like build outputs / prebuilt binaries. We keep them (they may be
        // vendored and load-bearing) but a user whose tree checks in a build/obj dir can --exclude it to trim.
        var outputs = x.Infrastructure.Where(InfraClassifier.LooksLikeBuildOutput)
                                      .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (outputs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"== REVIEW - look like build outputs / prebuilt binaries ({outputs.Count}) ==");
            sb.AppendLine("  (kept - may be vendored prebuilts the build links; if they are generated, add their dir to excludeDirectories)");
            foreach (var f in outputs) sb.AppendLine($"    {f}");
        }

        if (x.RemovedGarbage.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"== REMOVED - garbage, not a build/run input ({x.RemovedGarbage.Count}) ==");
            sb.AppendLine("  (VCS metadata, compiler/IDE scratch, dep/coverage artifacts, editor/OS junk, logs/temp; "
                          + "restore one with forceKeepFiles)");
            foreach (var f in x.RemovedGarbage.OrderBy(f => f, StringComparer.Ordinal)) sb.AppendLine($"    {f}");
        }
        return sb.ToString();
    }

    /// <summary>A short, generic hint about a non-code extension's likely build role — purely informational
    /// (never drives keep/drop) and toolchain-neutral. Unknown extensions get no hint.</summary>
    private static string DescribeExt(string ext) => ext switch
    {
        ".mk" or ".mak" or ".make" => "- make fragment",
        ".cmake" => "- CMake",
        ".ld" or ".lds" or ".ldscript" => "- linker script",
        ".cmd" or ".scat" or ".sct" or ".icf" => "- linker command / scatter",
        ".s" or ".asm" => "- assembly",
        ".cmm" => "- TRACE32 script",
        ".dts" or ".dtsi" => "- device tree",
        ".a" or ".o" or ".lib" or ".obj" => "- prebuilt binary",
        ".inc" or ".def" or ".tab" => "- generated table / data",
        ".json" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" => "- config",
        _ => "",
    };
}
