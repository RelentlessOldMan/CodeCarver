using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>One entry from a runtime function trace: the function that executed, and (if the trace carries
/// it) the source file and line it was defined/called at.</summary>
public readonly record struct TraceRecord(string Function, string? File, int? Line);

/// <summary>
/// Parses a RUNTIME TRACE — the functions a real run actually executed (name, and optionally file:line) —
/// into records. This is the tightest tier of the tightness ladder: what ran is definitely needed, and it
/// covers the dynamic edges (function pointers, dispatch tables) static reachability over-approximates or
/// misses. Two uses: (1) ADD traced functions as roots so the carve is guaranteed to keep them; (2) as a
/// soundness ORACLE — every traced function MUST be in the carve's kept set, or the static carve dropped
/// something that really ran.
///
/// The upstream trace format is not fixed yet, so extraction is a CONFIGURABLE regex with a named
/// <c>fn</c> group (optional <c>file</c>/<c>line</c>). The default handles the two shapes we expect —
/// a bare <c>funcName</c> per line, or <c>funcName file:line</c>. When the real format lands, pass a new
/// pattern (CLI <c>--trace-format</c>) instead of changing code. Blank lines and <c>#</c> comments skip.
/// </summary>
public static class TraceFile
{
    /// <summary>Default: a leading identifier (the function), optionally followed by <c>file:line</c>.</summary>
    /// An optional leading hex address (<c>0x0800a1c4</c>) or timestamp (<c>12.345</c>) is skipped, and a C++
    /// qualified name (<c>ns::Class::method</c>) yields its last component (review T6).
    public static readonly Regex DefaultPattern = new(
        @"^\s*(?:(?:0x)?[0-9A-Fa-f]{6,}:?\s+|\[?\d+(?:[.:]\d+)*\]?\s+)?(?<fn>[A-Za-z_~][\w~]*(?:::[A-Za-z_~][\w~]*)*)\b(?:.*?\s(?<file>[^\s:]+):(?<line>\d+))?",
        RegexOptions.Compiled);

    public static IReadOnlyList<TraceRecord> Parse(string text, Regex? pattern = null) => Parse(text, pattern, out _);

    /// <param name="unmatched">Non-blank, non-comment lines no function name could be read from.</param>
    public static IReadOnlyList<TraceRecord> Parse(string text, Regex? pattern, out int unmatched)
    {
        pattern ??= DefaultPattern;
        unmatched = 0;
        var recs = new List<TraceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var m = pattern.Match(line);
            if (!m.Success || !m.Groups["fn"].Success || m.Groups["fn"].Value.Length == 0) { unmatched++; continue; }
            var fn = m.Groups["fn"].Value;
            var colons = fn.LastIndexOf("::", StringComparison.Ordinal);
            if (colons >= 0) fn = fn[(colons + 2)..];
            var file = m.Groups["file"].Success && m.Groups["file"].Value.Length > 0 ? m.Groups["file"].Value : null;
            int? ln = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var l) ? l : null;
            var key = fn + "\0" + (file ?? "") + "\0" + (ln?.ToString() ?? "");
            if (seen.Add(key)) recs.Add(new TraceRecord(fn, file, ln));
        }
        return recs;
    }

    /// <summary>The distinct function names in a set of records (for rooting / the kept-set cross-check).</summary>
    public static IReadOnlyCollection<string> FunctionNames(IEnumerable<TraceRecord> recs) =>
        new HashSet<string>(recs.Select(r => r.Function), StringComparer.Ordinal);
}
