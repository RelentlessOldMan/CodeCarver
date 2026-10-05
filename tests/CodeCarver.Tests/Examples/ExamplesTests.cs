using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Examples;

/// <summary>
/// Every worked example under <c>examples/</c> is carved through the real CLI (in-process), from a temp COPY of
/// the example so nothing is written into the repo. Pins three things the review found unpinned (section 8c,
/// "Examples"; section 8, "Prevent the drift"): each <c>carve.toml</c> loads, resolves and carves cleanly; the
/// emitted tree plus the manifest account for every source file; and the numbers the READMEs print still match a
/// fresh carve.
/// </summary>
public sealed class ExamplesTests
{
    private static string ExamplesDir => Path.Combine(TestRepo.Root, "examples");

    /// <summary>Every example directory that has its own carve.toml.</summary>
    public static IEnumerable<object[]> CarveTomlExamples() =>
        Directory.EnumerateDirectories(Path.Combine(TestRepo.Root, "examples"))
            .Where(d => File.Exists(Path.Combine(d, "carve.toml")))
            .Select(d => new object[] { Path.GetFileName(d) })
            .OrderBy(a => (string)a[0], StringComparer.Ordinal);

    /// <summary>Every example whose README shows a carve's output (lines like <c>nodes   : ...</c>).</summary>
    public static IEnumerable<object[]> ReadmeExamples() =>
        Directory.EnumerateDirectories(Path.Combine(TestRepo.Root, "examples"))
            .Where(d => File.Exists(Path.Combine(d, "README.md"))
                     && OutputBlocks(File.ReadAllText(Path.Combine(d, "README.md"))).Any())
            .Select(d => new object[] { Path.GetFileName(d) })
            .OrderBy(a => (string)a[0], StringComparer.Ordinal);

    [Fact]
    public void Examples_AllConfiguredExamplesAreDiscovered()
    {
        // Guards the discovery itself: a rename or a broken glob must not quietly turn the theories below into
        // zero cases.
        var names = CarveTomlExamples().Select(a => (string)a[0]).ToList();
        foreach (var expected in new[] { "cmm-trace", "csharp-app", "mixed-cpp-firmware", "multistage-firmware" })
            Assert.Contains(expected, names);
        var readmes = ReadmeExamples().Select(a => (string)a[0]).ToList();
        foreach (var expected in new[] { "cmm-trace", "csharp-app", "mixed-cpp-firmware", "multistage-firmware", "stringlib" })
            Assert.Contains(expected, readmes);
    }

    [Theory]
    [MemberData(nameof(CarveTomlExamples))]
    public void Examples_EveryCarveToml_LoadsAndResolves(string example)
    {
        using var run = CarvedExample.Run(example, "carve.toml");

        // Loads with no errors, resolves with no errors, and every input file it names exists.
        var loaded = ConfigLoader.Load(run.ConfigPath);
        Assert.True(loaded.Errors.Count == 0, $"{example}/carve.toml: " + string.Join("; ", loaded.Errors));
        var res = CarveResolver.Resolve(loaded.Config!, null);
        Assert.True(res.Errors.Count == 0, $"{example}/carve.toml: " + string.Join("; ", res.Errors));
        var c = res.Carve!;
        foreach (var f in c.BuildLogs.Concat(c.BuildTraceFiles).Concat(c.RunTraceFiles).Concat(c.RunTraceLogs))
            Assert.True(File.Exists(f), $"{example}/carve.toml names a missing input: {f}");
        Assert.NotEmpty(c.EntryPoints);
        Assert.NotEmpty(c.Stages);

        // The carve itself: exit 0, one emitted tree per stage, and the verify line per stage.
        Assert.True(run.Code == 0, $"{example}: exit {run.Code}\n{run.Output}");
        foreach (var stageDir in run.StageDirs)
        {
            Assert.True(Directory.Exists(Path.Combine(stageDir, "carved")), $"{example}: no carved/ under {stageDir}");
            Assert.True(File.Exists(Path.Combine(stageDir, "codecarver", "manifest.json")), $"{example}: no manifest under {stageDir}");
        }
        var verifyLines = run.Output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("verify  :")).ToList();
        Assert.Equal(c.Stages.Count, verifyLines.Count);
        if (c.Languages.Contains("csharp", StringComparer.OrdinalIgnoreCase))
            // The emitted-tree link check is C/C++ only; for C# the run says so instead of printing OK.
            Assert.All(verifyLines, l => Assert.Contains("C/C++ only", l));
        else
            Assert.All(verifyLines, l => Assert.StartsWith("verify  : OK", l));
    }

    [Theory]
    [MemberData(nameof(CarveTomlExamples))]
    public void Examples_EmittedTreeAccountsForEverySourceFile(string example)
    {
        using var run = CarvedExample.Run(example, "carve.toml");
        Assert.True(run.Code == 0, $"{example}: exit {run.Code}\n{run.Output}");

        var cfg = ConfigLoader.Load(run.ConfigPath).Config!;
        var excluded = cfg.Common.ExcludeDirectories.Select(d => d.Replace('\\', '/').Trim('/') + "/").ToList();
        var sources = Directory.EnumerateFiles(run.Src, "*", SearchOption.AllDirectories)
            .Select(f => Rel(run.Src, f)).ToList();
        Assert.NotEmpty(sources);

        foreach (var stageDir in run.StageDirs)
        {
            var stage = Path.GetFileName(stageDir);
            var carved = Path.Combine(stageDir, "carved");
            var emitted = Directory.EnumerateFiles(carved, "*", SearchOption.AllDirectories)
                .Select(f => Rel(carved, f)).ToHashSet(StringComparer.Ordinal);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(stageDir, "codecarver", "manifest.json")));
            var dropped = List(manifest, "droppedFiles");
            var droppedCmm = List(manifest, "droppedCmm");
            var garbage = List(manifest, "removedGarbageFiles");

            // Every source file is in exactly one bucket: emitted, dropped (code), dropped (.cmm), removed as
            // garbage, or under an excluded directory.
            foreach (var f in sources)
            {
                var buckets = new List<string>();
                if (emitted.Contains(f)) buckets.Add("emitted");
                if (dropped.Contains(f)) buckets.Add("droppedFiles");
                if (droppedCmm.Contains(f)) buckets.Add("droppedCmm");
                if (garbage.Contains(f)) buckets.Add("removedGarbageFiles");
                if (excluded.Any(x => f.StartsWith(x, StringComparison.Ordinal))) buckets.Add("excludeDirectories");
                Assert.True(buckets.Count == 1,
                    $"{example}/{stage}: src/{f} is in {buckets.Count} bucket(s) [{string.Join(", ", buckets)}]; expected exactly one");
            }

            // Nothing emitted that is not a source file (the marker lives beside carved/, never inside it).
            foreach (var e in emitted)
                Assert.True(sources.Contains(e), $"{example}/{stage}: carved/{e} is not a file of src/");

            // The manifest's kept lists account for the whole emitted tree. Review 8c ("Examples") predicts a gap
            // here: files copied by CopyUnscannedIncludes are emitted but recorded in no manifest list (the manifest
            // records plan.KeptFiles, not what was written). If this assertion fails, that is the product gap, not
            // a test bug — report it; do not weaken this check.
            var keptLists = List(manifest, "keptFiles").Concat(List(manifest, "infrastructureFiles")).ToHashSet(StringComparer.Ordinal);
            var unlisted = emitted.Where(e => !keptLists.Contains(e)).OrderBy(e => e, StringComparer.Ordinal).ToList();
            Assert.True(unlisted.Count == 0,
                $"{example}/{stage}: emitted but in neither keptFiles nor infrastructureFiles: {string.Join(", ", unlisted)}");
        }
    }

    [Theory]
    [MemberData(nameof(ReadmeExamples))]
    public void Examples_ReadmeOutputBlocks_MatchFreshCarve(string example)
    {
        // README drift (review section 8, "Prevent the drift"): the nodes/files/dropped/size lines a README prints
        // must still be what a fresh carve prints, in the same order. Whitespace runs and the version string are
        // normalised. A README may abbreviate the trailing part of a line in parentheses
        // ("(60% smaller)" for "(60% smaller, saved 2,328 B)"), but every number it shows must match.
        var readme = File.ReadAllText(Path.Combine(ExamplesDir, example, "README.md"));
        var blocks = OutputBlocks(readme).ToList();
        Assert.NotEmpty(blocks);

        if (File.Exists(Path.Combine(ExamplesDir, example, "carve.toml")))
        {
            using var run = CarvedExample.Run(example, "carve.toml");
            Assert.True(run.Code == 0, $"{example}: exit {run.Code}\n{run.Output}");
            foreach (var block in blocks) AssertKeyLinesMatch(example, block, run.Output);
        }
        else
        {
            // No carve.toml (stringlib): the README shows each scenario's config as a ```toml block followed by its
            // output block. Carve exactly the config the README prints.
            var tomls = FencedBlocks(readme).Where(b => b.Lang == "toml").Select(b => b.Body).ToList();
            Assert.True(tomls.Count == blocks.Count,
                $"{example}/README.md: {tomls.Count} toml block(s) but {blocks.Count} output block(s) — cannot pair them");
            for (var i = 0; i < tomls.Count; i++)
            {
                using var run = CarvedExample.Run(example, $"readme-{i}.toml", tomls[i]);
                Assert.True(run.Code == 0, $"{example} README scenario {i}: exit {run.Code}\n{run.Output}");
                AssertKeyLinesMatch($"{example} README scenario {i}", blocks[i], run.Output);
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static readonly Regex KeyLine = new(@"^\s*(nodes|files|dropped|size)\s+:", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"\b\d+\.\d+\.\d+(\+[0-9a-f]+)?(-dirty)?\b", RegexOptions.Compiled);

    private static string Norm(string line) => Version.Replace(Regex.Replace(line.Trim(), @"\s+", " "), "<ver>");

    private static List<string> KeyLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Where(l => KeyLine.IsMatch(l)).Select(Norm).ToList();

    private static bool LineMatches(string readme, string actual)
    {
        if (readme == actual) return true;
        // Allowed abbreviation: "(60% smaller)" in the README for "(60% smaller, saved 2,328 B)".
        return readme.EndsWith(')') && actual.EndsWith(')')
            && actual.StartsWith(readme[..^1] + ",", StringComparison.Ordinal);
    }

    private static void AssertKeyLinesMatch(string what, string block, string output)
    {
        var expected = KeyLines(block);
        var actual = KeyLines(output);
        Assert.NotEmpty(expected);
        var at = 0;
        foreach (var e in expected)
        {
            var hit = -1;
            for (var j = at; j < actual.Count; j++)
                if (LineMatches(e, actual[j])) { hit = j; break; }
            Assert.True(hit >= 0,
                $"{what}: README line no longer matches a fresh carve (in order):\n  README: {e}\n  carve key lines:\n    "
                + string.Join("\n    ", actual) + "\nUpdate the README output block.");
            at = hit + 1;
        }
    }

    private static IEnumerable<(string Lang, string Body)> FencedBlocks(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith("```")) continue;
            var lang = t[3..].Trim();
            var body = new List<string>();
            for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) body.Add(lines[i]);
            yield return (lang, string.Join("\n", body));
        }
    }

    /// <summary>Fenced blocks that show carve output: an untagged block with at least one key line.</summary>
    private static IEnumerable<string> OutputBlocks(string md) =>
        FencedBlocks(md).Where(b => b.Lang == "" && b.Body.Split('\n').Any(l => KeyLine.IsMatch(l))).Select(b => b.Body);

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static HashSet<string> List(JsonDocument doc, string prop) =>
        doc.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.GetString()!.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One example carved from a temp copy of its directory via the in-process CLI.</summary>
    private sealed class CarvedExample : IDisposable
    {
        private readonly TempDir _work;
        public string Dir { get; }
        public string Src => Path.Combine(Dir, "src");
        public string ConfigPath { get; }
        public int Code { get; }
        /// <summary>stdout and stderr interleaved in the order the run wrote them.</summary>
        public string Output { get; }
        /// <summary>One directory per emitted stage (the output directory itself when there are no stages).</summary>
        public IReadOnlyList<string> StageDirs { get; }

        private CarvedExample(TempDir work, string dir, string config, int code, string output, IReadOnlyList<string> stages)
        { _work = work; Dir = dir; ConfigPath = config; Code = code; Output = output; StageDirs = stages; }

        public static CarvedExample Run(string example, string configName, string? configText = null)
        {
            var work = new TempDir("cc-example-");
            try
            {
                var dir = work.Sub(example);
                TempDir.CopyTree(Path.Combine(TestRepo.Root, "examples", example), dir);
                var cfg = Path.Combine(dir, configName);
                if (configText is not null) File.WriteAllText(cfg, configText);

                var w = new StringWriter();
                var code = CarveCommand.Run(new[] { "carve", Path.Combine(dir, "src"), "--config", cfg }, w, w);

                var stages = new List<string>();
                var loaded = ConfigLoader.Load(cfg);
                if (loaded.Config is { } c && CarveResolver.Resolve(c, null).Carve is { } rc)
                    foreach (var s in rc.Stages)
                        stages.Add(s.Name.Length == 0 ? rc.OutputDirectory : Path.Combine(rc.OutputDirectory, s.Name));
                return new CarvedExample(work, dir, cfg, code, w.ToString(), stages);
            }
            catch
            {
                work.Dispose();
                throw;
            }
        }

        public void Dispose() => _work.Dispose();
    }
}
