using System.Text;

namespace CodeCarver.Core.Emit;

/// <summary>
/// The carve report: a source-free (paths only, no file contents), deterministic account of what the
/// carve did to every file, in the three buckets a reviewer actually needs to trust the result —
///   1. KEPT — required to build: the reachable code and the include closure it pulls in;
///   2. REMOVED — not needed to build: code files the carve modelled and proved unreachable (dead);
///   3. KEPT — infrastructure / non-code: everything else, passed through verbatim so <c>--out</c> is a
///      complete buildable project (build files, linker scripts, startup asm, data tables, configs, …).
/// Bucket 3 is grouped by extension so an over- or under-keep is easy to eyeball. Rendering is stable
/// (ordinal sort) so two runs over the same carve produce byte-identical reports.
/// </summary>
public static class CarveReport
{
    public readonly record struct Inputs(
        string SourceRoot,
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> BuildRequired,   // FileTreeEmitter.Written — kept code + include closure
        IReadOnlyList<string> KeptCode,        // plan.KeptFiles — the modelled-code subset of BuildRequired
        IReadOnlyList<string> RemovedDeadCode, // plan.DroppedFiles
        IReadOnlyList<string> Infrastructure,  // InfrastructureEmitter.Files
        IReadOnlyList<string> ExcludedDirs,
        long CodeBytesBefore, long CodeBytesAfter, long InfraBytes);

    public static string Render(Inputs x)
    {
        var sb = new StringBuilder();
        var includeClosure = x.BuildRequired.Where(f => !x.KeptCode.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var codeSaved = x.CodeBytesBefore - x.CodeBytesAfter;
        var pct = x.CodeBytesBefore > 0 ? (double)codeSaved / x.CodeBytesBefore : 0;

        sb.AppendLine("CodeCarver carve report  (paths only - NO file contents)");
        sb.AppendLine($"source : {x.SourceRoot}");
        sb.AppendLine($"roots  : {(x.Roots.Count == 0 ? "(none)" : string.Join(", ", x.Roots))}");
        if (x.ExcludedDirs.Count > 0) sb.AppendLine($"exclude: {string.Join(", ", x.ExcludedDirs)}");
        sb.AppendLine();

        sb.AppendLine("== Summary ==");
        sb.AppendLine($"  KEPT - required to build : {x.BuildRequired.Count,7} file(s)  "
                      + $"({x.KeptCode.Count} code + {includeClosure.Count} include-closure)");
        sb.AppendLine($"  REMOVED - dead code      : {x.RemovedDeadCode.Count,7} file(s)");
        sb.AppendLine($"  KEPT - infrastructure    : {x.Infrastructure.Count,7} file(s)  (non-code, passed through verbatim)");
        sb.AppendLine($"  code size                : {x.CodeBytesBefore:N0} B -> {x.CodeBytesAfter:N0} B "
                      + $"({pct:P0} smaller, saved {codeSaved:N0} B by dropping dead code)");
        sb.AppendLine($"  infrastructure size      : {x.InfraBytes:N0} B (unchanged - copied verbatim)");
        sb.AppendLine();

        sb.AppendLine($"== KEPT - required to build ({x.BuildRequired.Count}) ==");
        sb.AppendLine($"  -- reachable code ({x.KeptCode.Count}) --");
        foreach (var f in x.KeptCode.OrderBy(f => f, StringComparer.Ordinal)) sb.AppendLine($"    {f}");
        if (includeClosure.Count > 0)
        {
            sb.AppendLine($"  -- include closure, non-source ({includeClosure.Count}) --");
            foreach (var f in includeClosure) sb.AppendLine($"    {f}");
        }
        sb.AppendLine();

        sb.AppendLine($"== REMOVED - not needed to build ({x.RemovedDeadCode.Count}) ==");
        foreach (var f in x.RemovedDeadCode.OrderBy(f => f, StringComparer.Ordinal)) sb.AppendLine($"    {f}");
        sb.AppendLine();

        sb.AppendLine($"== KEPT - infrastructure / other, not code ({x.Infrastructure.Count}) ==");
        // Group by extension so a reviewer can see at a glance WHAT kind of non-code was kept.
        var byExt = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in x.Infrastructure)
        {
            var ext = Path.GetExtension(f);
            var key = string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();
            if (!byExt.TryGetValue(key, out var l)) byExt[key] = l = new List<string>();
            l.Add(f);
        }
        foreach (var kv in byExt)
        {
            sb.AppendLine($"  -- {kv.Key} ({kv.Value.Count}) {DescribeExt(kv.Key)} --");
            foreach (var f in kv.Value.OrderBy(f => f, StringComparer.Ordinal)) sb.AppendLine($"    {f}");
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
