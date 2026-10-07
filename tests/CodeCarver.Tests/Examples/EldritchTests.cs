using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Examples;

/// <summary>
/// examples/eldritch: every nasty construct we know of in one small program. tools/eldritch/eldritch-oracle.ps1 builds
/// and runs each carve with real gcc (WSL) and demands the original's output; this test runs anywhere, carving with
/// the inputs that oracle captured (examples/eldritch/inputs) and pinning what must be kept, dropped and stubbed.
/// </summary>
public sealed class EldritchTests
{
    sealed class Run : IDisposable
    {
        readonly TempDir _work = new("cc-eldritch-");
        public string Dir { get; }
        public int Code { get; }
        public string Output { get; }
        public Run(string? buildSection = null)
        {
            Dir = _work.Sub("eldritch");
            TempDir.CopyTree(Path.Combine(TestRepo.Root, "examples", "eldritch"), Dir);
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
        public void Dispose() => _work.Dispose();
    }

    static readonly string[] Stages = { "safe", "headers", "aggressive", "max" };   // every combination of the two carve switches

    [Fact]
    public void LogAndTrace_EveryStageVerifies_DecoysGo_UnreachedCompiledFilesAreStubs()
    {
        using var r = new Run();
        Assert.True(r.Code == 0, r.Output);
        foreach (var s in Stages)
        {
            Assert.False(File.Exists(r.Carved(s, "legacy/decoy.c")), $"{s}: decoy kept\n{r.Output}");
            Assert.False(File.Exists(r.Carved(s, "legacy/garbage.c")), $"{s}: garbage kept");
            foreach (var stub in new[] { "quiet.c", "unused_compiled.c" })
                Assert.True(r.Placeholder(s, stub), $"{s}: {stub} is not a placeholder\n{r.Output}");
            // What only odd constructs reach: aliases, asm, attribute-only, inline-asm-only, #if 0 neighbours, ...
            foreach (var need in new[] { "aliases.c", "asm_target.c", "cleanup_fn.c", "digraph.c", "wrapped.c", "if0.c",
                                         "rename.c", "horrors.c", "bodyhelp.c", "handlers.c", "shoggoth.c", "forced.c",
                                         "rune.c", "linuxy.c", "inlhelp.c", "unicode.c", "lined.c", "hdr_user.c",
                                         // waves 3 and 4: linker, loader and command-line names
                                         "wrap_beast.c", "deep.c", "asmdef.c", "tmpl.c", "rites_b.c", "c99inl_emit.c", "gnu_twin.c",
                                         "ifunc.c", "ifunc_impl.c", "defsym_real.c", "dren.c", "next_two.c", "dirs_q.c", "dirs_sys.c",
                                         "dirs_after.c", "asm_callee.c", "ucn.c", "dollar.c", "ghost.c", "pp_alpha.c", "pp_beta.c",
                                         "audit_tick.c", "dl_target.c", "abyss_three.c", "redef.c", "spaced out.c", "deep/abyss.c" })
                Assert.True(r.Kept(s, need), $"{s}: {need} not kept\n{r.Output}");
            Assert.True(File.Exists(r.Carved(s, "asm_fast.S")), $"{s}: asm_fast.S not copied");
        }
        Assert.DoesNotContain("verify  : FAILED", r.Output);
    }

    [Theory]
    [InlineData("[builds.main]\nbuildLogs = [\"inputs/build.log\"]\n")]
    [InlineData("[builds.main]\nbuildTraceFiles = [\"inputs/build.trace\"]\n")]
    [InlineData("")]
    public void OtherInputSets_StillVerify(string buildSection)
    {
        using var r = new Run(buildSection);
        Assert.True(r.Code == 0, r.Output);
        Assert.DoesNotContain("verify  : FAILED", r.Output);
        foreach (var s in Stages)
            foreach (var need in new[] { "aliases.c", "asm_target.c", "cleanup_fn.c", "digraph.c", "wrapped.c", "if0.c" })
                Assert.True(r.Kept(s, need), $"{s}: {need} not kept\n{r.Output}");
    }
}
