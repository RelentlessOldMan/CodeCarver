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
    private static readonly Regex Quoted = new("\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Compiled);
    // strace -y: the path behind the returned descriptor, absolute — "= 3</abs/path>".
    private static readonly Regex StraceFdPath = new(@"\) = \d+<(.+)>$", RegexOptions.Compiled);
    private static readonly Regex StraceLine = new(@"^\s*(?:\[pid\s+\d+\]\s*|\d+\s+)?(?:\d[\d:.]*\s+)?\w+\(|resumed>", RegexOptions.Compiled);

    public static IReadOnlyCollection<string> Paths(string text, Regex? pattern = null) =>
        string.IsNullOrEmpty(text) ? new HashSet<string>() : Paths(text.Split('\n'), pattern);

    /// <summary>Streaming form: the caller passes <c>File.ReadLines</c>, so a multi-GB raw capture is never one
    /// string (review T2).</summary>
    public static IReadOnlyCollection<string> Paths(IEnumerable<string> lines, Regex? pattern = null)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
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

            // A raw strace line: prefer the absolute path strace -y reports for the returned fd; otherwise its
            // quoted argument. strace escapes non-ASCII bytes and quotes (caf\303\251.c, \") — decode them, or a
            // real file never matches (review SC-A6). Only strace lines are decoded: a Windows path has backslashes.
            var strace = StraceLine.IsMatch(line);
            if (strace)
            {
                var fd = StraceFdPath.Match(line);
                if (fd.Success) { paths.Add(DecodeStrace(fd.Groups[1].Value).Replace(" (deleted)", "")); continue; }
                if (line.Contains("= -1", StringComparison.Ordinal)) continue;   // a failed open is not a dependency
            }

            // No pattern: pull every quoted token (ProcMon CSV fields, strace's quoted path); if a line has no
            // quotes, treat the whole trimmed line as a path (the plain-list case).
            var any = false;
            foreach (Match q in Quoted.Matches(line))
            {
                var v = strace ? DecodeStrace(q.Groups[1].Value) : q.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(v)) { paths.Add(v.Trim()); any = true; }
            }
            if (!any) paths.Add(line.Trim());
        }
        return paths;
    }

    /// <summary>Undo strace's string escaping: \NNN octal bytes (UTF-8 sequences), \" and \\.</summary>
    public static string DecodeStrace(string s)
    {
        if (!s.Contains('\\')) return s;
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && OctalAt(s, i + 1) is { } b) { bytes.Add(b); i += 3; continue; }
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\') { bytes.Add((byte)s[i + 1]); i++; continue; }
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static byte? OctalAt(string s, int i)
    {
        if (i + 2 >= s.Length) return null;
        var v = 0;
        for (var k = 0; k < 3; k++)
        {
            var c = s[i + k];
            if (c < '0' || c > '7') return null;
            v = v * 8 + (c - '0');
        }
        return v <= 255 ? (byte)v : null;
    }
}
