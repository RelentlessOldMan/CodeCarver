using CodeCarver.Core.Reachability;
using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>
/// What keeps the link check's memory flat over a big tree: results streamed in order with a bounded lead, scans held
/// to a memory budget, and a function-like macro's visibility asked per name (a set of every visible macro per file was
/// files x macros). Each must change only how much is held at once, never what the check finds.
/// </summary>
public sealed class StreamingCheckTests
{
    [Fact]
    public void Ordered_HandsBackEveryResultInOrder_NeverStartingMoreThanAheadPastTheCaller()
    {
        var items = Enumerable.Range(0, 2000).ToList();
        const int ahead = 16;
        var cursor = 0;
        var tooFar = 0;
        var seen = new List<int>();
        foreach (var (i, v) in ReadAhead.Ordered(items, x =>
                 {
                     // the caller has taken item cursor - 1 and is handed the next: up to cursor + ahead may start
                     if (x > Volatile.Read(ref cursor) + ahead) Interlocked.Increment(ref tooFar);
                     if (x % 97 == 0) Thread.Sleep(5);   // slow ones finish after later ones
                     return x * 3;
                 }, ahead))
        {
            Assert.Equal(i * 3, v);
            seen.Add(i);
            Volatile.Write(ref cursor, i + 1);
        }
        Assert.Equal(items, seen);
        Assert.Equal(0, tooFar);
    }

    [Fact]
    public void Ordered_AFailingItem_IsThrownToTheCaller()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in ReadAhead.Ordered(Enumerable.Range(0, 500).ToList(),
                         x => x == 123 ? throw new InvalidOperationException("boom") : x)) { }
        });
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Ordered_LeftEarly_StopsItsWorkers()
    {
        var ran = 0;
        foreach (var (i, _) in ReadAhead.Ordered(Enumerable.Range(0, 100_000).ToList(), x => Interlocked.Increment(ref ran), 32))
            if (i == 10) break;
        Assert.True(Volatile.Read(ref ran) < 10 + 32 + ReadAhead.Window + 1, ran.ToString());
    }

    [Fact]
    public void MemoryGate_PiecesOverTheBudget_WaitForEachOther_AndOneBiggerThanItRunsAlone()
    {
        var gate = new MemoryGate(budgetBytes: 1000, bytesPerChar: 10, minChars: 10);
        using (var a = gate.Acquire(60))   // 600
        {
            var second = Task.Run(() => { using var b = gate.Acquire(60); });
            Assert.False(second.Wait(200));   // 1200 > 1000: waits
            a.Release();
            Assert.True(second.Wait(5000));
        }
        using (gate.Acquire(5)) using (gate.Acquire(5)) { }   // under minChars: not counted
        using var huge = gate.Acquire(1_000_000);              // bigger than the budget: runs once nothing else does
        Assert.False(Task.Run(() => { using var c = gate.Acquire(20); }).Wait(200));
    }

    [Fact]
    public void LinkCheck_AMacroIsSeenThroughIncludes_AndOnlyThere()
    {
        // helper() is a function-like macro in inc/m.h. a.c reaches it through mid.h; b.c includes nothing, so its
        // helper() call names the dropped function.
        var root = Path.Combine(Path.GetTempPath(), "cc-vis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inc"));
        try
        {
            (string, string) W(string rel, string text)
            {
                var p = Path.Combine(root, rel);
                File.WriteAllText(p, text);
                return (rel, p);
            }
            var emitted = new[]
            {
                W("inc/m.h", "#define helper(x) ((x) + 1)\n"),
                W("inc/mid.h", "#include \"m.h\"\n"),
                W("a.c", "#include \"inc/mid.h\"\nint a(void) { return helper(1); }\n"),
                W("b.c", "int b(void) { return helper(2); }\n"),
            };
            var dropped = new[] { W("d.c", "int helper(int x) { return x; }\n") };
            var r = EmittedLinkCheck.Run(emitted, dropped);
            var v = Assert.Single(r.Violations);
            Assert.Equal("helper", v.Name);
            Assert.Equal("b.c", v.ReferencedIn);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Theory]
    [InlineData(null)]   // headers held
    [InlineData("0")]    // no header text held: every walk reads its headers again
    public void LinkCheck_TemplateHeaders_SameAnswerWhetherHeldOrReadAgain_AndRedoneWhenAHeaderChanges(string? budgetMb)
    {
        // t.h defines a function through a macro-named head; cfg.h says what the macro makes of it. The dropped gone.c
        // instantiates it, so main.c's call names a dropped definition. A cached answer (the second run) must follow
        // cfg.h when it changes, though no header's text is kept from the first run.
        var root = Path.Combine(Path.GetTempPath(), "cc-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var was = Environment.GetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB");
        Environment.SetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB", budgetMb);
        try
        {
            (string, string) W(string rel, string text)
            {
                var p = Path.Combine(root, rel);
                File.WriteAllText(p, text);
                return (rel, p);
            }
            var cfg = W("cfg.h", "#define HIDE(n) hid_##n\n");
            var emitted = new[]
            {
                cfg,
                W("t.h", "int HIDE(den)(void) { return 0; }\n"),
                W("main.c", "int hid_den(void);\nint vis_den(void);\nint main(void) { return hid_den() + vis_den(); }\n"),
            };
            var dropped = new[] { W("gone.c", "#include \"cfg.h\"\n#include \"t.h\"\n") };
            var cache = new LinkCheckCache(p => p.EndsWith("gone.c", StringComparison.Ordinal));
            var first = EmittedLinkCheck.Run(emitted, dropped, cache: cache);
            Assert.Equal(new[] { "hid_den" }, first.Violations.Select(v => v.Name).ToArray());

            File.WriteAllText(cfg.Item2, "#define HIDE(n) vis_##n\n");
            var second = EmittedLinkCheck.Run(emitted, dropped, cache: cache);
            Assert.Equal(new[] { "vis_den" }, second.Violations.Select(v => v.Name).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB", was);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
