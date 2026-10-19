using System.Text;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// Memory per byte of input, not absolute sizes: a walk that allocates 9 times the headers it pulls in looks harmless over
/// a few MB of headers and stops a work run at 21 GB over 400 MB of them, 16 walks at once (C22). These trees are
/// shaped like that one (a unit pulling in a chain of register-map headers, long #define tables with comments, and a
/// template header), small enough for the suite; the ratios hold at any size.
/// The link check's part stats and its budgets (environment variables) are process-wide, so these run alone.
/// </summary>
[Collection(nameof(TemplateWalkMemoryTests))]
public class TemplateWalkMemoryTests
{
    // Register-map headers: count of them, #defines in each.
    static Dictionary<string, string> RegisterHeaders(int headers, int defines, string tag)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var h = 0; h < headers; h++)
        {
            var sb = new StringBuilder();
            sb.Append($"#ifndef {tag}_R{h}_H\n#define {tag}_R{h}_H\n");
            for (var k = 0; k < defines; k++)
                sb.Append($"#define {tag}{h}_R{k}_OFS ({tag}{h}_BASE + 0x{k * 4:X}u) /* reg {k} */\n");
            sb.Append("#endif\n");
            map[$"regs/r{h}.h"] = sb.ToString();
        }
        return map;
    }

    [Fact]
    public void TemplateWalk_AllocatesLessThanItsHeaders_FirstParseAndCached()
    {
        // 48 headers x 4,000 defines: 12 M chars of directive text, one unit pulling in all of it, then the template.
        // Before C22's fix the first walk allocated about 10 bytes a char (a copy of every line, name and body, a
        // string-keyed index), kept with the header for as long as it was held.
        var headers = RegisterHeaders(48, 4000, "WMA");
        headers["cfg.h"] = "#define HIDE(n) hid_##n\n";
        headers["t.h"] = "int HIDE(den)(void) { return 0; }\n";
        var unit = "#include \"cfg.h\"\n" + string.Concat(Enumerable.Range(0, 48).Select(h => $"#include \"regs/r{h}.h\"\n")) + "#include \"t.h\"\n";
        long chars = headers.Values.Sum(t => (long)t.Length);
        string? Read(string raw) => headers.TryGetValue(raw, out var t) ? t : null;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = TemplateInstances.Find(unit, Read);
        var first = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(found, f => f.Name == "hid_den");
        Assert.True(first <= 2 * chars, $"first walk: {first / (double)chars:F2} bytes a char of header (limit 2)");

        before = GC.GetAllocatedBytesForCurrentThread();
        TemplateInstances.Find(unit, Read);
        var cached = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(cached <= chars / 10, $"cached walk: {cached / (double)chars:F3} bytes a char of header (limit 0.1)");
        GC.KeepAlive(headers);
    }

    [Fact]
    public void TemplateWalk_ReportsWhatItHolds_NotWhatItAllocated()
    {
        // The link check lets walks run at once by what each holds (EmittedLinkCheck.WalkCost): the walk's own tables
        // come to a small part of the texts it walked, and grow with the names it looked up.
        var headers = RegisterHeaders(16, 2000, "WHB");
        headers["cfg.h"] = "#define HIDE(n) hid_##n\n";
        headers["t.h"] = "int HIDE(den)(void) { return WHB0_R7_OFS; }\n";
        var unit = "#include \"cfg.h\"\n" + string.Concat(Enumerable.Range(0, 16).Select(h => $"#include \"regs/r{h}.h\"\n")) + "#include \"t.h\"\n";
        long chars = headers.Values.Sum(t => (long)t.Length);
        string? Read(string raw) => headers.TryGetValue(raw, out var t) ? t : null;

        long held = -1;
        var found = TemplateInstances.Find(unit, Read, held: b => held = b);
        Assert.Contains(found, f => f.Name == "hid_den");
        Assert.InRange(held, 1, chars / 4);
        Assert.True(EmittedLinkCheck.WalkCost(chars, held, 0) >= 4 * chars, "a walk is charged at least its texts and their parses, twice");
    }

    [Fact]
    public void LinkCheck_TemplateWalks_AllocateAboutTheirHeaders_HeldOrReRead()
    {
        // 16 dropped units over overlapping 24-header windows of 48 register headers (each ~260 K chars), and the
        // template: the walk stats per loop (part.templateWalk) against what the walks pulled in.
        var root = Path.Combine(Path.GetTempPath(), "cc-wmem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "regs"));
        var was = (Environment.GetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB"), Environment.GetEnvironmentVariable("CODECARVER_CHECK_WALK_MB"));
        try
        {
            (string, string) W(string rel, string text)
            {
                var p = Path.Combine(root, rel);
                File.WriteAllText(p, text);
                return (rel, p);
            }
            var regs = RegisterHeaders(48, 4000, "WMB");
            var named = 16 * 24 * regs.Values.First().Length >> 20;   // M chars of register headers the walks include
            var emitted = regs.Select(kv => W(kv.Key, kv.Value)).ToList();
            emitted.Add(W("cfg.h", "#define HIDE(n) hid_##n\n"));
            emitted.Add(W("t.h", "int HIDE(den)(void) { return 0; }\n"));
            emitted.Add(W("main.c", "int hid_den(void);\nint main(void) { return hid_den(); }\n"));
            var dropped = Enumerable.Range(0, 16).Select(u => W($"gone{u}.c",
                "#include \"cfg.h\"\n" + string.Concat(Enumerable.Range(0, 24).Select(j => $"#include \"regs/r{(u * 2 + j) % 48}.h\"\n")) + "#include \"t.h\"\n")).ToArray();

            foreach (var headerMb in new[] { (string?)null, "0" })   // held, then every header read again by each walk
            {
                Environment.SetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB", headerMb);
                Environment.SetEnvironmentVariable("CODECARVER_CHECK_WALK_MB", null);
                var r = EmittedLinkCheck.Run(emitted, dropped);
                Assert.True(r.Stats.ContainsKey("dropped.part.templateWalk.allocatedMb"), string.Join(", ", r.Stats.Keys.Where(k => k.Contains("part."))));
                Assert.Equal(new[] { "hid_den" }, r.Violations.Select(v => v.Name).Distinct().ToArray());
                var alloc = r.Stats["dropped.part.templateWalk.allocatedMb"];
                var pulled = r.Stats["dropped.part.templateWalk.pulledInMb"];   // MB of chars
                // The walks look up none of the register names, so each header comes to them as its guard alone.
                Assert.True(pulled <= 1, $"pulled in {pulled} MB");
                // Held: nothing of the headers is read or parsed again (before 1.0.295 each was held whole and parsed once,
                // about 0.8 bytes a char). Read again: the file's bytes, its string and its directive lines (about 3 bytes
                // a char), each walk; before C22's fixes 10 bytes a char and a parse of it all.
                var limit = headerMb is null ? 0.05 : 4.0;
                Assert.True(alloc <= limit * named, $"header MB {headerMb ?? "default"}: walks allocated {alloc} MB for {named} M chars of headers named (limit {limit} a char)");
                Assert.Equal(0, r.Stats["templateWalksRedone"]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB", was.Item1);
            Environment.SetEnvironmentVariable("CODECARVER_CHECK_WALK_MB", was.Item2);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

[CollectionDefinition(nameof(TemplateWalkMemoryTests), DisableParallelization = true)]
public class TemplateWalkMemoryCollection { }
