using CodeCarver.Cli;
using CodeCarver.Core.Diagnostics;
using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Examples;

/// <summary>
/// The horror examples, anonymized whole: every stage of the anonymized bundle must keep, drop and stub exactly the
/// files the original does. Everything the carve keys on (pasted names, macro-named includes, strings that name
/// functions, link flags, digraphs, raw strings, -D renames) must survive the renaming, or a bundle sent from a real
/// tree would not reproduce what happened there.
/// </summary>
public sealed class AnonymizedExamplesTests
{
    static List<string> Files(string dir, bool placeholders) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("Placeholder written by CodeCarver") == placeholders)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .OrderBy(r => r, StringComparer.Ordinal).ToList()
            : new List<string>();

    [Theory]
    [InlineData("eldritch", "safe", false, false)]
    [InlineData("eldritch", "max", true, true)]
    [InlineData("hellbuild", "safe", false, false)]
    [InlineData("hellbuild", "max", true, true)]
    public void EveryStage_CarvesTheSame_Anonymized(string example, string stage, bool carveSource, bool carveHeaders)
    {
        using var r = new ExampleRun(example);
        Assert.True(r.Code == 0, r.Output);
        var src = Path.Combine(r.Dir, "src");
        var maps = new[]
        {
            ($"/mnt/c/Playground/CodeCarver/examples/{example}/src", src),
            ($"/mnt/c/Playground/CodeCarver/examples/{example}/sdk", Path.Combine(r.Dir, "sdk")),
        };
        string Map(string p)
        {
            foreach (var (from, to) in maps)
                if (p.StartsWith(from, StringComparison.Ordinal)) return Path.GetFullPath(to + p[from.Length..]);
            return p;
        }
        var cmds = new List<CompileCommand>();
        foreach (var c in BuildLogScraper.Parse(File.ReadAllText(Path.Combine(r.Dir, "inputs", "build.log"))))
        {
            var dirs = new[] { c.Directory }.Concat(c.AlternativeDirectories).Select(Map).ToList();
            var dir = dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, Map(c.File)))) ?? dirs[0];
            cmds.Add(c with
            {
                Directory = dir, File = Map(c.File), Includes = c.Includes.Select(Map).ToList(),
                ForcedIncludes = c.ForcedIncludes.Select(Map).ToList(), AlternativeDirectories = Array.Empty<string>(),
            });
        }
        // Every path the trace names, present or not, the way the carve reads them.
        var trace = new List<string>();
        foreach (var cand in FileAccessTrace.Paths(File.ReadLines(Path.Combine(r.Dir, "inputs", "build.trace"))))
            try { var m = Map(cand); trace.Add(Path.IsPathFullyQualified(m) ? Path.GetFullPath(m) : Path.GetFullPath(Path.Combine(src, m))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        var input = new AnonymizedBundle.Input
        {
            Root = src,
            Files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(r.Dir, "sdk"), "*", SearchOption.AllDirectories)).ToList(),
            Commands = cmds,
            TraceOpened = trace,
            EntryPoints = new[] { "main" },
            LinkNames = LinkFlags.RequiredSymbols(File.ReadAllText(Path.Combine(r.Dir, "inputs", "build.log")), false).Select(u => u.Name).ToList(),
            Wrapped = LinkFlags.WrappedSymbols(File.ReadAllText(Path.Combine(r.Dir, "inputs", "build.log"))).ToList(),
            CarveSource = carveSource,
            CarveHeaders = carveHeaders,
        };
        var a = new Anonymizer();
        AnonymizedBundle.Learn(input, a, File.ReadAllText);
        var bundleDir = Path.Combine(r.Dir, "bundle");
        var b = AnonymizedBundle.Write(input, bundleDir, a, File.ReadAllText);
        Assert.True(b.FilesWithheld == 0, string.Join(" | ", b.Withheld.Select(w => w.File + ": " + string.Join(" ", w.Words))));

        var w = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", b.SourceRoot, "--config", b.ConfigPath }, w, w);
        Assert.True(code == 0, w.ToString());
        foreach (var ph in new[] { false, true })
        {
            var expected = Files(Path.Combine(r.Dir, "out", stage, "carved"), ph).Select(a.Path).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var actual = Files(Path.Combine(bundleDir, "out", b.StageName, "carved"), ph).Where(f => f != "link-names.rsp").ToList();
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            var key = a.Map().ToDictionary(p => p.Anonymized, p => p.Original);
            string Back(string p) => string.Join('/', p.Split('/').Select(s =>
            {
                var dot = s.LastIndexOf('.');
                var stem = dot > 0 ? s[..dot] : s;
                return (key.TryGetValue(stem, out var o) ? o.Replace("  (path)", "") : stem) + (dot > 0 ? s[dot..] : "");
            }));
            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"{(ph ? "placeholders" : "kept")}: missing {string.Join(", ", missing.Select(Back))}; extra {string.Join(", ", extra.Select(Back))}\n{w}");
        }
    }
}
