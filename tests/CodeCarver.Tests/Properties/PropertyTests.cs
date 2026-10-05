using CodeCarver.Cli;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Properties;

/// <summary>Seeded, compiler-free properties from review section 8c (TS6 and property 8).</summary>
public sealed class PropertyTests
{
    static readonly (string Path, string Text)[] Tree =
    {
        ("main.c", "#include \"api.h\"\nint main(void){ init(); run(&handler); return helper(); }\n"),
        ("api.h", "void init(void); void run(void (*f)(void)); void handler(void); int helper(void);\n#define TWICE(x) ((x)+(x))\n"),
        ("init.c", "#include \"api.h\"\nstatic int helper(void){ return 1; }\nvoid init(void){ helper(); }\n"),
        ("run.c", "#include \"api.h\"\nvoid run(void (*f)(void)){ f(); }\nvoid handler(void){}\n"),
        ("helper.c", "#include \"api.h\"\nint helper(void){ return TWICE(2); }\n"),
        ("dead.c", "void dead_entry(void){ dead_leaf(); }\nvoid dead_leaf(void){}\n"),
        ("table.c", "void handler(void);\nvoid (*const table[])(void) = { handler };\n"),
        ("dup_a.c", "static void same(void){}\nvoid a_api(void){ same(); }\n"),
        ("dup_b.c", "static void same(void){}\nvoid b_api(void){ same(); }\n"),
    };

    static (SortedSet<string> Kept, SortedSet<string> Dropped) CarveInOrder(IEnumerable<(string, string)> files)
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(files);
        var roots = new ExplicitRootProvider(symbols: new[] { "main" }).Discover(graph).ToList();
        var plan = ReachabilityEngine.Compute(graph, roots);
        var kept = new SortedSet<string>(graph.Nodes.Where(n => plan.IsKept(n.Id))
            .Select(n => $"{n.Kind} {n.Name} {n.FilePath}"), StringComparer.Ordinal);
        return (kept, new SortedSet<string>(plan.DroppedFiles, StringComparer.Ordinal));
    }

    [Fact]
    public void Carve_IsInvariantUnderFileOrderPermutation_TS6()
    {
        var (kept, dropped) = CarveInOrder(Tree);
        Assert.Contains("dead.c", dropped);   // the property is not vacuous: something is dropped
        for (var seed = 1; seed <= 8; seed++)
        {
            var rng = new Random(seed);
            var shuffled = Tree.OrderBy(_ => rng.Next()).ToArray();
            var (k, d) = CarveInOrder(shuffled);
            Assert.Equal(kept, k);
            Assert.Equal(dropped, d);
        }
    }

    // ---- never-throw fuzzers (property 8) ---------------------------------------------------------------

    static readonly string[] Atoms =
    {
        "\"", "'", "\\", "/", "*", "#", "{", "}", "(", ")", "[", "]", "=", ";", ":", ",", "\n", "\r\n", "\t", " ",
        "\0", "\uD800", "￿", "é", "@", "&", "$", "-D", "-I", "-include", "gcc", "cl.exe", "/D", "\\\n",
        "[common]", "[builds.x]", "[[x]]", "entryPoints", "= [", "]", "true", "-1", "9999999999999999999",
        "openat(AT_FDCWD, \"", "\") = 3", "= -1 ENOENT", "<unfinished ...>", "DO ", "GOSUB ", "&var", "~~~~/",
        "class ", "struct ", "template<", "::", "~", "#define ", "#if ", "#endif", "/*", "*/", "//", "R\"(",
    };

    static string Junk(Random rng)
    {
        var n = rng.Next(0, 60);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < n; i++)
            sb.Append(rng.Next(4) == 0 ? ((char)rng.Next(1, 0x3000)).ToString() : Atoms[rng.Next(Atoms.Length)]);
        return sb.ToString();
    }

    static void ForJunk(Action<string> act)
    {
        var rng = new Random(20261004);
        for (var i = 0; i < 400; i++)
        {
            var s = Junk(rng);
            try { act(s); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"threw {ex.GetType().Name} on input #{i}: {Escape(s)}\n{ex}"); }
        }
    }

    static string Escape(string s) => string.Concat(s.Select(c => c < 32 || c > 126 ? $"\\u{(int)c:X4}" : c.ToString()));

    [Fact] public void ConfigLoader_Parse_NeverThrows() => ForJunk(s => ConfigLoader.Parse(s, "fuzz.toml"));
    [Fact] public void BuildLogScraper_Parse_NeverThrows() => ForJunk(s => BuildLogScraper.Parse(s));
    [Fact] public void FileAccessTrace_Paths_NeverThrows() => ForJunk(s => FileAccessTrace.Paths(s));
    [Fact] public void TraceFile_Parse_NeverThrows() => ForJunk(s => TraceFile.Parse(s));
    [Fact] public void EmittedLinkCheck_Scan_NeverThrows() => ForJunk(s => { EmittedLinkCheck.Scan(s); EmittedLinkCheck.Scan(s, header: true); });
    [Fact] public void ConstructorGate_NeverThrows() => ForJunk(s => ConstructorGate.InstantiatingMentions(s, new HashSet<string> { "T", "class" }));

    [Fact]
    public void CmmFrontEnd_BuildGraph_NeverThrows() => ForJunk(s =>
    {
        var fe = new CmmFrontEnd();
        fe.BuildGraph(new[] { ("a.cmm", s), ("sub/b.cmm", "DO a\n" + s) });
    });
}
