using CodeCarver.Cli;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A throwaway tree for end-to-end carve tests: <c>src/</c> (the carve root), files outside it (an SDK, generated
/// headers), a build log of gcc lines, and an optional build trace. All names are invented.
/// </summary>
internal sealed class TreeCarve : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-tc-" + Guid.NewGuid().ToString("N"));
    public string Src => Path.Combine(Root, "src");
    public TreeCarve() => Directory.CreateDirectory(Src);

    static string F(string p) => p.Replace('\\', '/');
    /// <summary>Absolute path (forward slashes) of a file under src/.</summary>
    public string S(string rel) => F(Path.Combine(Src, rel));
    /// <summary>Absolute path (forward slashes) of a file under the work dir, outside src/.</summary>
    public string Outside(string rel) => F(Path.Combine(Root, rel));

    /// <summary>Writes a file under src/.</summary>
    public TreeCarve W(string rel, string text) => Put(Path.Combine(Src, rel), text);
    /// <summary>Writes a file outside the carve root (relative to the work dir).</summary>
    public TreeCarve WOut(string rel, string text) => Put(Path.Combine(Root, rel), text);
    TreeCarve Put(string full, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return this;
    }

    public List<string> LogLines { get; } = new();
    /// <summary>Adds a compile of <paramref name="rel"/> (under src/) with the given flags to the build log.</summary>
    public TreeCarve Compile(string rel, string flags = "")
    {
        LogLines.Add($"cd {F(Src)} && gcc {flags} -c {S(rel)} -o {Path.GetFileNameWithoutExtension(rel)}.o".Replace("  ", " "));
        return this;
    }

    /// <summary>Build-trace paths (absolute). Null = no trace.</summary>
    public List<string>? TracePaths { get; set; }
    /// <summary>Traces every compiled file plus the given extra paths (absolute, e.g. headers).</summary>
    public TreeCarve TraceCompiled(params string[] extraAbs)
    {
        TracePaths = LogLines.Select(l => l.Split(" -c ")[1].Split(" -o ")[0]).Concat(extraAbs).ToList();
        return this;
    }

    /// <param name="common">Extra lines for [common].</param>
    /// <param name="extraToml">Extra sections after [builds.b] (e.g. "[advanced]\n...").</param>
    public (int Code, string Out, string Err) Carve(string entry = "main", string extraToml = "", bool log = true, string common = "",
                                                   string languages = "\"c\"")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"outputDirectory = \"{F(Path.Combine(Root, "out"))}\"\n[common]\nentryPoints = [\"{entry}\"]\nlanguages = [{languages}]\n{common}");
        if (log || TracePaths is not null) sb.Append("[builds.b]\n");
        if (log && LogLines.Count > 0)
        {
            File.WriteAllText(Path.Combine(Root, "build.log"), string.Join("\n", LogLines) + "\n");
            sb.Append($"buildLogs = [\"{F(Path.Combine(Root, "build.log"))}\"]\n");
        }
        if (TracePaths is not null)
        {
            File.WriteAllText(Path.Combine(Root, "build.trace"), string.Join("\n", TracePaths) + "\n");
            sb.Append($"buildTraceFiles = [\"{F(Path.Combine(Root, "build.trace"))}\"]\n");
        }
        sb.Append(extraToml);
        var cfg = Path.Combine(Root, "carve.toml");
        File.WriteAllText(cfg, sb.ToString());
        var so = new StringWriter(); var se = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
        return (code, so.ToString(), se.ToString());
    }

    public string CarvedPath(string rel) => Path.Combine(Root, "out", "carved", rel);
    public bool Kept(string rel) => File.Exists(CarvedPath(rel)) && !File.ReadAllText(CarvedPath(rel)).Contains("Placeholder written by CodeCarver");
    public bool Placeholder(string rel) => File.Exists(CarvedPath(rel)) && File.ReadAllText(CarvedPath(rel)).Contains("Placeholder written by CodeCarver");
    public string Summary => File.ReadAllText(Path.Combine(Root, "out", "codecarver", "summary.txt"));
    public string VerifyLog => File.Exists(Path.Combine(Root, "out", "codecarver", "verify.txt"))
        ? File.ReadAllText(Path.Combine(Root, "out", "codecarver", "verify.txt")) : "";
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
