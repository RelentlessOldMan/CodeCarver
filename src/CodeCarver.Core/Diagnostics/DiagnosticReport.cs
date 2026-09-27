using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CodeCarver.Core.Diagnostics;

/// <summary>
/// Collects a source-free, shareable diagnostic snapshot of ONE carve run and writes it as a single
/// portable <c>.zip</c> (<c>summary.txt</c> + <c>diagnostics.json</c> + <c>manifest.txt</c>). This is the
/// CLI analogue of the spec's "Save Diagnostic Report": when a carve misbehaves on a repo the user cannot
/// share, they run with <c>--diag</c>, get one file, and send it — enough for a developer to reconstruct
/// what the tool did without the source.
///
/// PRIVACY IS THE OVERRIDING CONSTRAINT. CodeCarver's inputs are proprietary and must never leave the
/// user's machine, so the package NEVER contains source content — only what the tool did (version,
/// environment, parameters, statistics, warnings, phase timings, and any failure). Absolute paths that
/// would leak a username/home directory are redacted to <c>%USERPROFILE%</c>/<c>$HOME</c> placeholders.
///
/// The collector is a single centralized abstraction (spec §41): the CLI feeds it fields/events/warnings
/// as the run proceeds, then asks it to write the package. Generating the package is itself robust
/// (spec §24): a write failure returns false with a reason rather than throwing.
/// </summary>
public sealed class DiagnosticReport
{
    /// <summary>Bump when the package layout/field set changes so a future reader can tell versions apart.</summary>
    public const int FormatVersion = 1;

    public string SessionId { get; }
    private readonly DateTimeOffset _started;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<(long Ms, string Message)> _events = new();
    private readonly List<string> _warnings = new();
    private readonly Dictionary<string, object?> _fields = new(StringComparer.Ordinal);

    public DiagnosticReport(string sessionId, DateTimeOffset startedUtc)
    {
        SessionId = sessionId;
        _started = startedUtc;
    }

    /// <summary>New report with a fresh short session id and the current UTC start time.</summary>
    public static DiagnosticReport Start() =>
        new(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow);

    /// <summary>Record a breadcrumb with the elapsed time since start (phase transitions, milestones).</summary>
    public void Event(string message) => _events.Add((_sw.ElapsedMilliseconds, message));

    /// <summary>Record a warning surfaced during the run (kept-whole fragments, unresolved includes, …).</summary>
    public void Warn(string warning) => _warnings.Add(warning);

    /// <summary>Add or overwrite a structured field (version, lang, roots, stats, …).</summary>
    public void Set(string key, object? value) => _fields[key] = value;

    /// <summary>Record the failure that ended the run (for the unhandled-exception path). Type + message +
    /// stack are safe to include; they describe the tool, not the user's source.</summary>
    public void SetFailure(Exception ex)
    {
        _fields["failed"] = true;
        _fields["exceptionType"] = ex.GetType().FullName;
        _fields["exceptionMessage"] = ex.Message;
        _fields["exceptionStack"] = ex.StackTrace;
        if (ex.InnerException is { } inner)
            _fields["innerException"] = $"{inner.GetType().FullName}: {inner.Message}";
    }

    /// <summary>Replace the user's home-directory prefix (both separator styles) with a placeholder so a
    /// username never ships in the package. Returns the input unchanged when it contains no home path.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return text;
        var placeholder = OperatingSystem.IsWindows() ? "%USERPROFILE%" : "$HOME";
        var result = text.Replace(home, placeholder, StringComparison.OrdinalIgnoreCase);
        // Windows paths may appear with forward slashes after normalization; redact that form too.
        var homeFwd = home.Replace('\\', '/');
        if (!homeFwd.Equals(home, StringComparison.Ordinal))
            result = result.Replace(homeFwd, placeholder, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Human-readable overview a developer can read WITHOUT unzipping the raw artifacts.</summary>
    public string BuildSummaryText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CodeCarver Diagnostic Report");
        sb.AppendLine($"DiagnosticFormatVersion: {FormatVersion}");
        sb.AppendLine($"Session: {SessionId}");
        sb.AppendLine($"Created (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Started (UTC): {_started:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Duration: {_sw.ElapsedMilliseconds} ms");
        sb.AppendLine();
        sb.AppendLine("== Fields ==");
        foreach (var kv in _fields)
            sb.AppendLine($"{kv.Key}: {Redact(FormatValue(kv.Value))}");
        sb.AppendLine();
        sb.AppendLine($"== Events ({_events.Count}) ==");
        foreach (var (ms, msg) in _events)
            sb.AppendLine($"[{ms,7} ms] {Redact(msg)}");
        sb.AppendLine();
        sb.AppendLine($"== Warnings ({_warnings.Count}) ==");
        foreach (var w in _warnings)
            sb.AppendLine($"- {Redact(w)}");
        return sb.ToString();
    }

    /// <summary>Machine-readable snapshot with the same information (for future automated analysis).</summary>
    public string BuildDiagnosticsJson()
    {
        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["diagnosticFormatVersion"] = FormatVersion,
            ["session"] = SessionId,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("o"),
            ["startedUtc"] = _started.ToString("o"),
            ["durationMs"] = _sw.ElapsedMilliseconds,
            ["fields"] = _fields.ToDictionary(kv => kv.Key, kv => (object?)Redact(FormatValue(kv.Value))),
            ["events"] = _events.Select(e => new Dictionary<string, object?> { ["ms"] = e.Ms, ["message"] = Redact(e.Message) }).ToList(),
            ["warnings"] = _warnings.Select(Redact).ToList(),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Self-describing list of what the package contains and — importantly — what it EXCLUDES,
    /// so the recipient can trust it carries no source or secrets.</summary>
    public string BuildManifestText() =>
        $"""
        CodeCarver Diagnostic Package
        DiagnosticFormatVersion: {FormatVersion}
        Session: {SessionId}
        Created (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}

        Included:
        - summary.txt        (human-readable overview)
        - diagnostics.json   (structured snapshot)
        - manifest.txt       (this file)

        Excluded (by design):
        - Source file contents (proprietary — never collected)
        - Environment variables, secrets, credentials
        - Absolute home paths (redacted to a placeholder)

        """;

    /// <summary>
    /// Write the package to <paramref name="outPath"/>. If that is an existing directory (or ends with a
    /// separator), a timestamped file <c>CodeCarver_Diagnostics_&lt;UTC&gt;.zip</c> is created inside it;
    /// otherwise <paramref name="outPath"/> is used as the zip path (a <c>.zip</c> extension is appended if
    /// missing). Never throws: on failure returns false with a reason, so a diagnostics write can never be
    /// the thing that crashes the tool (spec §24).
    /// </summary>
    public bool TryWritePackage(string outPath, out string zipPath, out string? error)
    {
        zipPath = "";
        error = null;
        try
        {
            var target = ResolveZipPath(outPath);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write to a temp file first, then move into place — a crash mid-write can't leave a
            // half-written .zip masquerading as a valid package.
            var tmp = target + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "summary.txt", BuildSummaryText());
                WriteEntry(zip, "diagnostics.json", BuildDiagnosticsJson());
                WriteEntry(zip, "manifest.txt", BuildManifestText());
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            zipPath = target;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or DirectoryNotFoundException or System.Security.SecurityException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private string ResolveZipPath(string outPath)
    {
        var looksLikeDir = Directory.Exists(outPath)
                           || outPath.EndsWith(Path.DirectorySeparatorChar)
                           || outPath.EndsWith(Path.AltDirectorySeparatorChar);
        if (looksLikeDir)
            return Path.Combine(outPath, $"CodeCarver_Diagnostics_{DateTimeOffset.UtcNow:yyyy-MM-dd_HHmmss}.zip");
        return outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? outPath : outPath + ".zip";
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string FormatValue(object? v) => v switch
    {
        null => "(none)",
        string s => s,
        System.Collections.IEnumerable e and not string => string.Join(", ", e.Cast<object?>().Select(x => x?.ToString())),
        _ => v.ToString() ?? "",
    };
}
