using System.Text;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Emit;

/// <summary>Outcome of carving big headers: bytes before/after and how many #defines were kept vs dropped.</summary>
public sealed record HeaderCarveResult(long BytesBefore, long BytesAfter, int DefinesKept, int DefinesDropped)
{
    /// <summary>Reads of the big headers the fixpoint took (each pass reads every one).</summary>
    public int Passes { get; init; }

    /// <summary>Names defined in the carved headers that are paste macros, and that have a forwarding definition there (a
    /// function-like define with a call in its body that pastes no parameter itself).</summary>
    public int PasteMacros { get; init; }
    public int ForwardingMacros { get; init; }

    /// <summary>Milliseconds per part: seed, bigDefines, pasteCalls, fixpoint, emit.</summary>
    public IReadOnlyList<(string Part, long Ms)> PartMs { get; init; } = Array.Empty<(string, long)>();

    /// <summary>The part the carve gave up in, past its memory or time limit (the headers are left whole); null when it finished.</summary>
    public string? AbandonedIn { get; init; }

    /// <summary>Private memory (MB) when it gave up.</summary>
    public long AbandonedPrivateMb { get; init; }

    /// <summary>It gave up past its time limit, not its memory limit.</summary>
    public bool AbandonedOverTime { get; init; }
}

/// <summary>
/// EXPERIMENTAL header carving: strips unused <c>#define</c> macros from the giant auto-generated register
/// headers that <see cref="FileTreeEmitter"/> keeps whole (multi-GB files past <c>--max-parse-bytes</c>).
/// A carve that needs a handful of registers shouldn't drag in a million unrelated ones.
///
/// It is SOUND-by-construction and STREAMING (bounded memory, never loads the file):
///   • Everything that is NOT an object/function-like <c>#define</c> is kept verbatim — include guards,
///     <c>#if/#endif</c>, <c>#include</c>, typedefs, structs, comments. So conditional structure and types
///     are never disturbed.
///   • A <c>#define NAME …</c> is kept iff NAME is in the transitively-needed set; otherwise dropped
///     (with its backslash-continuation lines).
///   • "Needed" seeds from every identifier used by the kept code, then grows to a fixpoint: if a needed
///     define's body references other names, those become needed too (register offsets built from a base
///     address, masks built from shifts). Identifiers in the header's own <c>#if/#ifdef/#elif</c>
///     conditions are always needed, so branch selection can't change.
///
/// Over-keeping is the safe side of "must build": anything uncertain (any non-#define line, any name we
/// can't rule out) is kept. Still EXPERIMENTAL — always build-verify the result.
/// </summary>
public static partial class HeaderCarver
{
    /// <summary>
    /// Carve the given big headers (relative paths) in place under <paramref name="outDir"/>, dropping
    /// #defines not transitively needed by the other kept files there. Returns aggregate size/keep stats.
    /// </summary>
    /// <param name="extraNames">Names used where the carved tree can't show them: an SDK header outside it that
    /// tests a macro, the build's command line (<c>-DBAUD=UART_DIV_115200</c>).</param>
    /// <param name="partDone">Called as each part ends with its numbers (<c>&lt;part&gt;.ms</c>, <c>.cpuMs</c> (process CPU), <c>.allocatedMb</c>, <c>.privateMb</c>, <c>.liveMb</c>):
    /// a memory stop later in the carve still has them.</param>
    /// <param name="elsewhereDefines">#defines in effect that the carved tree doesn't hold: the build's -D values
    /// (<c>#define N 2</c>) and the defines of headers read outside it. A paste macro among them is one, and what one
    /// expands to can be a paste's piece (<c>XCAT(REG_, N)</c> builds REG_2).</param>
    public static HeaderCarveResult Carve(string outDir, IReadOnlyCollection<string> bigHeaderRels, IEnumerable<string>? extraNames = null,
                                          Action<string, long>? partDone = null, IEnumerable<string>? elsewhereDefines = null,
                                          TimeSpan? timeLimit = null)
    {
        // Past its memory limit the carve gives up before it rewrites a header, so they stay whole (as a carve that drops
        // nothing leaves them) and the run goes on: work's 1.0.330 max stage stopped the whole run at 20 GB in the
        // fixpoint, when its paste pieces grew from 0.5M to 4.5M between two passes.
        // Past its time limit it gives up the same way: work's 1.0.345 max stage read its big headers for 2 hours and
        // kept every one of 122M defines.
        var limit = new MemoryLimit(LimitBytes(), timeLimit is { } t && t > TimeSpan.Zero
            ? System.Diagnostics.Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * System.Diagnostics.Stopwatch.Frequency) : 0);
        var part = "seed";
        var passes = 0;   // those begun: a carve given up in its fixpoint says how far it got
        try { return CarveWithin(outDir, bigHeaderRels, extraNames, partDone, elsewhereDefines, limit, p => part = p, n => passes = n); }
        catch (Exception ex) when (OverLimit(ex) is { } over)
        {
            var overTime = over is OverTimeLimit;
            CodeCarver.Core.Util.Activity.Doing(null);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);   // what the carve held, back
            long bytes = 0;
            foreach (var rel in bigHeaderRels) bytes += new FileInfo(Path.Combine(outDir, rel)).Length;
            partDone?.Invoke("abandoned", 1);
            partDone?.Invoke("abandonedPrivateMb", over.PrivateBytes / (1024 * 1024));
            partDone?.Invoke("abandonedOverTime", overTime ? 1 : 0);
            partDone?.Invoke("limitMb", limit.Bytes / (1024 * 1024));
            if (timeLimit is { } tl) partDone?.Invoke("limitMinutes", (long)tl.TotalMinutes);
            return new HeaderCarveResult(bytes, bytes, 0, 0)
            {
                Passes = passes, AbandonedIn = part, AbandonedPrivateMb = over.PrivateBytes / (1024 * 1024), AbandonedOverTime = overTime,
            };
        }
    }

    /// <summary>The most private memory the header carve may reach: nine tenths of memoryStopMb (or of the machine when
    /// none is set), or CODECARVER_HEADER_CARVE_MB.</summary>
    static long LimitBytes() =>
        long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_HEADER_CARVE_MB"), out var mb) && mb >= 0
            ? mb * 1024 * 1024 : CodeCarver.Core.Util.MemoryGate.BudgetBytes() / 10 * 9;

    static OverLimitBase? OverLimit(Exception ex) => ex switch
    {
        OverMemoryLimit o => o,
        OverTimeLimit o => o,
        AggregateException a => a.InnerExceptions.Select(OverLimit).FirstOrDefault(o => o is not null),
        _ => null,
    };

    private abstract class OverLimitBase(string message, long privateBytes) : Exception(message)
    {
        public long PrivateBytes { get; } = privateBytes;
    }

    private sealed class OverMemoryLimit(long privateBytes) : OverLimitBase("the header carve passed its memory limit", privateBytes);

    private sealed class OverTimeLimit(long privateBytes) : OverLimitBase("the header carve passed its time limit", privateBytes);

    /// <summary>
    /// Private memory against the carve's limit, looked at a few times a second from any thread. Past it, one full
    /// compacting collection (much of a pass's memory is garbage the GC has not handed back yet); still past, the carve
    /// ends (<see cref="OverMemoryLimit"/>). Past <paramref name="deadline"/> (a Stopwatch timestamp; 0 for none) it ends too
    /// (<see cref="OverTimeLimit"/>).
    /// </summary>
    private sealed class MemoryLimit(long bytes, long deadline = 0)
    {
        public long Bytes { get; } = bytes;
        private readonly object gate = new();
        private long nextAt;

        public void Check()
        {
            if ((Bytes <= 0 && deadline == 0) || System.Diagnostics.Stopwatch.GetTimestamp() < Volatile.Read(ref nextAt)) return;
            lock (gate)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < nextAt) return;
                Volatile.Write(ref nextAt, now + System.Diagnostics.Stopwatch.Frequency / 5);
                if (deadline != 0 && now > deadline) throw new OverTimeLimit(Private());
                if (Bytes <= 0) return;
                if (Private() <= Bytes) return;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                var after = Private();
                if (after > Bytes) throw new OverMemoryLimit(after);
                // Collected back under: not again for a while (a carve near its limit must not spend its time collecting).
                Volatile.Write(ref nextAt, System.Diagnostics.Stopwatch.GetTimestamp() + 2 * System.Diagnostics.Stopwatch.Frequency);
            }
        }

        private static long Private()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.PrivateMemorySize64;
        }
    }

    private static HeaderCarveResult CarveWithin(string outDir, IReadOnlyCollection<string> bigHeaderRels, IEnumerable<string>? extraNames,
                                                 Action<string, long>? partDone, IEnumerable<string>? elsewhereDefines, MemoryLimit limit,
                                                 Action<string> inPart, Action<int> inPass)
    {
        var bigSet = new HashSet<string>(bigHeaderRels, StringComparer.OrdinalIgnoreCase);
        var bigFull = bigHeaderRels.Select(r => Path.Combine(outDir, r)).ToList();
        // Files are read on many threads (one at a time, a work-scale output of 47 GB and GBs of big headers read
        // again each pass took hours); what they found is folded in in file order, so the result is the same.
        CodeCarver.Core.Util.Activity.Begin("files read for the header carve");
        var partMs = new List<(string, long)>();
        var partClock = System.Diagnostics.Stopwatch.StartNew();
        // What each part holds as it ends: private memory, then the heap after a full collection (work's max stage stopped
        // at 20 GB in the fixpoint, 3.9 GB of it from the steps before: which part kept the rest?).
        long partCpuAt, partAllocatedAt = GC.GetTotalAllocatedBytes();
        using (var p = System.Diagnostics.Process.GetCurrentProcess()) partCpuAt = (long)p.TotalProcessorTime.TotalMilliseconds;
        void PartDone(string part)
        {
            var ms = partClock.ElapsedMilliseconds;
            partMs.Add((part, ms));
            if (partDone is not null)
            {
                partDone($"{part}.ms", ms);
                var allocated = GC.GetTotalAllocatedBytes();
                partDone($"{part}.allocatedMb", (allocated - partAllocatedAt) / (1024 * 1024));
                partAllocatedAt = allocated;
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    var cpu = (long)p.TotalProcessorTime.TotalMilliseconds;
                    partDone($"{part}.cpuMs", cpu - partCpuAt);
                    partDone($"{part}.privateMb", p.PrivateMemorySize64 / (1024 * 1024));
                }
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                partDone($"{part}.liveMb", GC.GetTotalMemory(false) / (1024 * 1024));
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) partCpuAt = (long)p.TotalProcessorTime.TotalMilliseconds;   // not the collection
            }
            partClock.Restart();
            inPart(part switch { "seed" => "bigDefines", "bigDefines" => "pasteCalls", "pasteCalls" => "fixpoint", _ => "emit" });
        }

        // 1. Seed: every identifier used by kept code that ISN'T one of the carved headers. That is the set
        //    of names the compiled output could reference; a define outside its closure is unreachable.
        //    Every TEXT file in the output counts — assembly, linker scripts, .inc tables, scripts — not only C:
        //    a define used only by startup.S or a linker script is still needed (review H1). The caller runs this
        //    after the infrastructure copy so those files are present. Binary files are skipped.
        //    A name a paste builds (`REG_##n##_BASE` makes REG_0_BASE) never appears literally: rule 3 of
        //    docs/TESTING.md keeps the names ONE paste use builds (see UseEngine), from the calls in the kept files and
        //    in the big headers' kept lines, through the paste macros every define in reach makes.
        // Names by key (see NameKey): work held 8M of them as strings. Two names that share a key are both needed.
        var needed = new HashSet<ulong>((extraNames ?? Array.Empty<string>()).Select(n => NameKey.Short(n)));
        var facts = new DefineFacts();
        var pasterCount = 0;
        // The kept files' and the build's defines: what a paste use instantiates. The big headers' are read when a use
        // needs one (UseEngine.Asked).
        var texts = new Dictionary<ulong, List<string>>();
        // Hidden files count (a .config, a dot-named generated header): only the system ones are skipped.
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var candidates = Directory.EnumerateFiles(outDir, "*", walk)
            .Where(f => !bigSet.Contains(Path.GetRelativePath(outDir, f).Replace('\\', '/'))).ToList();
        var callFiles = new List<string>();
        var hashHashFiles = new HashSet<string>(StringComparer.Ordinal);
        UseEngine? engine = null;
        UseResults? built = null;   // what the uses built, once the engine is let go
        // The big headers read so far this pass, of how many: a pass held up reading them shows how far it got (work's
        // sat 18 minutes at "pass 1" with nothing on the line moving).
        var headersRead = 0;
        var headersAll = 0;
        string Learned() => engine is null
            ? $"{pasterCount:N0} paste macros, {needed.Count:N0} names"
            : $"{facts.Pasters.Count:N0} paste macros, {needed.Count:N0} names, {engine.Uses:N0} paste uses, " +
              $"{engine.Results.Exact.Count:N0} names built, {engine.Results.Patterns:N0} patterns, {engine.Results.Pools:N0} pools, " +
              $"{engine.Runs:N0} use runs" + (engine.Left > 0 ? $" ({engine.Left:N0} to go)" : "") +
              (engine.OverWork > 0 ? $", {engine.OverWork:N0} over the work limit" : "") +
              (headersAll > 0 ? $", {Volatile.Read(ref headersRead):N0} of {headersAll:N0} big headers read" : "");
        CodeCarver.Core.Util.Activity.Doing("reading the kept files for names and defines", Learned);
        foreach (var (i, seen) in CodeCarver.Core.Util.ReadAhead.Ordered(candidates, SeedScan, CodeCarver.Core.Util.ReadAhead.Window))
        {
            limit.Check();
            if (seen.Names is null) continue;   // binary
            needed.UnionWith(seen.Names);
            facts.Merge(seen.Facts);
            foreach (var (key, text) in seen.Defines) AddText(texts, key, text);
            if (!StreamsCalls(candidates[i])) continue;
            callFiles.Add(candidates[i]);
            if (seen.HashHash) hashHashFiles.Add(candidates[i]);
        }
        var elsewhere = (elsewhereDefines ?? Array.Empty<string>()).Select(Digraphs).ToList();
        var def = new PpDefine();
        var elsewhereFacts = new FileFacts();
        foreach (var line in elsewhere)
        {
            if (!def.Parse(line)) continue;
            elsewhereFacts.Add(def, big: false);
            AddText(texts, NameKey.Short(def.NameSpan), line);
        }
        facts.Merge(elsewhereFacts);
        PartDone("seed");

        // Rule 1 over every define in reach: which names are paste macros, which of them something calls, which names can
        // expand to nothing. The big headers' defines are held as name keys only.
        CodeCarver.Core.Util.Activity.Doing("learning paste macros from the big headers", Learned);
        foreach (var (_, f) in CodeCarver.Core.Util.ReadAhead.Ordered(bigFull, BigFacts, CodeCarver.Core.Util.ReadAhead.Window))
        {
            limit.Check();
            facts.Merge(f);
        }
        facts.Resolve();
        PartDone("bigDefines");

        // The kept files' paste uses and chains, and the build's defines' (each a body of its own).
        CodeCarver.Core.Util.Activity.Doing("reading paste uses in the kept files", Learned);
        engine = new UseEngine(facts, texts) { Tick = limit.Check };
        if (bigFull.Count == 0) engine.NothingToAsk();
        var pasters = facts.Pasters;
        pasterCount = pasters.Count;
        var streamed = pasters.Count > 0 ? callFiles : callFiles.Where(hashHashFiles.Contains).ToList();
        foreach (var (_, ev) in CodeCarver.Core.Util.ReadAhead.Ordered(streamed, f => StreamKept(f, pasters), CodeCarver.Core.Util.ReadAhead.Window))
        {
            limit.Check();
            if (ev is not null) engine.Fold(ev);
        }
        if (elsewhere.Count > 0)
        {
            var ev = new UseEvents(pasters);
            var bodies = new UseTracker(ev);
            foreach (var line in elsewhere) if (def.Parse(line)) StreamBody(bodies, def);
            engine.Fold(ev);
        }
        engine.Drain();
        PartDone("pasteCalls");

        bool Wanted(string name)
        {
            var key = NameKey.Short(name);
            if (needed.Contains(key) || ReadBySystemHeaders(name)) return true;
            var r = built ?? engine!.Results;
            return r.Exact.Contains(key) || r.PatternMatch(name) || r.PoolMatch(name);
        }
        // Why the emit kept each define: its name is needed, the compiler's headers read it, a paste use builds it, a
        // pattern of one fits it (an unknown or __LINE__ slot), or a use's own pool does (a computed call, one open
        // across a conditional, or one past the cap).
        long keptByName = 0, keptBySystem = 0, keptByUse = 0, keptByPattern = 0, keptByPool = 0;
        bool WantedCounted(string name)
        {
            var key = NameKey.Short(name);
            if (needed.Contains(key)) { Interlocked.Increment(ref keptByName); return true; }
            if (ReadBySystemHeaders(name)) { Interlocked.Increment(ref keptBySystem); return true; }
            var r = built ?? engine!.Results;
            if (r.Exact.Contains(key)) { Interlocked.Increment(ref keptByUse); return true; }
            if (r.PatternMatch(name)) { Interlocked.Increment(ref keptByPattern); return true; }
            if (r.PoolMatch(name)) { Interlocked.Increment(ref keptByPool); return true; }
            return false;
        }

        // 2. Fixpoint: grow `needed` with the bodies of wanted defines and the big headers' own non-define lines (kept
        //    verbatim: typedefs, enums, inline functions, #if conditions, so branch selection can't change), and grow
        //    what the paste uses build with the uses those lines and bodies make. Terminates because both only grow
        //    and are finite. Passes ≈ the deepest define-dependency chain (small for register maps).
        //    Headers are read in batches on many threads against what is wanted so far, and what they hand back is
        //    folded in in order. The first pass takes every kept line; a later one only the defines wanted, but those
        //    taken two passes back (a name taken in a pass may have been passed over in a batch before it became
        //    wanted). A use that met a name whose big-header defines were not read yet asks for them: each batch hands
        //    them back, and the use runs again with them.
        var batches = Batches(bigFull, BatchBytes.Value ?? 256L << 20);
        // Defines taken, by key (see NameKey): work took 18.9M in one pass, held as strings.
        var done = new HashSet<UInt128>();
        var takenBefore = new HashSet<UInt128>();
        var passes = 0;
        var forwardsNeeded = new HashSet<ulong>();
        void NeedForwards(ulong name)
        {
            if (forwardsNeeded.Add(name)) facts.ForwardsTo(name, k => { needed.Add(k); return forwardsNeeded.Add(k); });
        }
        foreach (var name in needed.Where(pasters.Contains).ToList()) NeedForwards(name);
        while (true)
        {
            passes++;
            inPass(passes);
            CodeCarver.Core.Util.Activity.Doing($"reading the big headers for what they need, pass {passes}", Learned);
            var first = passes == 1;
            var grew = false;
            var taken = new HashSet<UInt128>();
            List<ulong>? newPasters = null;
            var versionAt = engine.Results.Version;
            var capturedAt = engine.Captured;
            var runsAt = engine.Runs;
            headersRead = 0;
            headersAll = bigFull.Count;
            foreach (var batch in batches)
            {
                var serial = engine.BatchBegin();
                var skip = first ? null : done;
                var asked = engine.Asked;
                // Nothing wanted, built or asked for changes while a batch is read: each reader hands back only what its
                // file adds (work's first pass held its kept lines' text, a batch at a time, and stopped at 20 GB).
                foreach (var take in CodeCarver.Core.Util.ReadAhead.Each(batch, path => Take(path, first, skip, Wanted, needed, pasters, asked, limit)))
                {
                    limit.Check();
                    headersRead++;
                    foreach (var name in take.Names)
                        if (needed.Add(name))
                        {
                            grew = true;
                            if (pasters.Contains(name)) (newPasters ??= new()).Add(name);
                        }
                    foreach (var key in take.Defines) taken.Add(key);
                    if (take.Captured is not null)
                        foreach (var (key, text) in take.Captured) engine.Capture(key, text);
                    if (take.Events is not null) engine.Fold(take.Events);
                }
                engine.BatchEnd(serial, batches.Count);
            }
            // A paste macro needed needs every paste macro it forwards to, names in its body or theirs: each link of a
            // chain of 40 forwarders took a pass of its own.
            if (newPasters is not null)
                foreach (var name in newPasters) NeedForwards(name);
            // Every stream has been read: a paste macro named and never called is used with wildcards.
            if (first)
            {
                engine.PastersRead();
                engine.CalledFinal();
                engine.Drain();
            }
            if (partDone is not null)
            {
                var at = $"pass{passes}";
                partDone($"{at}.ms", partClock.ElapsedMilliseconds);
                partDone($"{at}.names", needed.Count);
                partDone($"{at}.definesTaken", taken.Count);
                partDone($"{at}.namesBuilt", engine.Results.Exact.Count);
                partDone($"{at}.patterns", engine.Results.Patterns);
                partDone($"{at}.pools", engine.Results.Pools);
                partDone($"{at}.uses", engine.Uses);
                partDone($"{at}.useRuns", engine.Runs - runsAt);
                partDone($"{at}.definesCaptured", engine.Captured - capturedAt);
                partDone($"{at}.asked", engine.Asked.Count);
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) partDone($"{at}.privateMb", p.PrivateMemorySize64 / (1024 * 1024));
            }
            done.UnionWith(takenBefore);
            takenBefore = taken;
            if (!grew && engine.Results.Version == versionAt && !engine.Pending) break;
        }

        PartDone("fixpoint");
        headersAll = 0;
        // The rewrite needs only what the uses built. Every closure of this method shares one frame, so the engine (its
        // uses, captured texts and runs), the kept files' define texts and the big headers' define facts stayed live
        // through it: 85 MB more after the fixpoint on a 1.2M-define tree.
        built = engine.Results;
        var counts = (Uses: engine.Uses, Chains: engine.Chains, Runs: engine.Runs, OverWork: engine.OverWork,
            Captured: engine.Captured, Defines: facts.Defines, Edges: facts.Edges, Called: facts.CalledPasters.Count,
            Emptyable: facts.Emptyable.Count, Forwarding: facts.ForwardingMacros);
        engine = null;
        texts = null!;
        facts = null!;
        done = null!;
        takenBefore = null!;
        // 3. Emit: rewrite each header, dropping #defines whose name isn't wanted (and their continuations).
        CodeCarver.Core.Util.Activity.Doing("rewriting the big headers", Learned);
        var rewritten = CodeCarver.Core.Util.ReadAhead.Each(bigFull, path =>
        {
            var before = new FileInfo(path).Length;
            var tmp = path + ".carve.tmp";
            int kept = 0, dropped = 0;
            var pasteNames = new HashSet<string>(StringComparer.Ordinal);
            RewriteDroppingUnneeded(path, tmp, WantedCounted, ref kept, ref dropped, name => { if (pasters.Contains(NameKey.Short(name))) pasteNames.Add(name); });
            // A read-only source copy (Perforce) must not make the replace throw.
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            File.Move(tmp, path);
            return (Before: before, After: new FileInfo(path).Length, Kept: kept, Dropped: dropped, Pasters: pasteNames);
        });
        PartDone("emit");
        if (partDone is not null)
        {
            partDone("keptByName", keptByName);
            partDone("keptBySystemHeaders", keptBySystem);
            partDone("keptByUse", keptByUse);
            partDone("keptByPattern", keptByPattern);
            partDone("keptByPool", keptByPool);
            partDone("pasteUses", counts.Uses);
            partDone("pasteChains", counts.Chains);
            partDone("useRuns", counts.Runs);
            partDone("usesOverWork", counts.OverWork);
            partDone("namesBuilt", built.Exact.Count);
            partDone("patterns", built.Patterns);
            partDone("pools", built.Pools);
            partDone("definesCaptured", counts.Captured);
            partDone("bigDefines.defines", counts.Defines);
            partDone("bigDefines.edges", counts.Edges);
            partDone("bigDefines.pasters", pasterCount);
            partDone("bigDefines.calledPasters", counts.Called);
            partDone("bigDefines.emptyable", counts.Emptyable);
        }
        CodeCarver.Core.Util.Activity.Doing(null);
        return new HeaderCarveResult(rewritten.Sum(r => r.Before), rewritten.Sum(r => r.After), rewritten.Sum(r => r.Kept), rewritten.Sum(r => r.Dropped))
        {
            Passes = passes, PartMs = partMs,
            PasteMacros = rewritten.SelectMany(r => r.Pasters).Distinct(StringComparer.Ordinal).Count(),
            ForwardingMacros = counts.Forwarding,
        };
    }

    private static void AddText(Dictionary<ulong, List<string>> texts, ulong key, string text)
    {
        if (!texts.TryGetValue(key, out var l)) texts[key] = l = new List<string>(1);
        if (!l.Contains(text)) l.Add(text);   // a define repeated word for word is one body
    }

    /// <summary>A seed file's identifiers, what its defines say (rule 1), and their texts (null names: a binary file).</summary>
    private static (HashSet<ulong>? Names, FileFacts Facts, List<(ulong Key, string Text)> Defines, bool HashHash) SeedScan(string path)
    {
        var facts = new FileFacts();
        var defines = new List<(ulong, string)>();
        if (LooksBinary(path)) return (null, facts, defines, false);
        var names = new HashSet<ulong>();
        var def = new PpDefine();
        var hashHash = false;
        foreach (var line in LogicalLines(path))
        {
            AddIdentifiers(names, line);
            if (!line.Contains('#')) continue;
            hashHash |= line.Contains("##", StringComparison.Ordinal);
            if (!def.Parse(line)) continue;
            facts.Add(def, big: false);
            defines.Add((NameKey.Short(def.NameSpan), line));
        }
        return (names, facts, defines, hashHash);
    }

    /// <summary>What a big header's #defines say (rule 1, calls, emptiness), as name keys.</summary>
    private static FileFacts BigFacts(string path)
    {
        var facts = new FileFacts();
        var def = new PpDefine();
        foreach (var (_, text, kind) in EnumerateLogicalLines(path))
            if (kind == LineKind.Define && def.Parse(text)) facts.Add(def, big: true);
        return facts;
    }

    /// <summary>What a pass takes from one big header's lines (every non-define line when <paramref name="first"/>, and
    /// each wanted define but those named in <paramref name="skip"/>): the names they use not yet
    /// <paramref name="needed"/>, the paste uses and chains of its non-define lines (one stream) and of each define body
    /// it takes that pastes or names a paste macro (a stream each), and the text of every define of a name
    /// <paramref name="asked"/> for. Read on many threads at once: nothing it reads changes until the batch is read.</summary>
    private static FileTake Take(string path, bool first, HashSet<UInt128>? skip, Func<string, bool> wanted, HashSet<ulong> needed,
                                 HashSet<ulong> pasters, Dictionary<ulong, int> asked, MemoryLimit limit)
    {
        var lines = 0;
        var names = new HashSet<ulong>();
        var defines = new List<UInt128>();
        List<(ulong, string)>? captured = null;
        var ev = new UseEvents(pasters);
        var main = first ? new UseTracker(ev) : null;
        var bodies = new UseTracker(ev);
        var def = new PpDefine();
        var toks = new List<PpToken>();
        var inBlock = false;
        main?.Begin(null);
        foreach (var (name, text, kind) in EnumerateLogicalLines(path))
        {
            if ((++lines & 0xFFFF) == 0) limit.Check();
            if (kind != LineKind.Define)
            {
                if (!first) continue;
                AddNewIdentifiers(names, needed, text);
                StreamLine(main!, bodies, text, ref inBlock, toks);
                continue;
            }
            inBlock = false;
            if (name is null) continue;
            // The first pass takes every paste macro's text: the uses each level builds call them.
            var shortKey = NameKey.Short(name);
            if ((first && pasters.Contains(shortKey)) || (asked.Count > 0 && asked.ContainsKey(shortKey))) (captured ??= new()).Add((shortKey, text));
            var key = NameKey.Of(name);
            if (skip?.Contains(key) == true || !wanted(name)) continue;
            defines.Add(key);
            if (!def.Parse(text))
            {
                AddNewIdentifiers(names, needed, text, except: name);
                continue;
            }
            // Not its own name (it is wanted already, and stays so) nor its parameters': every other name in its body.
            AddBodyNames(names, needed, def);
            if (MayUse(def, pasters)) StreamBody(bodies, def);
        }
        main?.End();
        return new FileTake(names, defines, captured, ev.Events.Count > 0 ? ev : null);
    }

    private sealed record FileTake(HashSet<ulong> Names, List<UInt128> Defines, List<(ulong Key, string Text)>? Captured, UseEvents? Events);

    /// <summary>Every name in a define's body, comments and strings too, but its parameters. A number names nothing.</summary>
    private static void AddBodyNames(HashSet<ulong> set, HashSet<ulong> known, PpDefine d)
    {
        var s = d.Text;
        var i = d.BodyStart;
        while (NextIdentifier(s, ref i, out var st))
        {
            var id = s.AsSpan(st, i - st);
            if (d.IsParam(id)) continue;
            var key = NameKey.Short(id);
            if (!known.Contains(key)) set.Add(key);
        }
    }

    /// <summary>A body with a <c>##</c> or a paste macro's name in it: only such a body can use or name one.</summary>
    private static bool MayUse(PpDefine d, HashSet<ulong> pasters)
    {
        for (var k = 0; k < d.Body.Count; k++)
        {
            var t = d.Body[k];
            if (t.Kind == PpTok.HashHash) return true;
            if (t.Kind == PpTok.Ident && pasters.Contains(NameKey.Short(d.Tok(k)))) return true;
        }
        return false;
    }

    /// <summary>A test's batch size: work's first batch held 8 headers, a small test's hold all of them.</summary>
    internal static readonly AsyncLocal<long?> BatchBytes = new();

    /// <summary>The headers in runs of about <paramref name="bytes"/> each, in order.</summary>
    private static List<List<string>> Batches(List<string> paths, long bytes)
    {
        var batches = new List<List<string>>();
        var cur = new List<string>();
        long size = 0;
        foreach (var p in paths)
        {
            cur.Add(p);
            size += new FileInfo(p).Length;
            if (size >= bytes) { batches.Add(cur); cur = new List<string>(); size = 0; }
        }
        if (cur.Count > 0) batches.Add(cur);
        return batches;
    }

    private enum LineKind { Define, Conditional, Other }

    /// <summary>
    /// Stream a header yielding one entry per logical line (continuations joined): a <c>#define</c> with its
    /// name and full text, a conditional (<c>#if/#ifdef/#ifndef/#elif</c>) with its whole condition, or any
    /// other line. Bounded memory; Latin-1 so no byte can fail to decode.
    /// </summary>
    private static IEnumerable<(string? Name, string Text, LineKind Kind)> EnumerateLogicalLines(string path)
    {
        using var r = new StreamReader(path, Encoding.Latin1);
        string? line;
        var inComment = Comment.None;
        while ((line = r.ReadLine()) is not null)
        {
            line = CommentsBlanked(Digraphs(line));
            var commented = inComment != Comment.None;   // a #define inside a comment is comment text
            var (isDefine, isCond) = commented ? (false, false) : Directive(line);
            inComment = CommentOpenAfter(line, inComment);
            // A line is copied only when continuation lines join it (each line was copied three more times).
            var full = line;
            if ((isDefine || isCond) && EndsWithContinuation(line))
            {
                var sb = new StringBuilder(line);
                while (EndsWithContinuation(line) && (line = r.ReadLine()) is not null)
                {
                    line = CommentsBlanked(Digraphs(line));
                    sb.Append('\n').Append(line);
                    inComment = CommentOpenAfter(line, inComment);
                }
                full = sb.ToString();
            }
            // A define that leaves a comment open is kept whatever it names (the rewrite can't cut the comment):
            // read it as plain text, so what it names counts.
            yield return isDefine && inComment == Comment.None ? (DefineName(full), full, LineKind.Define)
                 : isCond ? (null, full, LineKind.Conditional)
                 : (null, full, LineKind.Other);
        }
    }

    /// <summary>A file's lines with backslash-continuations joined, streamed. Latin-1, or what a byte-order mark says
    /// (a UTF-16 source an MSVC-style toolchain compiles).</summary>
    private static IEnumerable<string> LogicalLines(string path)
    {
        var sb = new StringBuilder();
        using var r = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            line = CommentsBlanked(Digraphs(line));
            if (EndsWithContinuation(line)) { sb.Append(line).Append('\n'); continue; }
            if (sb.Length == 0) { yield return line; continue; }
            sb.Append(line);
            yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>A directive line with the closed comments before its <c>#</c> and between the <c>#</c> and its keyword
    /// blanked: a comment is a space, so <c>/* c */ #define X</c> and <c># /* c */ define X</c> are defines. Only for
    /// reading.</summary>
    internal static string CommentsBlanked(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i >= line.Length || (line[i] != '#' && !(line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*'))) return line;
        char[]? c = null;
        var hash = false;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                c ??= line.ToCharArray();
                for (var k = i; k < end + 2; k++) c[k] = ' ';
                i = end + 2;
                continue;
            }
            if (line[i] == '#' && !hash) { hash = true; i++; continue; }
            break;
        }
        return hash && c is not null ? new string(c) : line;
    }

    /// <summary>A line read as C reads it: <c>%:</c> is <c>#</c> and <c>%:%:</c> is <c>##</c> (a define, a paste). Only
    /// for reading; the rewrite copies the text as it is.</summary>
    internal static string Digraphs(string line) => line.Contains("%:", StringComparison.Ordinal) ? line.Replace("%:", "#", StringComparison.Ordinal) : line;

    /// <summary>Is a comment open after <paramref name="line"/>, given whether one was open before it? A block
    /// comment, or a line comment ending in a backslash: the splice makes the next line comment text too
    /// (<c>// path: C:\vendor\regs\</c>). String and character literals open nothing.</summary>
    private static Comment CommentOpenAfter(string line, Comment open)
    {
        if (open == Comment.Line) return EndsWithContinuation(line) ? Comment.Line : Comment.None;   // all comment text
        var inBlock = open == Comment.Block;
        var quote = '\0';
        for (var c = 0; c < line.Length; c++)
        {
            var ch = line[c];
            if (inBlock) { if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/') { inBlock = false; c++; } }
            else if (quote != '\0') { if (ch == '\\') c++; else if (ch == quote) quote = '\0'; }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') return EndsWithContinuation(line) ? Comment.Line : Comment.None;
            else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*') { inBlock = true; c++; }
        }
        return inBlock ? Comment.Block : Comment.None;
    }

    /// <summary>What comment is open at a line's end: none, a block comment, or a line comment a backslash continues.</summary>
    private enum Comment { None, Block, Line }

    /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/>, omitting #defines the predicate rejects.
    /// Byte-transparent (Latin-1) and every kept line keeps its own line ending (review E1). A line inside a comment
    /// (a block comment, or a line comment a trailing backslash continues) is text, and a define that leaves a
    /// comment open is kept: dropping it would cut the comment, or splice the next line into one.</summary>
    private static void RewriteDroppingUnneeded(string src, string dst, Func<string, bool> wanted, ref int kept, ref int dropped, Action<string> defined)
    {
        using var w = new StreamWriter(dst, false, Encoding.Latin1);
        using var e = ReadLinesWithEol(src).GetEnumerator();
        var inComment = Comment.None;
        var define = new List<(string Line, string Eol)>();
        while (e.MoveNext())
        {
            var (line, eol) = e.Current;
            var view = inComment == Comment.None ? CommentsBlanked(Digraphs(line)) : line;
            if (inComment == Comment.None && IsDefineDirective(view.TrimStart()))
            {
                // Read the whole (possibly multi-line) define, then decide.
                define.Clear();
                define.Add((line, eol));
                inComment = CommentOpenAfter(line, inComment);
                var cont = line;
                while (EndsWithContinuation(cont) && e.MoveNext())
                {
                    (cont, eol) = e.Current;
                    define.Add((cont, eol));
                    inComment = CommentOpenAfter(cont, inComment);
                }
                var name = DefineName(view);
                if (name is not null) defined(name);
                var drop = name is not null && inComment == Comment.None && !wanted(name);
                if (drop) { dropped++; continue; }
                kept++;
                foreach (var (l, el) in define) { w.Write(l); w.Write(el); }
                continue;
            }
            inComment = CommentOpenAfter(line, inComment);
            w.Write(line);
            w.Write(eol);
        }
    }

    /// <summary>Lines with their exact terminators ("\r\n", "\n", "\r" or "" at EOF), Latin-1, streaming.</summary>
    private static IEnumerable<(string Line, string Eol)> ReadLinesWithEol(string path)
    {
        using var r = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        var sb = new StringBuilder();
        int c;
        while ((c = r.Read()) >= 0)
        {
            if (c == '\n') { yield return (sb.ToString(), "\n"); sb.Clear(); continue; }
            if (c == '\r')
            {
                if (r.Peek() == '\n') { r.Read(); yield return (sb.ToString(), "\r\n"); }
                else yield return (sb.ToString(), "\r");
                sb.Clear();
                continue;
            }
            sb.Append((char)c);
        }
        if (sb.Length > 0) yield return (sb.ToString(), "");
    }

    /// <summary>A NUL byte in the first 8 KB: treat as binary (no identifiers to seed from). UTF-16/32 text with a
    /// byte-order mark is full of NULs but is text: <see cref="LogicalLines"/> decodes it by its mark.</summary>
    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8192];
            var n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
            if (n >= 4 && buf[0] == 0 && buf[1] == 0 && buf[2] == 0xFE && buf[3] == 0xFF) return false;
            return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// A macro the compiler's own headers read, which the tree never names: a reserved name (<c>_GNU_SOURCE</c>,
    /// <c>__STDC_WANT_LIB_EXT1__</c>) or a well-known configuration macro (<c>NDEBUG</c> for <c>&lt;assert.h&gt;</c>).
    /// Dropping its #define changes what the build compiles (review H1).
    /// </summary>
    internal static bool ReadBySystemHeaders(string name) =>
        CodeCarver.Core.Preprocess.MacroTable.IsReserved(name) || SystemConfigMacros.Contains(name);

    private static readonly HashSet<string> SystemConfigMacros = new(StringComparer.Ordinal)
    {
        "NDEBUG", "DEBUG", "WIN32_LEAN_AND_MEAN", "VC_EXTRALEAN", "NOMINMAX", "STRICT", "UNICODE", "NOGDI",
        "NOUSER", "NOCRYPT", "NOSERVICE", "NOMCX", "NOIME", "WINVER", "NTDDI_VERSION",
    };

    /// <summary>Is <paramref name="line"/> a <c>#define</c>, or a conditional directive?</summary>
    private static (bool Define, bool Conditional) Directive(string line)
    {
        var t = line.AsSpan().TrimStart();
        if (IsDefineDirective(t)) return (true, false);
        return (false, IsConditionalDirective(t));
    }

    private static bool IsDefineDirective(ReadOnlySpan<char> trimmed) => StartsWithHash(trimmed, "define");

    private static bool IsConditionalDirective(ReadOnlySpan<char> trimmed) =>
        StartsWithHash(trimmed, "if") || StartsWithHash(trimmed, "ifdef") ||
        StartsWithHash(trimmed, "ifndef") || StartsWithHash(trimmed, "elif") ||
        StartsWithHash(trimmed, "elifdef") || StartsWithHash(trimmed, "elifndef");

    /// <summary>Matches <c>#</c> (optional spaces) then the keyword as a whole word — e.g. <c>#  define</c>.</summary>
    private static bool StartsWithHash(ReadOnlySpan<char> trimmed, string keyword)
    {
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;
        var i = 1;
        while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t')) i++;
        if (i + keyword.Length > trimmed.Length) return false;
        for (var k = 0; k < keyword.Length; k++)
            if (trimmed[i + k] != keyword[k]) return false;
        var after = i + keyword.Length;
        return after == trimmed.Length || !IsIdentChar(trimmed[after]); // whole word (#if not #ifdef)
    }

    /// <summary>The macro name of a <c>#define NAME</c> / <c>#define NAME(args)</c> line, or null.</summary>
    private static string? DefineName(string text)
    {
        var i = text.IndexOf('#');
        if (i < 0) return null;
        i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        i += "define".Length;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        var start = i;
        while (i < text.Length && IsIdentChar(text[i])) i++;
        return i > start ? text[start..i] : null;
    }

    private static bool EndsWithContinuation(string line)
    {
        var t = line.AsSpan().TrimEnd();
        return t.Length > 0 && t[^1] == '\\';
    }

    /// <summary>Add each C identifier in <paramref name="text"/> not in <paramref name="known"/> to <paramref name="set"/>.</summary>
    private static void AddNewIdentifiers(HashSet<ulong> set, HashSet<ulong> known, string text, string? except = null)
    {
        var i = 0;
        while (NextIdentifier(text, ref i, out var start))
        {
            var id = text.AsSpan(start, i - start);
            if (except is not null && id.SequenceEqual(except)) continue;
            var key = NameKey.Short(id);
            if (!known.Contains(key)) set.Add(key);
        }
    }

    /// <summary>Add every C identifier in <paramref name="text"/> to <paramref name="set"/>; return true if any was new.</summary>
    private static bool AddIdentifiers(HashSet<ulong> set, string text)
    {
        var grew = false;
        var i = 0;
        while (NextIdentifier(text, ref i, out var start)) grew |= set.Add(NameKey.Short(text.AsSpan(start, i - start)));
        return grew;
    }

    /// <summary>The next identifier at or after <paramref name="i"/>: its start, <paramref name="i"/> just past it. A number
    /// is one token and names nothing (<c>0x1Fu</c> is not the name <c>x1Fu</c>: every register value was one, millions of
    /// names in a big header's needed set).</summary>
    private static bool NextIdentifier(string text, ref int i, out int start)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (IsIdentStart(c))
            {
                start = i;
                while (i < text.Length && IsIdentChar(text[i])) i++;
                return true;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                // A preprocessing number: digits, letters, _, '.', and a sign after e, E, p or P.
                for (i++; i < text.Length; i++)
                {
                    var d = text[i];
                    if (IsIdentChar(d) || d == '.') continue;
                    if ((d == '+' || d == '-') && text[i - 1] is 'e' or 'E' or 'p' or 'P') continue;
                    break;
                }
                continue;
            }
            i++;
        }
        start = i;
        return false;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
