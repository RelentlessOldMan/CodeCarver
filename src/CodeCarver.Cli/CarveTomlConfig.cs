using Tomlyn;
using Tomlyn.Model;

namespace CodeCarver.Cli;

/// <summary>The parsed carve config (TOML). One annotated file (<c>init</c> writes the template) holds every
/// input/option, grouped into <c>[common]</c>, named <c>[builds.X]</c> / <c>[runs.X]</c> / <c>[stages.X]</c>
/// tables, and an optional <c>[use]</c> selector. See <see cref="ConfigLoader"/> for load + validation.</summary>
public sealed class CarveTomlConfig
{
    public string? OutputDirectory;
    public bool? AnalysisOnly;        // true = decide only (plan + report + manifest), don't emit a carved tree
    public CommonSection Common = new();
    public Dictionary<string, BuildSection> Builds = new(StringComparer.Ordinal);
    public Dictionary<string, RunSection> Runs = new(StringComparer.Ordinal);
    public Dictionary<string, StageSection> Stages = new(StringComparer.Ordinal);
    public List<string>? UseBuilds;   // null = use all defined builds
    public List<string>? UseRuns;     // null = use all defined runs
    // [advanced] — rarely-needed tuning escape hatches (not in the init template). null = engine default.
    public long? MaxParseBytes;
    public int? ParseTimeout;         // seconds (0 disables)
    public int? MaxSymbolsPerFile;
}

public sealed class CommonSection
{
    public List<string> EntryPoints = new();
    public string? EntryPointsFile;
    public List<string> Languages = new();
    public List<string> ExcludeDirectories = new();
    public List<string> ForceKeepFiles = new();
    public bool CarveSourceFileContents;
    public bool CarveHeaderFileContents;
}

public sealed class BuildSection
{
    public List<string> BuildLogs = new();
    public string? Compiler;
    public List<string> Defines = new();
    public List<string> BuildTraceFiles = new();
}

public sealed class RunSection
{
    public List<string> RunTraceFiles = new();
    public List<string> RunTraceLogs = new();
}

public sealed class StageSection
{
    public bool CarveSourceFileContents;
    public bool CarveHeaderFileContents;
}

/// <summary>Loads and validates a carve.toml. Parsing is strict on purpose: an unknown key is an ERROR (a typo'd
/// option silently ignored is exactly the silent-config trap the evals flag), and a wrong value type is reported
/// with the key + section. Booleans accept <c>true/false</c> OR <c>yes/no/on/off/1/0</c> (user request).</summary>
public static class ConfigLoader
{
    public sealed record Result(CarveTomlConfig? Config, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    private static readonly string[] TopKeys = { "outputDirectory", "analysisOnly", "common", "builds", "runs", "stages", "use", "advanced" };
    private static readonly string[] AdvancedKeys = { "maxParseBytes", "parseTimeout", "maxSymbolsPerFile" };
    private static readonly string[] CommonKeys =
        { "entryPoints", "entryPointsFile", "languages", "excludeDirectories", "forceKeepFiles",
          "carveSourceFileContents", "carveHeaderFileContents" };
    private static readonly string[] BuildKeys = { "buildLogs", "compiler", "defines", "buildTraceFiles" };
    private static readonly string[] RunKeys = { "runTraceFiles", "runTraceLogs" };
    private static readonly string[] StageKeys = { "carveSourceFileContents", "carveHeaderFileContents" };
    private static readonly string[] UseKeys = { "builds", "runs" };

    public static Result Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new Result(null, new[] { $"--config '{path}': could not read ({ex.GetType().Name}: {ex.Message})" }, new List<string>()); }
        return Parse(text, path);
    }

    public static Result Parse(string text, string path)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        TomlTable root;
        try
        {
            var doc = Toml.Parse(text, path);
            if (doc.HasErrors)
            {
                foreach (var d in doc.Diagnostics) errors.Add($"--config '{path}': {d.Message} (line {d.Span.Start.Line + 1})");
                return new Result(null, errors, warnings);
            }
            root = doc.ToModel();
        }
        catch (Exception ex)
        { return new Result(null, new[] { $"--config '{path}': not valid TOML ({ex.Message})" }, warnings); }

        var cfg = new CarveTomlConfig();
        var ctx = new Ctx(errors, warnings);

        RejectUnknownKeys(root, TopKeys, "(top level)", ctx);
        cfg.OutputDirectory = GetString(root, "outputDirectory", "(top level)", ctx);
        cfg.AnalysisOnly = GetBool(root, "analysisOnly", "(top level)", ctx);

        if (GetTable(root, "common", ctx) is { } common)
        {
            RejectUnknownKeys(common, CommonKeys, "[common]", ctx);
            cfg.Common.EntryPoints = GetStringList(common, "entryPoints", "[common]", ctx);
            cfg.Common.EntryPointsFile = GetString(common, "entryPointsFile", "[common]", ctx);
            cfg.Common.Languages = GetStringList(common, "languages", "[common]", ctx);
            cfg.Common.ExcludeDirectories = GetStringList(common, "excludeDirectories", "[common]", ctx);
            cfg.Common.ForceKeepFiles = GetStringList(common, "forceKeepFiles", "[common]", ctx);
            cfg.Common.CarveSourceFileContents = GetBool(common, "carveSourceFileContents", "[common]", ctx) ?? false;
            cfg.Common.CarveHeaderFileContents = GetBool(common, "carveHeaderFileContents", "[common]", ctx) ?? false;
        }

        foreach (var (name, t) in NamedTables(root, "builds", ctx))
        {
            RejectUnknownKeys(t, BuildKeys, $"[builds.{name}]", ctx);
            cfg.Builds[name] = new BuildSection
            {
                BuildLogs = GetStringList(t, "buildLogs", $"[builds.{name}]", ctx),
                Compiler = GetString(t, "compiler", $"[builds.{name}]", ctx),
                Defines = GetStringList(t, "defines", $"[builds.{name}]", ctx),
                BuildTraceFiles = GetStringList(t, "buildTraceFiles", $"[builds.{name}]", ctx),
            };
        }

        foreach (var (name, t) in NamedTables(root, "runs", ctx))
        {
            RejectUnknownKeys(t, RunKeys, $"[runs.{name}]", ctx);
            cfg.Runs[name] = new RunSection
            {
                RunTraceFiles = GetStringList(t, "runTraceFiles", $"[runs.{name}]", ctx),
                RunTraceLogs = GetStringList(t, "runTraceLogs", $"[runs.{name}]", ctx),
            };
        }

        foreach (var (name, t) in NamedTables(root, "stages", ctx))
        {
            RejectUnknownKeys(t, StageKeys, $"[stages.{name}]", ctx);
            var s = new StageSection
            {
                CarveSourceFileContents = GetBool(t, "carveSourceFileContents", $"[stages.{name}]", ctx) ?? false,
                CarveHeaderFileContents = GetBool(t, "carveHeaderFileContents", $"[stages.{name}]", ctx) ?? false,
            };
            // Orthogonal but unusual: stripping header #defines while NOT carving source bodies. Allowed, warned.
            if (s.CarveHeaderFileContents && !s.CarveSourceFileContents)
                warnings.Add($"[stages.{name}]: carveHeaderFileContents=true with carveSourceFileContents=false is unusual "
                    + "(stripping header #defines but keeping all source functions) — allowed, but intended?");
            cfg.Stages[name] = s;
        }
        if (cfg.Common.CarveHeaderFileContents && !cfg.Common.CarveSourceFileContents)
            warnings.Add("[common]: carveHeaderFileContents=true with carveSourceFileContents=false is unusual — allowed, but intended?");

        if (GetTable(root, "use", ctx) is { } use)
        {
            RejectUnknownKeys(use, UseKeys, "[use]", ctx);
            cfg.UseBuilds = use.ContainsKey("builds") ? GetStringList(use, "builds", "[use]", ctx) : null;
            cfg.UseRuns = use.ContainsKey("runs") ? GetStringList(use, "runs", "[use]", ctx) : null;
        }

        if (GetTable(root, "advanced", ctx) is { } adv)
        {
            RejectUnknownKeys(adv, AdvancedKeys, "[advanced]", ctx);
            cfg.MaxParseBytes = GetLong(adv, "maxParseBytes", "[advanced]", ctx);
            cfg.ParseTimeout = (int?)GetLong(adv, "parseTimeout", "[advanced]", ctx);
            cfg.MaxSymbolsPerFile = (int?)GetLong(adv, "maxSymbolsPerFile", "[advanced]", ctx);
        }

        // Cross-checks: a [use] selection must name a defined section.
        foreach (var b in cfg.UseBuilds ?? new List<string>())
            if (!cfg.Builds.ContainsKey(b)) errors.Add($"[use] builds names '{b}', which has no [builds.{b}] section.");
        foreach (var r in cfg.UseRuns ?? new List<string>())
            if (!cfg.Runs.ContainsKey(r)) errors.Add($"[use] runs names '{r}', which has no [runs.{r}] section.");

        return new Result(errors.Count == 0 ? cfg : null, errors, warnings);
    }

    /// <summary>The annotated template written by <c>init</c>. Doubles as the config documentation; it parses
    /// cleanly through <see cref="Parse"/> (a drift test enforces that).</summary>
    public const string Template =
        """
        # CodeCarver config. Fill in what you have, delete what you don't. Run with:
        #   codecarver carve <source-dir> --config carve.toml
        # Comments start with #. Values are typed: "strings", [arrays], true/false (yes/no also accepted).
        # Every INPUT and OPTION lives here, so the command line stays short.

        # Where the carved project + reports go. CodeCarver manages a carved/ + codecarver/ layout under it,
        # always writes a report + manifest, and ignores its own output when scanning source.
        outputDirectory = "D:/carved/myimage"

        # ===== common to every build/run of this carve =====
        [common]
        # Entry symbols the image truly needs (ISRs, main, exported API). A missing named one FAILS the run.
        entryPoints = ["main", "Reset_Handler"]
        # entryPointsFile = "roots.txt"   # alternative: one symbol per line (for long curated lists)

        # The linked code to carve: "c", "cpp", "csharp". A mixed C+C++ tree -> ["c","cpp"] carves both into ONE
        # graph (reachability crosses the C/C++ boundary). "csharp" is its own graph — run it as a separate carve.
        # asm (.s) is auto-scanned for roots; .cmm is handled via run traces, not here.
        languages = ["c"]

        excludeDirectories = ["tests", "other_board"]   # dirs NOT in this image (variants, host tools, tests)
        forceKeepFiles = []              # globs to ALWAYS keep (even if excluded/auto-dropped), e.g. ["prebuilt/*.a"]

        carveSourceFileContents = false  # true = also drop unused functions WITHIN kept .c files (aggressive)
        carveHeaderFileContents = false  # true = also strip unused #defines from kept headers (aggressive)

        # ===== builds (how the real compiler sees the code) =====
        # One [builds.NAME] per build STEP; several steps/compilers UNION into one image. The build log is the
        # best input: it pins the exact -D/-I per file so #ifdefs resolve like your real build.
        [builds.main]
        buildLogs = ["make-n.log"]       # a `make -n` log, build console capture, or compile_commands.json (list several; unioned)
        compiler = ""                    # optional: your compiler exe (e.g. "arm-none-eabi-gcc"), probed for its built-in macros
        defines = []                     # RARE manual override, only if you have no build log: ["CHIP=F4","FEATURE_X=1"]
        buildTraceFiles = []             # optional: files opened while BUILDING (ProcMon/strace capture)

        # ===== runs (what a real execution actually touched) — all OPTIONAL =====
        # One [runs.NAME] per captured scenario. Tightens + audits; never drops what it didn't see.
        # [runs.smoke]
        # runTraceFiles = ["flash.csv"]  # files opened while RUNNING/flashing; catches the loader/.cmm/data layer
        # runTraceLogs  = ["run.log"]    # functions that actually ran (one name per line, or "name file:line")

        # ===== stages (optional: emit several aggressiveness tiers in one run) =====
        # Each [stages.NAME] is a tier; only the two carve toggles vary. Pick one with --stage NAME, or omit to
        # run all (each to <outputDirectory>/NAME/, with a size comparison). No stages = the [common] toggles once.
        # [stages.safe]
        # carveSourceFileContents = false
        # carveHeaderFileContents = false
        # [stages.aggressive]
        # carveSourceFileContents = true
        # carveHeaderFileContents = false
        # [stages.max]
        # carveSourceFileContents = true
        # carveHeaderFileContents = true

        # ===== use (optional: which builds/runs apply; default = all defined) =====
        # [use]
        # builds = ["main"]
        # runs = ["smoke"]

        """;

    private sealed record Ctx(List<string> Errors, List<string> Warnings);

    private static void RejectUnknownKeys(TomlTable t, string[] allowed, string where, Ctx ctx)
    {
        foreach (var k in t.Keys)
            if (!allowed.Contains(k, StringComparer.Ordinal))
                ctx.Errors.Add($"{where}: unknown key '{k}'. Valid keys: {string.Join(", ", allowed)}.");
    }

    private static TomlTable? GetTable(TomlTable t, string key, Ctx ctx)
    {
        if (!t.TryGetValue(key, out var v)) return null;
        if (v is TomlTable tt) return tt;
        ctx.Errors.Add($"'{key}' must be a section/table.");
        return null;
    }

    // Named sub-tables of a parent table ([builds.main] => parent "builds", name "main").
    private static IEnumerable<(string Name, TomlTable Table)> NamedTables(TomlTable root, string parent, Ctx ctx)
    {
        if (GetTable(root, parent, ctx) is not { } p) yield break;
        foreach (var k in p.Keys)
        {
            if (p[k] is TomlTable t) yield return (k, t);
            else ctx.Errors.Add($"[{parent}.{k}] must be a section/table.");
        }
    }

    private static string? GetString(TomlTable t, string key, string where, Ctx ctx)
    {
        if (!t.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return string.IsNullOrWhiteSpace(s) ? null : s;
        ctx.Errors.Add($"{where}: '{key}' must be a string.");
        return null;
    }

    private static List<string> GetStringList(TomlTable t, string key, string where, Ctx ctx)
    {
        var result = new List<string>();
        if (!t.TryGetValue(key, out var v) || v is null) return result;
        if (v is not TomlArray arr) { ctx.Errors.Add($"{where}: '{key}' must be an array of strings, e.g. [\"a\",\"b\"]."); return result; }
        foreach (var item in arr)
        {
            if (item is string s) { if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim()); }
            else ctx.Errors.Add($"{where}: '{key}' must contain only strings (got {item?.GetType().Name ?? "null"}).");
        }
        return result;
    }

    private static long? GetLong(TomlTable t, string key, string where, Ctx ctx)
    {
        if (!t.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case string s when long.TryParse(s, out var p): return p;
        }
        ctx.Errors.Add($"{where}: '{key}' must be an integer.");
        return null;
    }

    // Accepts a TOML bool (true/false) OR a string yes/no/true/false/on/off/1/0 (case-insensitive) — user request.
    private static bool? GetBool(TomlTable t, string key, string where, Ctx ctx)
    {
        if (!t.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case bool b: return b;
            case string s:
                switch (s.Trim().ToLowerInvariant())
                {
                    case "true" or "yes" or "on" or "1": return true;
                    case "false" or "no" or "off" or "0": return false;
                }
                break;
        }
        ctx.Errors.Add($"{where}: '{key}' must be true/false (or yes/no).");
        return null;
    }
}
