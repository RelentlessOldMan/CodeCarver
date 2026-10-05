using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// The trace-seeded .cmm tightening: observed scripts seed a DO/GOSUB closure, scripts neither observed nor
/// reachable are dropped, and the statically-unresolvable residue (dynamic <c>DO &amp;var</c>, oversized
/// kept-whole) is surfaced rather than silently dropped. Models a realistic small-script web (the common
/// shape in the real tree: many ~KB scripts with dense DO/GOSUB — see the work-profile scan).
/// </summary>
public class CmmTraceClosureTests
{
    // A realistic PRACTICE web: an entry that DOs/GOSUBs into a chain, a dynamic DO (runtime-chosen board),
    // board scripts only reachable that way, and a genuinely orphaned script.
    private static readonly (string Rel, string Text)[] Tree =
    {
        ("scripts/main.cmm",   "DO init\n  GOSUB Boot\nBoot:\n  DO flash\n  RETURN\n"),   // seed
        ("scripts/init.cmm",   "DO common\n  DO &board\n"),                               // dynamic dispatch
        ("scripts/flash.cmm",  "Flash:\n  DO common\n  RETURN\n"),
        ("scripts/common.cmm", "Common:\n  RETURN\n"),
        ("scripts/orphan.cmm", "Orphan:\n  GOSUB Nowhere\n  RETURN\n"),                   // reached by nobody
        ("boards/board_a.cmm", "A:\n  RETURN\n"),                                         // only via DO &board
        ("boards/board_b.cmm", "B:\n  RETURN\n"),
    };

    private static CmmClosureResult Run(IEnumerable<string> observed, long cap = 1_000_000,
        IDictionary<string, long>? sizeOverride = null)
    {
        var map = Tree.ToDictionary(t => t.Rel, t => t.Text, StringComparer.OrdinalIgnoreCase);
        var all = Tree.Select(t => (t.Rel, Bytes: (long)(sizeOverride != null && sizeOverride.TryGetValue(t.Rel, out var b) ? b : t.Text.Length))).ToList();
        return CmmTraceClosure.Compute(all, observed.ToList(), rel => map[rel], cap);
    }

    [Fact]
    public void ObservedSeed_KeepsStaticClosure_DropsUnreachable()
    {
        var r = Run(new[] { "scripts/main.cmm" });

        // main -> init (DO) -> common (DO); main.Boot -> flash (DO) -> common (DO). All kept.
        Assert.Contains("scripts/main.cmm", r.Kept);
        Assert.Contains("scripts/init.cmm", r.Kept);
        Assert.Contains("scripts/flash.cmm", r.Kept);
        Assert.Contains("scripts/common.cmm", r.Kept);

        // init.cmm is KEPT and does `DO &board` (dynamic): we can't see what it runs, so nothing is dropped
        // (owner decision D-C) and the reason is surfaced.
        Assert.Empty(r.Dropped);
        Assert.Contains(r.Warnings, w => w.Contains("dropping nothing"));

        Assert.Equal(1, r.ObservedSeeds);
        Assert.Equal(3, r.ClosureAdded);              // init, flash, common
        Assert.Equal(7, r.Total);
    }

    [Fact]
    public void DynamicDo_InKeptScript_IsSurfaced_NotSilentlyDropped()
    {
        var r = Run(new[] { "scripts/main.cmm" });
        // init.cmm is kept and contains `DO &board` -> the unresolved dynamic dispatch must be warned about
        // (so the user can widen the trace or forceKeep the board scripts), never swallowed.
        Assert.Contains(r.Warnings, w => w.Contains("init.cmm") && w.Contains("&board"));
    }

    [Fact]
    public void NoObservedSeed_KeepsEverything_SoundDefault()
    {
        var r = Run(Array.Empty<string>());
        Assert.Equal(7, r.Kept.Count);
        Assert.Empty(r.Dropped);
        Assert.Equal(0, r.ObservedSeeds);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void OversizedSeed_IsKeptWhole_AndWarned_NotParsed()
    {
        // A 300 MB script is kept whole (not DO-parsed) — the memory backstop — and flagged so the user knows
        // its closure wasn't followed. Its static DO targets therefore can't be auto-kept.
        var big = new Dictionary<string, long> { ["scripts/main.cmm"] = 300L * 1024 * 1024 };
        var r = Run(new[] { "scripts/main.cmm" }, cap: 1_000_000, sizeOverride: big);

        Assert.Contains("scripts/main.cmm", r.Kept);              // still kept (it was observed)
        Assert.Equal(1, r.OversizedKeptWhole);
        Assert.Contains(r.Warnings, w => w.Contains("main.cmm") && w.Contains("kept whole"));
        // Not parsed => its `DO init` edge isn't followed => init is NOT auto-kept (the documented trade-off).
        Assert.DoesNotContain("scripts/init.cmm", r.Kept);
    }
}
