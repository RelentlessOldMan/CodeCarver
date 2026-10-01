using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Parses a FILE-ACCESS TRACE — the files the OS actually opened under the repo during a real build or run —
/// into candidate path strings. This is the embedded "run closure" lever: the loader/orchestration layer
/// (TRACE32 <c>.cmm</c> scripts that load a bootstrapper, then another binary, then helpers + data) is invisible
/// to a function trace and mostly unresolvable statically (its <c>DO</c>/<c>Data.LOAD</c> targets are computed
/// <c>&amp;var</c> paths). Observing what the OS opens sidesteps that entirely — the concrete path is reported
/// regardless of how it was computed.
///
/// Captured TWICE: during the build (compile/link inputs) and during the run/flash/debug session (the loader +
/// binaries + data). Two uses, mirroring <see cref="TraceFile"/>: (1) observed files ADD roots / a keep-floor so
/// the carve keeps what was touched; (2) a soundness ORACLE + attribution — a file the run read that the carve
/// would drop is a miss, and infra NOT observed becomes a drop-candidate.
///
/// Extraction is deliberately FORMAT-TOLERANT so there's no per-tool parser: with a custom regex (named
/// <c>path</c> group, or group 1) it applies that per line; otherwise it pulls every quoted token per line and
/// falls back to the whole line. Which candidates are REAL is decided by the caller filtering to under the carve
/// root (+ existence) — that selective filter is what turns "every quoted CSV field" into "repo files touched".
/// So ProcMon CSV, <c>strace openat</c>, and a plain path-per-line list all work unmodified.
/// </summary>
public static class FileAccessTrace
{
    private static readonly Regex Quoted = new("\"([^\"]*)\"", RegexOptions.Compiled);

    public static IReadOnlyCollection<string> Paths(string text, Regex? pattern = null)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return paths;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (pattern is not null)
            {
                foreach (Match m in pattern.Matches(line))
                {
                    var g = m.Groups["path"];
                    var v = g.Success ? g.Value
                          : m.Groups.Count > 1 ? m.Groups[1].Value
                          : m.Value;
                    if (!string.IsNullOrWhiteSpace(v)) paths.Add(v.Trim());
                }
                continue;
            }

            // No pattern: pull every quoted token (ProcMon CSV fields, strace's quoted path); if a line has no
            // quotes, treat the whole trimmed line as a path (the plain-list case).
            var any = false;
            foreach (Match q in Quoted.Matches(line))
            {
                var v = q.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(v)) { paths.Add(v.Trim()); any = true; }
            }
            if (!any) paths.Add(line.Trim());
        }
        return paths;
    }
}
