using CodeCarver.Cli;

namespace CodeCarver.Tests.Examples;

/// <summary>Carves a copy of examples/&lt;name&gt; with its carve.toml, optionally with a different [builds.main]
/// section (another input set), and answers what each stage kept, dropped and stubbed.</summary>
internal class ExampleRun : IDisposable
{
    readonly TempDir _work;
    public string Dir { get; }
    public int Code { get; }
    public string Output { get; }
    public ExampleRun(string example, string? buildSection = null)
    {
        _work = new TempDir($"cc-{example}-");
        Dir = _work.Sub(example);
        TempDir.CopyTree(Path.Combine(TestRepo.Root, "examples", example), Dir);
        var cfg = Path.Combine(Dir, "carve.toml");
        if (buildSection is not null)
        {
            var text = File.ReadAllText(cfg);
            var start = text.IndexOf("[builds.main]", StringComparison.Ordinal);
            var end = text.IndexOf("[advanced]", StringComparison.Ordinal);
            File.WriteAllText(cfg, text[..start] + buildSection + "\n" + text[end..]);
        }
        var w = new StringWriter();
        Code = CarveCommand.Run(new[] { "carve", Path.Combine(Dir, "src"), "--config", cfg }, w, w);
        Output = w.ToString();
    }
    public string Carved(string stage, string rel) => Path.Combine(Dir, "out", stage, "carved", rel);
    public bool Kept(string stage, string rel) => File.Exists(Carved(stage, rel))
        && !File.ReadAllText(Carved(stage, rel)).Contains("Placeholder written by CodeCarver");
    public bool Placeholder(string stage, string rel) => File.Exists(Carved(stage, rel))
        && File.ReadAllText(Carved(stage, rel)).Contains("Placeholder written by CodeCarver");
    public string Summary(string stage) => File.ReadAllText(Path.Combine(Dir, "out", stage, "codecarver", "summary.txt"));
    public void Dispose() => _work.Dispose();
}
