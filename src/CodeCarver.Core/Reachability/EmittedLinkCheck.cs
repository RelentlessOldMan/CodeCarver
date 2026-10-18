using System.Collections.Concurrent;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Reachability;

/// <summary>One unresolved reference found by <see cref="EmittedLinkCheck"/>: emitted code names a function
/// that only a DROPPED file defines.</summary>
/// <param name="Name">The function name.</param>
/// <param name="DefinedIn">A dropped file that defines it (first in ordinal order).</param>
/// <param name="ReferencedIn">An emitted file that references it.</param>
/// <param name="Line">1-based line of that reference.</param>
/// <param name="DeadOnly">True when every reference sits on a line the #ifdef model calls dead — the carve
/// relied on that model to drop the definition. Reported, but not a hard failure.</param>
/// <param name="DefinedLine">1-based line of that definition in <paramref name="DefinedIn"/>.</param>
public sealed record LinkViolation(string Name, string DefinedIn, string ReferencedIn, int Line, bool DeadOnly, int DefinedLine = 0)
{
    /// <summary>Every dropped file that defines the name (<see cref="DefinedIn"/> is the first).</summary>
    public IReadOnlyList<string> DefinedInAll { get; init; } = Array.Empty<string>();
    /// <summary>The emitted code declares the name weak: an unresolved reference links (as null).</summary>
    public bool Weak { get; init; }
}

/// <summary>Outcome of <see cref="EmittedLinkCheck.Run"/>.</summary>
public sealed record LinkCheckResult(IReadOnlyList<LinkViolation> Violations, int FilesChecked, int FilesSkipped)
{
    /// <summary>References on live lines: the emitted tree will not link (or not compile).</summary>
    public IReadOnlyList<LinkViolation> Hard => Violations.Where(v => !v.DeadOnly).ToList();
    /// <summary>References only on #ifdef-dead lines: correct exactly when the #ifdef model is.</summary>
    public IReadOnlyList<LinkViolation> DeadOnly => Violations.Where(v => v.DeadOnly).ToList();
    /// <summary>Where the time went (headersMs, droppedMs, prunedMs, emittedScanMs, restMs) and how files were had (filesRead,
    /// fromCache, instancesRedone).</summary>
    public IReadOnlyDictionary<string, long> Stats { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// What <see cref="EmittedLinkCheck.Run"/> keeps between the rounds and stages of one carve: for each file that cannot
/// change during the run (the source tree's dropped files, headers and originals) its definitions and its header text.
/// Every round otherwise read the whole tree again, from a share at a round trip per file. Only what a later run needs
/// is kept (a dropped file's definitions, not its uses), so it stays small. Every run sharing one must pass the same
/// inputs (size limit, definer macros, command-line macros, C99 inline functions); a run that doesn't is not served
/// from it.
/// </summary>
public sealed class LinkCheckCache
{
    /// <param name="unchanging">True for a path whose content cannot change during the run: the source tree, not the
    /// output.</param>
    public LinkCheckCache(Func<string, bool> unchanging) => Unchanging = unchanging;

    internal Func<string, bool> Unchanging { get; }
    internal readonly ConcurrentDictionary<string, EmittedLinkCheck.DroppedEntry?> Dropped = new(StringComparer.Ordinal);
    // A header's text only when it is a template (see EmittedLinkCheck.HeaderInfo): the rest are what a big tree is made of.
    internal readonly ConcurrentDictionary<string, (EmittedLinkCheck.HeaderInfo? Info, string? TemplateText)> Headers = new(StringComparer.Ordinal);
    internal readonly ConcurrentDictionary<string, EmittedLinkCheck.OriginalEntry?> Originals = new(StringComparer.Ordinal);
    // The #include names in each header (TemplateInstances.IncludeNames).
    internal readonly ConcurrentDictionary<string, string[]> HeaderIncludes = new(StringComparer.Ordinal);
    // An emitted unit's template walk by its text (length, hash): what it found and each include it read, so a later
    // round emitting the same text serves it while those headers read as they did. Every round walked every emitted
    // unit again (C24: a round's 3,308 walks took about 30 min at work, and a carve runs several rounds).
    internal readonly ConcurrentDictionary<(int Length, ulong Hash), (List<(string Raw, EmittedLinkCheck.HeaderInfo? Read)> Deps, List<EmittedLinkCheck.Definition> Defs)> Walks = new();

    private object?[]? _inputs;

    /// <summary>True when these are the inputs of the first run that used it.</summary>
    internal bool Matches(params object?[] inputs)
    {
        lock (Dropped)
        {
            _inputs ??= inputs;
            return _inputs.Length == inputs.Length && _inputs.Zip(inputs).All(p => Equals(p.First, p.Second));
        }
    }
}

/// <summary>
/// The independent soundness check over the EMITTED tree (review V1). It deliberately shares nothing with
/// the graph: no tree-sitter, no edges, no name tables. A plain C/C++ tokenizer collects
/// <list type="number">
/// <item>every function DEFINITION in the dropped code files (identifier, balanced <c>( )</c>, then <c>{</c>
///   at file/namespace scope — <c>static</c> ones in non-header files are skipped, since no other file can
///   link against them);</item>
/// <item>every function definition in the emitted files; and</item>
/// <item>every identifier USE in the emitted files (outside comments, strings, <c>#if</c>/<c>#include</c>
///   lines, member access, and names declared in the same scope).</item>
/// </list>
/// A name defined in a dropped file, defined in no emitted file, and used by emitted code is a violation:
/// the carve dropped a definition the emitted code needs. Because it never consults the graph, it catches
/// exactly the bugs the graph-based check cannot — a wrong #ifdef resolution, a wrong build-log define, a
/// name-table collision, or an emitter that writes more code than reachability accounted for.
///
/// The one input it does share is the #ifdef model (<c>deadLines</c>), and only to CLASSIFY: a use that sits
/// only on lines the model calls dead is reported as <see cref="LinkViolation.DeadOnly"/> — the drop is right
/// if and only if the model is — instead of failing the run, so a correct closed-world carve stays green.
/// </summary>
public static class EmittedLinkCheck
{
    /// <param name="emitted">Emitted code files: (relative path, path on disk to read).</param>
    /// <param name="dropped">Dropped code files: (relative path, path on disk to read).</param>
    /// <param name="deadLines">Optional: (relative path, emitted text) → 1-based dead-line map, or null.</param>
    /// <param name="maxBytes">Files larger than this are not read (counted in FilesSkipped).</param>
    /// <param name="original">For a stage that carves INSIDE kept files: emitted relative path → the original file on
    /// disk. A function the original defines and the emitted copy doesn't was pruned, and counts as dropped.</param>

    static volatile Func<Dictionary<string, long>>? s_shapeNow;

    /// <summary>The running (or last) check's shape.* numbers so far: a run stopped inside a loop still has them.</summary>
    public static IReadOnlyDictionary<string, long> ShapeSoFar() => s_shapeNow?.Invoke() ?? new Dictionary<string, long>();

    public static LinkCheckResult Run(
        IEnumerable<(string Rel, string Path)> emitted,
        IEnumerable<(string Rel, string Path)> dropped,
        Func<string, string, bool[]?>? deadLines = null,
        long maxBytes = 20_000_000,
        Func<string, string?>? original = null,
        CodeCarver.Core.Preprocess.DefinerMacros.Set? definerMacros = null,
        IEnumerable<(string Name, string Rel, int Line)>? linkUses = null,
        IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? commandLineMacros = null,
        IReadOnlySet<string>? c99InlineFns = null,
        IReadOnlySet<string>? wrapped = null,
        LinkCheckCache? cache = null,
        Action<string, long>? report = null)
    {
        var emittedList = emitted.ToList();
        var droppedList = dropped.ToList();
        emitted = emittedList;
        dropped = droppedList;
        var skipped = 0;
        var checkedFiles = 0;
        var stats = new Dictionary<string, long>(StringComparer.Ordinal);
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var droppedDefs = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        var macroCalls = new List<(string Name, string Rel, int Line, bool Dead)>();
        var live = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        var dead = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        long heldChars = 0;
        // The names the template walks have looked up, by hash (string.GetHashCode), each with the order it came in: a
        // non-template header is held with only the defines of these (TemplateInstances.KeepDefines). A register-map tree
        // is nearly all #defines no walk asks for: held whole, it went past HeaderTextBudget, and every walk read and parsed
        // again each header not held (C26: 3,007 s walking, 1,600 s reading, 1,173 s parsing). Replaced as it grows, never
        // changed, so read without a lock; grown under headerInfo's.
        var walkNames = new Dictionary<int, int>();
        var walkOrder = new List<int>();
        // Each loop's time, and memory as it ends: the process's private bytes, the managed heap the last GC left, and
        // managed bytes allocated in the loop (MB). A "Max" key is kept at its largest over a stage's rounds.
        var allocatedAt = GC.GetTotalAllocatedBytes();
        long filesRead = 0, fromCache = 0, instancesRedone = 0, templateWalks = 0, templateWalksSkipped = 0, templateWalksReused = 0, templateWalksRedone = 0, templateWalksWhole = 0, headersRefiltered = 0;
        // The tree's shape, for sizing a test corpus like it (shape.*): each header's size, each walk's include closure.
        var headerSizes = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var closures = new ConcurrentBag<(int Files, long Chars, int Reads, long ReadChars)>();
        s_shapeNow = () => { var d = new Dictionary<string, long>(StringComparer.Ordinal); Shape(d, headerSizes.Values, closures, Interlocked.Read(ref templateWalks), Interlocked.Read(ref templateWalksSkipped)); return d; };
        void Lap(string key)
        {
            stats[key + "Ms"] = phase.ElapsedMilliseconds;
            try { using var self = System.Diagnostics.Process.GetCurrentProcess(); stats[key + ".privateMaxMb"] = self.PrivateMemorySize64 >> 20; }
            catch (InvalidOperationException) { }
            stats[key + ".heapMaxMb"] = GC.GetGCMemoryInfo().HeapSizeBytes >> 20;
            var now = GC.GetTotalAllocatedBytes();
            stats[key + ".allocatedMb"] = (now - allocatedAt) >> 20;
            allocatedAt = now;
            // What the check holds (the largest over its loops and rounds): its tables, and the cache kept for later rounds.
            stats["held.headerTextMaxMb"] = heldChars * 2 >> 20;
            stats["held.cacheDroppedMax"] = cache?.Dropped.Count ?? 0;
            stats["held.cacheDefinitionsMax"] = cache?.Dropped.Values.Sum(e => (long)(e?.Definitions.Count ?? 0)) ?? 0;
            stats["held.cacheOriginalsMax"] = cache?.Originals.Count ?? 0;
            stats["held.droppedDefsMax"] = droppedDefs.Count;
            stats["held.macroCallsMax"] = macroCalls.Count;
            stats["held.usesMax"] = live.Count + dead.Count;
            // What each part of the loop's per-file work allocated (Activity.Part): the loop's own, reset as the next begins.
            foreach (var (pk, pv) in Activity.Parts())
                if (pk.StartsWith("part.", StringComparison.Ordinal) && !pk.EndsWith(".inFlight", StringComparison.Ordinal)) stats[$"{key}.{pk}"] = pv;
            Activity.ClearParts();
            Shape(stats, headerSizes.Values, closures, templateWalks, templateWalksSkipped);
            phase.Restart();
            // Handed out as each loop ends, so a run stopped inside the check still has the loops before.
            foreach (var k in new[] { key + "Ms", key + ".privateMaxMb", key + ".heapMaxMb", key + ".allocatedMb" }
                         .Concat(stats.Keys.Where(k => k.StartsWith("held.", StringComparison.Ordinal) || k.StartsWith("shape.", StringComparison.Ordinal) || k.StartsWith(key + ".part.", StringComparison.Ordinal))).ToList())
                if (stats.TryGetValue(k, out var v)) report?.Invoke(k, v);
        }
        var scanGate = new CodeCarver.Core.Util.ByteGate(ScanBudget());   // see Gated
        var readGate = new CodeCarver.Core.Util.ByteGate(ReadBudget());
        // Template walks parse and walk their include closures, a budget at a time (C20/C21: 64 walks at once re-parsing
        // their include closures went from 12 to 21 GB in 90 s; 1.0.294 gated only once a header was not held, and a run
        // whose headers were all held peaked 5.1 GB locally). A walk takes what the largest walk allocated before it, or
        // the whole budget until one has: the first runs alone and sizes the rest.
        var walkBudget = WalkBudget();
        var walkGate = new CodeCarver.Core.Util.ByteGate(walkBudget);
        long walkCost = -1;
        // A header's first parse (kept with the header after) allocates a few times its text; walks of different units parse
        // different closures at once (C22: 16 walks at once, one parsing 402 MB of headers). Each parse takes its estimate
        // from the same budget, one at a time per thread, so the walks hold at most the budget in parses running at once.
        var parseGate = new CodeCarver.Core.Util.ByteGate(walkBudget);
        IDisposable? Parsing(int chars) => new ParseSlot(Activity.Part("templateParse", chars), parseGate.Take(Math.Min(walkBudget, chars * ParseBytesPerChar)));
        LinkCheckResult Done(IReadOnlyList<LinkViolation> v)
        {
            Lap("rest");
            stats["filesRead"] = filesRead; stats["fromCache"] = fromCache; stats["instancesRedone"] = instancesRedone;
            stats["scanWaits"] = scanGate.Waits; stats["readWaits"] = readGate.Waits; stats["walkWaits"] = walkGate.Waits; stats["parseWaits"] = parseGate.Waits;
            stats["walkCostMaxMb"] = Math.Max(0, walkCost) >> 20;
            stats["templateWalks"] = templateWalks; stats["templateWalksSkipped"] = templateWalksSkipped; stats["templateWalksReused"] = templateWalksReused;
            stats["templateWalksRedone"] = templateWalksRedone; stats["templateWalksWhole"] = templateWalksWhole; stats["headersRefiltered"] = headersRefiltered;
            stats["walkNames"] = walkNames.Count;
            foreach (var k in new[] { "filesRead", "fromCache", "instancesRedone", "scanWaits", "readWaits", "walkWaits", "parseWaits", "walkCostMaxMb", "templateWalks", "templateWalksSkipped", "templateWalksReused", "templateWalksRedone", "templateWalksWhole", "headersRefiltered", "walkNames" })
                report?.Invoke(k, stats[k]);
            return new LinkCheckResult(v, checkedFiles, skipped) { Stats = stats };
        }
        if (cache is not null && !cache.Matches(maxBytes, definerMacros, commandLineMacros, c99InlineFns)) cache = null;
        bool Cached(string path) => cache is not null && cache.Unchanging(path);

        // Template headers (see TemplateInstances): the names a translation unit defines by including one.
        var byBase = emitted.Concat(dropped).GroupBy(f => Path.GetFileName(f.Rel), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList(), StringComparer.OrdinalIgnoreCase);
        // Files are read and scanned on several threads (ReadAhead), so these caches take a lock.
        // A header's text as TemplateInstances reads it: whole for a template, its directive lines otherwise (a config
        // header still carries macros a template after it uses). Every header is looked at once (HeaderInfo: is it a
        // template, and what its text hashes to); the text is held for a template, and for the rest only while they fit
        // HeaderTextBudget - a tree of register-map headers is nearly all directives, and holding all of them ran a work
        // machine out of memory. A header not held is read again when a walk needs it.
        var headerInfo = new Dictionary<string, HeaderInfo?>(StringComparer.Ordinal);
        var headerText = new Dictionary<string, string>(StringComparer.Ordinal);
        var heldDropped = new Dictionary<string, int[]>(StringComparer.Ordinal);   // a held header's Filtered.Dropped
        var templates = new HashSet<string>(StringComparer.Ordinal);
        var budget = HeaderTextBudget();
        // The loops below scan up to ReadAhead.Window files at once (on a network drive each read waits a round trip), and
        // a scan holds about 30 times its file's size while it runs: 64 register headers of a few MB each held over 7 GB.
        // So a file waits until the bytes being scanned fit ScanBudget (scanGate); small files never wait.
        R Gated<R>(string path, Func<R> scan, CodeCarver.Core.Util.ByteGate? gate = null)
        {
            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { size = 0; }
            using ((gate ?? scanGate).Take(size))
            using (Activity.Working(size))
                return scan();
        }
        (HeaderInfo? Info, string? Text) ReadDirectives(string p)
        {
            using var part = Activity.Part("headerRead");
            Interlocked.Increment(ref filesRead);
            var t = TryRead(p, maxBytes);
            if (t is null) return (null, null);
            headerSizes.TryAdd(p, t.Length);
            t = CodeCarver.Core.Preprocess.SourceText.Normalize(t);
            var template = CodeCarver.Core.Preprocess.TemplateInstances.HasMacroNamedHead(t);
            if (!template) t = CodeCarver.Core.Preprocess.TemplateInstances.DirectivesOnly(t);
            return (new HeaderInfo(template, t.Length, ContentHash(t)), t);
        }
        // The #include names (quoted, <>, computed) in each header looked at: which headers can bring a template in (reachTemplate).
        var headerIncludes = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        (HeaderInfo? Info, string? Text) PrepareHeader(string p)
        {
            var cached = Cached(p);
            if (cached && cache!.Headers.TryGetValue(p, out var hit) && cache.HeaderIncludes.TryGetValue(p, out var had))
            {
                Interlocked.Increment(ref fromCache);
                headerIncludes[p] = had;
                return (hit.Info, hit.TemplateText);
            }
            var h = ReadDirectives(p);
            var includes = h.Text is null ? Array.Empty<string>() : CodeCarver.Core.Preprocess.TemplateInstances.IncludeNames(h.Text);
            headerIncludes[p] = includes;
            if (cached)
            {
                h = cache!.Headers.GetOrAdd(p, (h.Info, h.Info is { Template: true } ? h.Text : null));
                cache.HeaderIncludes.TryAdd(p, includes);
            }
            return h;
        }
        // The first look at a header stays its answer for the run. Returns the text when there is one in hand.
        // A header's text as a walk reads it: a template or a small header whole, a big one with the defines of walkNames
        // only. Dropped: the hashes of the names whose defines went (sorted); Through: how many of walkOrder were kept for.
        (string Text, int[] Dropped, int Through) Filtered(HeaderInfo info, string text)
        {
            if (info.Template || text.Length < FilterChars) return (text, Array.Empty<int>(), int.MaxValue);
            var names = Volatile.Read(ref walkNames);
            var dropped = new List<int>();
            text = CodeCarver.Core.Preprocess.TemplateInstances.KeepDefines(text, names.ContainsKey, dropped);
            var d = dropped.Distinct().ToArray();
            Array.Sort(d);
            return (text, d, names.Count);
        }
        static bool DropsAny(int[] dropped, IEnumerable<int> hashes)
        {
            if (dropped.Length == 0) return false;
            foreach (var h in hashes) if (Array.BinarySearch(dropped, h) >= 0) return true;
            return false;
        }
        void StoreHeader(string p, (HeaderInfo? Info, string? Text) h)
        {
            var f = h.Info is null || h.Text is null ? default : Filtered(h.Info, h.Text);
            lock (headerInfo)
            {
                if (headerInfo.ContainsKey(p)) return;
                headerInfo[p] = h.Info;
                if (h.Info is null || h.Text is null) return;
                if (h.Info.Template) templates.Add(p);
                Hold(p, f, h.Info.Template);
            }
        }
        // Under headerInfo's lock. A header is held only with the defines of every name in walkNames: one filtered before
        // the last names came in, and dropping one of them, is not.
        void Hold(string p, (string Text, int[] Dropped, int Through) f, bool template)
        {
            if (f.Through < walkOrder.Count && DropsAny(f.Dropped, walkOrder.Skip(f.Through))) return;
            long size = f.Text.Length + f.Dropped.Length * (sizeof(int) / sizeof(char));
            if (!template && heldChars + size > budget) return;
            headerText[p] = f.Text;
            if (f.Dropped.Length > 0) heldDropped[p] = f.Dropped;
            if (!template) heldChars += size;
        }
        // Under headerInfo's lock: the names a walk looked up join walkNames, and a header held without the define of
        // one is held no more (the next walk that wants it reads it again, with the define).
        void Grow(IEnumerable<int> hashes)
        {
            List<int>? add = null;
            foreach (var h in hashes)
                if (!walkNames.ContainsKey(h) && (add ??= new List<int>()) is var a && !a.Contains(h)) a.Add(h);
            if (add is null) return;
            var next = new Dictionary<int, int>(walkNames);
            foreach (var h in add) { next[h] = walkOrder.Count; walkOrder.Add(h); }
            Volatile.Write(ref walkNames, next);
            List<string>? gone = null;
            foreach (var (p, d) in heldDropped)
                if (DropsAny(d, add)) (gone ??= new List<string>()).Add(p);
            foreach (var p in gone ?? new List<string>())
            {
                heldChars -= headerText[p].Length + heldDropped[p].Length * (sizeof(int) / sizeof(char));
                headerText.Remove(p);
                heldDropped.Remove(p);
                headersRefiltered++;
            }
        }
        HeaderInfo? Info(string p)
        {
            lock (headerInfo) if (headerInfo.TryGetValue(p, out var i)) return i;
            StoreHeader(p, PrepareHeader(p));
            lock (headerInfo) return headerInfo[p];
        }
        // whole: the header's text as it is (no define dropped), not held - a walk that could not settle on the names it
        // needs reads them so.
        (string? Text, int[] Dropped, int Through) ReadHeader(string p, bool whole)
        {
            var none = ((string?)null, Array.Empty<int>(), int.MaxValue);
            if (Info(p) is null) return none;
            if (!whole)
                lock (headerInfo)
                    if (headerText.TryGetValue(p, out var t))
                        return (t, heldDropped.TryGetValue(p, out var d) ? d : Array.Empty<int>(), walkOrder.Count);
            var (info, text) = ReadDirectives(p);   // not held: read again (the same text: within a run a file stays as it is)
            if (info is null || text is null) return none;
            if (whole) return (text, Array.Empty<int>(), int.MaxValue);
            var f = Filtered(info, text);
            lock (headerInfo)
            {
                if (headerText.TryGetValue(p, out var t))
                    return (t, heldDropped.TryGetValue(p, out var d) ? d : Array.Empty<int>(), walkOrder.Count);
                Hold(p, f, info.Template);
            }
            return f;
        }
        // The header an #include of raw reads: a template among those of its name, else the first.
        string? Chosen(string raw)
        {
            if (!byBase.TryGetValue(Path.GetFileName(raw), out var ps)) return null;
            foreach (var p in ps) Info(p);
            lock (headerInfo) return ps.FirstOrDefault(templates.Contains) ?? ps[0];
        }
        HeaderInfo? TemplateKey(string raw) => Chosen(raw) is { } p ? Info(p) : null;
        // Every header, looked at in parallel before the scans start (a scan asks as soon as it meets an #include).
        var anyTemplate = false;
        if (emitted.Concat(dropped).Any(f => !IsHeader(f.Rel) && !IsAssembly(f.Rel)))
        {
            var headerPaths = byBase.Values.SelectMany(ps => ps).Where(p => IsHeader(p)).ToList();
            // Each loop counts on its own, named (the heartbeat and a stop say which one a run is in).
            Activity.Begin("check: headers read", headerPaths.Count);
            // A header's look is light (its directive lines and a hash), and the loop waits mostly on reads: under the scan
            // budget it ran few at once and took 3.3 times as long (work run C18) for little less memory. The read budget,
            // far larger, keeps only a run of huge headers from piling up.
            foreach (var (i, h) in ReadAhead.Ordered(headerPaths, p => Gated(p, () => PrepareHeader(p), readGate))) StoreHeader(headerPaths[i], h);
            lock (headerInfo) anyTemplate = templates.Count > 0;
        }
        // The headers whose #include closure (as the walk follows it: each name to its Chosen header) holds a
        // template: walked back from the templates, so a cycle of includes is no trouble. A unit with no head of its own
        // that includes none of them finds nothing in a walk (any other header gives the walk only directives).
        var reachTemplate = new HashSet<string>(StringComparer.Ordinal);
        if (anyTemplate)
        {
            var includedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(headerIncludes.Keys, StringComparer.Ordinal);
            var todo = new Queue<string>(seen);
            while (todo.TryDequeue(out var p))
                foreach (var raw in headerIncludes.TryGetValue(p, out var incs) ? incs : Array.Empty<string>())
                    if (Chosen(raw) is { } q)   // a file not looked at yet (an included .inc) is looked at here
                    {
                        if (!includedBy.TryGetValue(q, out var by)) includedBy[q] = by = new List<string>();
                        by.Add(p);
                        if (seen.Add(q)) todo.Enqueue(q);
                    }
            List<string> seeds;
            lock (headerInfo) seeds = templates.ToList();
            // A computed include (#include MACRO) may bring in any header: its includer is taken to reach a template.
            seeds.AddRange(headerIncludes.Where(kv => kv.Value.Contains(CodeCarver.Core.Preprocess.TemplateInstances.ComputedInclude)).Select(kv => kv.Key));
            var back = new Queue<string>(seeds);
            reachTemplate.UnionWith(seeds);
            while (back.TryDequeue(out var q))
                if (includedBy.TryGetValue(q, out var by))
                    foreach (var p in by)
                        if (reachTemplate.Add(p)) back.Enqueue(p);
        }
        bool ReachesTemplate(string[] includes)
        {
            foreach (var raw in includes)
                if (raw == CodeCarver.Core.Preprocess.TemplateInstances.ComputedInclude || Chosen(raw) is { } q && reachTemplate.Contains(q)) return true;
            return false;
        }
        Lap("headers");
        bool AnyTemplate() => anyTemplate;
        var cmdFnMacros = CodeCarver.Core.Preprocess.CommandLineMacros.AnyFunctionLike(commandLineMacros);
        // Names a translation unit defines beyond its own function heads: template-header instances (with the build's
        // function-like -D macros, `int HIDE(den)(void) {`), and the C99 inline functions it emits by declaring them.
        IEnumerable<Definition> Instances(string rel, string text)
        {
            if (IsHeader(rel) || IsAssembly(rel)) return Array.Empty<Definition>();
            var r = new List<Definition>();
            if (TemplateGate(text.Contains("include", StringComparison.Ordinal),
                             CodeCarver.Core.Preprocess.TemplateInstances.HasMacroNamedHead(text),
                             text.Contains("define", StringComparison.Ordinal),
                             () => CodeCarver.Core.Preprocess.TemplateInstances.MayHaveMacroNamedHead(text),
                             () => CodeCarver.Core.Preprocess.TemplateInstances.IncludeNames(text)))
                r.AddRange(Walked(text));
            r.AddRange(C99Defs(text));
            return r;
        }
        // An include walk is skipped when it can find nothing: no head in the unit, no template its includes bring in.
        bool TemplateGate(bool hasInclude, bool macroHead, bool hasDefine, Func<bool> mayHead, Func<string[]> includes)
        {
            if (macroHead && (cmdFnMacros || hasDefine)) return true;
            if (!hasInclude || !AnyTemplate()) return false;
            if (mayHead() || ReachesTemplate(includes())) return true;
            Interlocked.Increment(ref templateWalksSkipped);
            return false;
        }
        // An emitted unit's walk, served from an earlier round when the same text read the same headers (LinkCheckCache.Walks).
        List<Definition> Walked(string text)
        {
            if (cache is null) return TemplateDefs(text, null);
            var key = (text.Length, ContentHash(text));
            if (cache.Walks.TryGetValue(key, out var had) && had.Deps.All(d => Equals(TemplateKey(d.Raw), d.Read)))
            {
                Interlocked.Increment(ref templateWalksReused);
                return had.Defs;
            }
            var deps = new List<(string Raw, HeaderInfo? Read)>();
            var defs = TemplateDefs(text, deps);
            cache.Walks[key] = (deps, defs);
            return defs;
        }
        // deps: each include the walk read and what its text hashed to, so a later run can tell whether the answer still holds.
        List<Definition> TemplateDefs(string text, List<(string Raw, HeaderInfo? Read)>? deps)
        {
            Interlocked.Increment(ref templateWalks);
            var cost = Interlocked.Read(ref walkCost) is var c and >= 0 ? c : walkBudget;
            using var slot = walkGate.Take(cost);
            // After the gate: a walk waiting for room is not counted as running (C23 read 16 "running at once").
            using var part = Activity.Part("templateWalk", text.Length);
            var start = GC.GetAllocatedBytesForCurrentThread();
            List<Definition> found;
            HashSet<string> entered;
            long chars, readChars; int reads;
            // The headers come with only the defines of the names walks looked up before (Filtered): the walk is the walk of
            // the whole headers unless it looked up a name whose define one of them dropped. Then the names join walkNames
            // and it is walked again, with those defines; past MaxRedos, with the whole headers.
            for (var attempt = 0; ; attempt++)
            {
                var whole = attempt > MaxRedos;
                entered = new HashSet<string>(StringComparer.Ordinal);
                (chars, readChars, reads) = (0, 0, 0);
                // A header not held is read again as a new string, and the walk knows a header it has walked (and its parse)
                // by its string: each re-include of a guarded header was read, parsed and walked again with all it includes
                // (death-lite: a unit's walk resolving 2,817 includes, 2 GB). One read per header per walk, whatever name reached it.
                var read = new Dictionary<string, string?>(StringComparer.Ordinal);
                var filtered = new List<(int[] Dropped, int Through)>();
                var asked = deps is null ? null : new HashSet<string>(StringComparer.Ordinal);   // a dep per include name, not per #include line
                deps?.Clear();
                var lookedUp = new List<string>();
                found = CodeCarver.Core.Preprocess.TemplateInstances.Find(text, raw =>
                            {
                                string? t = null;
                                if (Chosen(raw) is { } path && !read.TryGetValue(path, out t))
                                {
                                    var (rt, dropped, through) = ReadHeader(path, whole);
                                    read[path] = t = rt;
                                    if (dropped.Length > 0) filtered.Add((dropped, through));
                                    if (t is not null) { reads++; readChars += t.Length; if (entered.Add(path)) chars += t.Length; }
                                }
                                Activity.Note(t?.Length ?? 0);
                                if (asked?.Add(raw) == true) deps!.Add((raw, TemplateKey(raw)));
                                return t;
                            }, commandLineMacros, Parsing, lookedUp)
                            .Select(i => new Definition(i.Name, i.Line, false, false)).ToList();
                if (whole) { Interlocked.Increment(ref templateWalksWhole); break; }
                // A name looked up that a header read dropped: one walkNames did not have when that header was filtered.
                var names = Volatile.Read(ref walkNames);
                var hashes = new List<(int Hash, int At)>(lookedUp.Count);
                var fresh = false;
                foreach (var n in lookedUp)
                {
                    var h = string.GetHashCode(n.AsSpan());
                    var at = names.TryGetValue(h, out var i) ? i : int.MaxValue;
                    fresh |= at == int.MaxValue;
                    hashes.Add((h, at));
                }
                var missed = false;
                if (filtered.Count > 0)
                {
                    var oldest = filtered.Min(r => r.Through);
                    var late = hashes.Where(x => x.At >= oldest).ToList();
                    foreach (var (dropped, through) in filtered)
                        if (late.Count > 0 && DropsAny(dropped, late.Where(x => x.At >= through).Select(x => x.Hash))) { missed = true; break; }
                }
                if (fresh) lock (headerInfo) Grow(hashes.Where(x => x.At == int.MaxValue).Select(x => x.Hash));
                if (!missed) break;
                Interlocked.Increment(ref templateWalksRedone);
            }
            closures.Add((entered.Count, chars, reads, readChars));
            var used = GC.GetAllocatedBytesForCurrentThread() - start;
            for (var was = Interlocked.Read(ref walkCost); used > was; was = Interlocked.Read(ref walkCost))
                if (Interlocked.CompareExchange(ref walkCost, used, was) == was) break;
            return found;
        }
        List<Definition> C99Defs(string text)
        {
            if (c99InlineFns is not { Count: > 0 }) return new List<Definition>();
            var starts = LineStarts(text);
            return CodeCarver.Core.Preprocess.C99Inline.Emitters(text, c99InlineFns)
                       .Select(e => new Definition(e.Name, LineOf(starts, e.Index), false, false)).ToList();
        }
        // A command-line rename (-Dsecret_rite=true_rite): a function defined as secret_rite is the symbol true_rite.
        void Renamed(ScanResult s)
        {
            if (commandLineMacros is not { Count: > 0 }) return;
            foreach (var d in s.Definitions.ToList())
                foreach (var to in CodeCarver.Core.Preprocess.CommandLineMacros.RenamesOf(commandLineMacros, d.Name)) s.Definitions.Add(d with { Name = to });
        }

        // 1. Candidate definitions from dropped files (name -> first defining file).
        // A file-scope `MACRO(a, b, c)` with no ';' reads as a definition when the next function's '{' follows.
        // A name with only such bare "definitions" that the tree #defines as a function-like macro is a macro use,
        // not a link symbol (work eval, 1.0.162: a registration macro invoked in two modules was reported as
        // "used" in one and "defined only" in the other).
        var typedDef = new HashSet<string>(StringComparer.Ordinal);
        var definers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var functionMacros = new HashSet<string>(StringComparer.Ordinal);
        // A dropped file's definitions: its own scan (kept for the run when the file can't change), the names template
        // headers it includes define (redone when a header it read now reads differently), and C99 inline emitters.
        ScanResult? ScanDropped((string Rel, string Path) f)
        {
            var header = IsHeader(f.Rel);
            var plain = header || IsAssembly(f.Rel);
            var key = (header ? "h|" : "c|") + f.Path;
            var cached = Cached(f.Path);
            string? text = null;
            DroppedEntry? e;
            if (cached && cache!.Dropped.TryGetValue(key, out e)) Interlocked.Increment(ref fromCache);
            else
            {
                Interlocked.Increment(ref filesRead);
                using (Activity.Part("dropped.read")) text = TryRead(f.Path, maxBytes);
                e = null;
                if (text is not null)
                {
                    using var scanPart = Activity.Part("dropped.scan", text.Length);
                    var own = Scan(text, header, declarators: false, definerMacros);
                    e = new DroppedEntry
                    {
                        Definitions = own.Definitions,
                        FunctionMacros = own.FunctionMacros,
                        HasInclude = text.Contains("include", StringComparison.Ordinal),
                        MacroHead = CodeCarver.Core.Preprocess.TemplateInstances.HasMacroNamedHead(text),
                        HasDefine = text.Contains("define", StringComparison.Ordinal),
                        // Only a tree with a template asks (TemplateGate); an entry made without one says "may" (walk).
                        MayHead = !anyTemplate || CodeCarver.Core.Preprocess.TemplateInstances.MayHaveMacroNamedHead(text),
                        Includes = anyTemplate ? CodeCarver.Core.Preprocess.TemplateInstances.IncludeNames(text) : Array.Empty<string>(),
                        C99 = plain ? new List<Definition>() : C99Defs(text),
                    };
                }
                if (cached) e = cache!.Dropped.GetOrAdd(key, e);
            }
            if (e is null) return null;
            var templateDefs = new List<Definition>();
            if (!plain && TemplateGate(e.HasInclude, e.MacroHead, e.HasDefine, () => e.MayHead, () => e.Includes))
            {
                List<(string Raw, HeaderInfo? Read)>? deps;
                List<Definition>? had;
                lock (e) { deps = e.Deps; had = e.TemplateDefs; }
                if (deps is not null && deps.All(d => Equals(TemplateKey(d.Raw), d.Read)))
                    templateDefs = had!;
                else
                {
                    if (text is null) Interlocked.Increment(ref instancesRedone);
                    text ??= TryRead(f.Path, maxBytes);
                    if (text is null) return null;
                    var nd = new List<(string Raw, HeaderInfo? Read)>();
                    templateDefs = TemplateDefs(text, nd);
                    lock (e) { e.Deps = nd; e.TemplateDefs = templateDefs; }
                }
            }
            var s = new ScanResult();
            s.Definitions.AddRange(e.Definitions);
            s.Definitions.AddRange(templateDefs);
            s.Definitions.AddRange(e.C99);
            s.FunctionMacros.UnionWith(e.FunctionMacros);
            Renamed(s);
            return s;
        }
        var droppedSorted = droppedList.OrderBy(d => d.Rel, StringComparer.Ordinal).ToList();
        Activity.Begin("check: dropped files scanned", droppedSorted.Count);
        foreach (var (di, dscan) in ReadAhead.Ordered(droppedSorted, d => Gated(d.Path, () => ScanDropped(d))))
        {
            using var consume = Activity.Part("dropped.consume");
            var rel = droppedSorted[di].Rel;
            if (dscan is null) { skipped++; continue; }
            checkedFiles++;
            var header = IsHeader(rel);
            functionMacros.UnionWith(dscan.FunctionMacros);
            foreach (var d in dscan.Definitions)
                if (header || !d.Static)
                {
                    droppedDefs.TryAdd(d.Name, (rel, d.Line));
                    if (!definers.TryGetValue(d.Name, out var dl)) definers[d.Name] = dl = new List<string>();
                    if (!dl.Contains(rel)) dl.Add(rel);
                    if (!d.Bare) typedDef.Add(d.Name);
                }
        }
        // Functions pruned out of kept files. A pruned static is only visible in its own file.
        var prunedStatics = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        // The original's definitions the emitted copy doesn't have, or null when the file is unchanged.
        // The original's length, content hash and definitions are kept for the run when it can't change.
        IEnumerable<Definition>? Pruned((string Rel, string Path) f)
        {
            if (IsAssembly(f.Rel) || original!(f.Rel) is not { } op) return null;
            var header = IsHeader(f.Rel);
            var key = (header ? "h|" : "c|") + op;
            var cached = Cached(op);
            string? otext = null;
            OriginalEntry? oe;
            if (cached && cache!.Originals.TryGetValue(key, out oe)) Interlocked.Increment(ref fromCache);
            else
            {
                Interlocked.Increment(ref filesRead);
                otext = TryRead(op, maxBytes);
                oe = otext is null ? null
                    : new OriginalEntry(otext.Length, ContentHash(otext), Scan(otext, header, declarators: false, definerMacros).Definitions);
                if (cached) oe = cache!.Originals.GetOrAdd(key, oe);
            }
            if (oe is null) return null;
            var etext = TryRead(f.Path, maxBytes);
            if (etext is null) return null;
            if (otext is not null ? otext == etext : etext.Length == oe.Length && ContentHash(etext) == oe.Hash) return null;
            var kept = Scan(etext, header, declarators: false, definerMacros).Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            return oe.Definitions.Where(d => !kept.Contains(d.Name)).ToList();
        }
        Lap("dropped");
        if (original is not null)
        {
            Activity.Begin("check: emitted files compared with their originals", emittedList.Count);
            foreach (var (pi, gone) in ReadAhead.Ordered(emittedList, e => Gated(e.Path, () => Pruned(e))))
            {
                var rel = emittedList[pi].Rel;
                if (gone is null) continue;
                var header = IsHeader(rel);
                foreach (var d in gone)
                {
                    if (d.Static && !header)
                    {
                        if (!prunedStatics.TryGetValue(rel, out var ps)) prunedStatics[rel] = ps = new(StringComparer.Ordinal);
                        ps.TryAdd(d.Name, d.Line);
                        continue;
                    }
                    droppedDefs.TryAdd(d.Name, (rel, d.Line));
                    if (!definers.TryGetValue(d.Name, out var dl)) definers[d.Name] = dl = new List<string>();
                    if (!dl.Contains(rel)) dl.Add(rel);
                    if (!d.Bare) typedDef.Add(d.Name);
                }
            }
        }
        Lap("pruned");
        var staticViolations = new List<LinkViolation>();
        if (droppedDefs.Count == 0 && prunedStatics.Count == 0)
            return Done(Array.Empty<LinkViolation>());

        // 2+3. Definitions and uses in the emitted files.
        var emittedDefs = new HashSet<string>(StringComparer.Ordinal);
        var inlineHeaderDefs = new HashSet<string>(StringComparer.Ordinal);
        // Per file: the function-like macros it #defines on live lines, and what it #includes. A call `name(...)` where
        // a function-like macro `name` is visible (this file or a header it includes) is expanded by the preprocessor:
        // it never reaches the linker, whatever a same-named function elsewhere does.
        var fileMacros = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fileIncludes = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // include basenames
        var keptNames = new CodeCarver.Core.Util.NamePool();
        var aliasMacros = new HashSet<string>(StringComparer.Ordinal);
        var macroDeclarators = new List<(string Name, string Macro, string Target, string Rel, int Line)>();
        // Weak: declared weak in a header (it may be included anywhere), or every use sits in a file that declares it weak
        // itself. One file's weak declaration does not make another file's reference weak: ELF fails the link on that one.
        var weakNames = new HashSet<string>(StringComparer.Ordinal);
        var strongUse = new HashSet<string>(StringComparer.Ordinal);
        var weakUse = new HashSet<string>(StringComparer.Ordinal);
        // Only a symbol the build wraps (--wrap=X): a __wrap_X elsewhere is a unit-test mock, not part of this link.
        var anyWrap = wrapped is { Count: > 0 } && droppedDefs.Keys.Any(k => k.StartsWith("__wrap_", StringComparison.Ordinal));
        (ScanResult Scan, bool[]? Map)? ScanEmitted((string Rel, string Path) f)
        {
            string? text;
            using (Activity.Part("emitted.read")) text = TryRead(f.Path, maxBytes);
            if (text is null) return null;
            bool[]? map;
            using (Activity.Part("emitted.deadLines", text.Length)) map = deadLines?.Invoke(f.Rel, text);
            ScanResult s;
            using (Activity.Part("emitted.scan", text.Length)) s = IsAssembly(f.Rel) ? ScanAssembly(text) : Scan(text, IsHeader(f.Rel), definerMacros: definerMacros);
            using (Activity.Part("emitted.instances", text.Length)) s.Definitions.AddRange(Instances(f.Rel, text));
            Renamed(s);
            // A use is read below only for a name a dropped file defines, a static pruned from this file, or a wrapped
            // symbol: the others are let go here, so a result waiting its turn holds little.
            var prunedHere = prunedStatics.TryGetValue(f.Rel, out var ph) ? ph : null;
            bool Wanted(string n) => droppedDefs.ContainsKey(n) || (prunedHere?.ContainsKey(n) ?? false) || (anyWrap && wrapped!.Contains(n));
            s.Uses.RemoveAll(u => !Wanted(u.Name));
            s.CallUses.RemoveWhere(u => !Wanted(u.Name));
            return (s, map);
        }
        var emittedSorted = emittedList.OrderBy(e => e.Rel, StringComparer.Ordinal).ToList();
        Activity.Begin("check: emitted files scanned", emittedSorted.Count);
        foreach (var (ei, scanned) in ReadAhead.Ordered(emittedSorted, e => Gated(e.Path, () => ScanEmitted(e))))
        {
            using var consume = Activity.Part("emitted.consume");
            var rel = emittedSorted[ei].Rel;
            if (scanned is not { } got) { skipped++; continue; }
            var (scan, map) = got;
            checkedFiles++;
            bool IsDead(int line) => map is not null && line < map.Length && map[line];
            functionMacros.UnionWith(scan.FunctionMacros);
            // Held for every emitted file until the end: only what Sees reads (a file with neither sees no macro), each
            // name one string for the run.
            var liveMacros = scan.FunctionMacroLines.Where(m => !IsDead(m.Line)).Select(m => keptNames.Get(m.Name, 0, m.Name.Length)).ToHashSet(StringComparer.Ordinal);
            if (liveMacros.Count > 0) fileMacros[rel] = liveMacros;
            if (scan.Includes.Count > 0)
                fileIncludes[rel] = scan.Includes.Select(i => keptNames.Get(i, i.Length - Path.GetFileName(i.AsSpan()).Length, i.Length)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var d in scan.Definitions)
                if (IsDead(d.Line)) continue;
                else if (d.Inline && !d.Static && IsHeader(rel)) inlineHeaderDefs.Add(d.Name);
                else emittedDefs.Add(d.Name);
            aliasMacros.UnionWith(scan.AliasMacros);
            foreach (var md in scan.MacroDeclarators) if (!IsDead(md.Line)) macroDeclarators.Add((md.Name, md.Macro, md.Target, rel, md.Line));
            if (IsHeader(rel)) weakNames.UnionWith(scan.WeakDeclarations);
            foreach (var (name, _) in scan.Uses) (scan.WeakDeclarations.Contains(name) ? weakUse : strongUse).Add(name);
            if (prunedStatics.TryGetValue(rel, out var pruned))
            {
                var definedHere = scan.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (name, line) in scan.Uses)
                    if (pruned.TryGetValue(name, out var defLine) && !definedHere.Contains(name) && reported.Add(name)
                        && !(scan.CallUses.Contains((name, line)) && scan.FunctionMacros.Contains(name)))
                        staticViolations.Add(new LinkViolation(name, rel, rel, line, DeadOnly: IsDead(line), DefinedLine: defLine)
                                             { DefinedInAll = new[] { rel } });
            }
            foreach (var (name, line) in scan.Uses)
            {
                // Linked with --wrap=X, every X call goes to __wrap_X: a dropped wrapper breaks the link.
                if (anyWrap && wrapped!.Contains(name) && droppedDefs.ContainsKey("__wrap_" + name)) (IsDead(line) ? dead : live).TryAdd("__wrap_" + name, (rel, line));
                if (!droppedDefs.ContainsKey(name)) continue;
                if (scan.CallUses.Contains((name, line))) { macroCalls.Add((name, rel, line, IsDead(line))); continue; }
                var into = IsDead(line) ? dead : live;
                into.TryAdd(name, (rel, line));
            }
        }
        Lap("emittedScan");
        // A non-static inline body in a header provides the symbol only in C++. In C it emits nothing: a translation unit
        // emits it (C99, by declaring it extern) or defines it out of line (GNU extern inline). When such a translation
        // unit was dropped, the header's body links nothing.
        foreach (var name in inlineHeaderDefs)
            if (!(definers.TryGetValue(name, out var dl) && dl.Any(r => !IsHeader(r)))) emittedDefs.Add(name);
        // Symbols the link itself names (--defsym's right side, --undefined, a linker script's EXTERN...): ld needs
        // them whatever the C code calls.
        if (linkUses is not null)
            foreach (var (name, rel, line) in linkUses)
                if (droppedDefs.ContainsKey(name)) { live.TryAdd(name, (rel, line)); strongUse.Add(name); }
        // A function-like macro is visible in a file that #defines it or includes (by basename, transitively) an emitted
        // file that does. Asked per macro name: the files that can see it, walking back from its definers through the
        // files that include them. (A set of every visible macro per file was files x macros: tens of GB on a big tree.)
        // Files by number, and for each include basename the files that include it: built on the first question.
        Dictionary<string, int>? fileIndex = null;
        List<string>? files = null;
        Dictionary<string, List<int>>? includersByBase = null;
        var seeing = new Dictionary<string, System.Collections.BitArray>(StringComparer.Ordinal);
        bool Sees(string rel, string name)
        {
            if (fileIndex is null)
            {
                files = fileIncludes.Keys.Union(fileMacros.Keys, StringComparer.Ordinal).ToList();
                fileIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < files.Count; i++) fileIndex[files[i]] = i;
                includersByBase = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (f, incs) in fileIncludes)
                    foreach (var b in incs.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!includersByBase.TryGetValue(b!, out var l)) includersByBase[b!] = l = new List<int>();
                        l.Add(fileIndex[f]);
                    }
            }
            if (!seeing.TryGetValue(name, out var sees))
            {
                sees = new System.Collections.BitArray(files!.Count);
                var stack = new Stack<int>();
                foreach (var (f, ms) in fileMacros)
                    if (ms.Contains(name)) { var i = fileIndex[f]; if (!sees[i]) { sees[i] = true; stack.Push(i); } }
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    if (!includersByBase!.TryGetValue(Path.GetFileName(files[x]), out var by)) continue;
                    foreach (var i in by) if (!sees[i]) { sees[i] = true; stack.Push(i); }
                }
                seeing[name] = sees;
            }
            return fileIndex.TryGetValue(rel, out var at) && sees[at];
        }
        foreach (var (name, macro, target, mrel, mline) in macroDeclarators)
            if (aliasMacros.Contains(macro))
            {
                emittedDefs.Add(name);
                if (droppedDefs.ContainsKey(target)) live.TryAdd(target, (mrel, mline));   // the alias needs its target
            }
        foreach (var (name, rel, line, isDead) in macroCalls)
        {
            // Only a name some file #defines as a function-like macro can be one (the include closure is the cost).
            if (functionMacros.Contains(name) && Sees(rel, name)) continue;   // a macro invocation, not a reference
            (isDead ? dead : live).TryAdd(name, (rel, line));
        }

        var violations = new List<LinkViolation>();
        foreach (var name in live.Keys.Concat(dead.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (emittedDefs.Contains(name)) continue;

            if (functionMacros.Contains(name) && !typedDef.Contains(name)) continue;
            var isLive = live.TryGetValue(name, out var at);
            if (!isLive) at = dead[name];
            var def = droppedDefs[name];
            violations.Add(new LinkViolation(name, def.Rel, at.Rel, at.Line, DeadOnly: !isLive, DefinedLine: def.Line) { DefinedInAll = definers[name], Weak = weakNames.Contains(name) || (weakUse.Contains(name) && !strongUse.Contains(name)) });
        }
        violations.AddRange(staticViolations);
        return Done(violations);
    }

    /// <summary>A dropped file's part of a check that later runs can reuse (see <see cref="LinkCheckCache"/>).</summary>
    internal sealed class DroppedEntry
    {
        public required List<Definition> Definitions;
        public required HashSet<string> FunctionMacros;
        public required bool HasInclude, MacroHead, HasDefine, MayHead;
        public required string[] Includes;
        public required List<Definition> C99;
        // The template-header names, with what the walk read to find them; under lock(this).
        public List<(string Raw, HeaderInfo? Read)>? Deps;
        public List<Definition>? TemplateDefs;
    }

    /// <summary>An original file of a carve inside kept files: what its emitted copy is compared with.</summary>
    internal sealed record OriginalEntry(int Length, ulong Hash, List<Definition> Definitions);

    /// <summary>A header as the template walk reads it: is it a template, and the length and hash of that text (what a
    /// cached answer is checked against, instead of holding the text).</summary>
    internal sealed record HeaderInfo(bool Template, int Length, ulong Hash);

    /// <summary>Walks of a unit again with more defines (a name looked up that a header held had dropped) before it is
    /// walked with the whole headers: each walk again goes one macro further down a chain of names.</summary>
    const int MaxRedos = 16;

    /// <summary>A header with less directive text than this is held whole. The text is in the big headers (a register
    /// map), and a walk seldom looks up one of their defines; filtered, the small ones made almost every walk a redo
    /// (death-lite: 2 to 4 names a unit, each from a header of a few KB).</summary>
    const int FilterChars = 64 * 1024;

    /// <summary>Characters of non-template header text the check holds at once: a 32nd of the machine's memory, or
    /// CODECARVER_CHECK_HEADER_MB.</summary>
    static long HeaderTextBudget()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_CHECK_HEADER_MB"), out var mb) && mb >= 0)
            return mb * 1024 * 1024 / sizeof(char);
        var total = CodeCarver.Core.Util.MemoryGate.BudgetBytes();
        return (total > 0 ? total / 32 : 512L << 20) / sizeof(char);
    }

    /// <summary>The tree's shape as the check saw it, numbers only (no names), for sizing a test corpus like it: the
    /// headers read (count, KB p50/p95/max, how many over 1 MB, total MB), each template walk's include closure (distinct
    /// files and KB, includes followed and KB with re-includes, p50/p95/max) and the share of files walked. Sizes are in
    /// chars (bytes for one-byte text).</summary>
    static void Shape(Dictionary<string, long> stats, ICollection<long> headerSizes, IEnumerable<(int Files, long Chars, int Reads, long ReadChars)> closures,
                      long walks, long skipped)
    {
        void Spread(string key, IEnumerable<long> values)
        {
            var v = values.OrderBy(x => x).ToArray();
            if (v.Length == 0) return;
            long At(double q) => v[Math.Min(v.Length - 1, (int)(q * v.Length))];
            stats[key + ".p50"] = At(0.50); stats[key + ".p95"] = At(0.95); stats[key + ".max"] = v[^1];
        }
        stats["shape.headers"] = headerSizes.Count;
        stats["shape.headersOver1Mb"] = headerSizes.Count(s => s > 1L << 20);
        stats["shape.headerTotalMb"] = headerSizes.Sum() >> 20;
        Spread("shape.headerKb", headerSizes.Select(s => s >> 10));
        var c = closures.ToList();
        Spread("shape.walkFiles", c.Select(x => (long)x.Files));
        Spread("shape.walkKb", c.Select(x => x.Chars >> 10));
        Spread("shape.walkReads", c.Select(x => (long)x.Reads));
        Spread("shape.walkReadKb", c.Select(x => x.ReadChars >> 10));
        stats["shape.walkedPct"] = walks + skipped == 0 ? 0 : walks * 100 / (walks + skipped);
    }

    /// <summary>What a header's first template parse allocates for each char of it, as measured (the ops, the define
    /// index), with room: what a parse takes from the walk budget.</summary>
    const long ParseBytesPerChar = 2;

    sealed class ParseSlot(Activity.PartScope part, CodeCarver.Core.Util.ByteGate.Held held) : IDisposable
    {
        public void Dispose() { held.Dispose(); part.Dispose(); }
    }

    /// <summary>Bytes the template walks allocate at once: an 8th of the machine's memory or the run
    /// limit (2.5 GB under memoryStopMb 20000), at least 256 MB, or CODECARVER_CHECK_WALK_MB.</summary>
    static long WalkBudget()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_CHECK_WALK_MB"), out var mb) && mb > 0)
            return mb * 1024 * 1024;
        var total = CodeCarver.Core.Util.MemoryGate.BudgetBytes();
        return Math.Max(256L << 20, total > 0 ? total / 8 : 2L << 30);
    }

    /// <summary>Bytes of files the check scans at once: a 512th of the machine's memory (64 MB of 32 GB, about 2 GB while
    /// scanning), at least 16 MB, or CODECARVER_CHECK_SCAN_MB.</summary>
    /// <summary>Bytes of headers the check reads at once in its first loop: a 32nd of the machine's memory (1 GB of
    /// 32 GB), or CODECARVER_CHECK_READ_MB.</summary>
    static long ReadBudget()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_CHECK_READ_MB"), out var mb) && mb > 0)
            return mb * 1024 * 1024;
        var total = CodeCarver.Core.Util.MemoryGate.BudgetBytes();
        return Math.Max(64L << 20, total > 0 ? total / 32 : 1L << 30);
    }

    static long ScanBudget()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_CHECK_SCAN_MB"), out var mb) && mb > 0)
            return mb * 1024 * 1024;
        var total = CodeCarver.Core.Util.MemoryGate.BudgetBytes();
        return Math.Max(16L << 20, total > 0 ? total / 512 : 64L << 20);
    }

    static ulong ContentHash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    static string? TryRead(string path, long maxBytes)
    {
        try
        {
            // Metered (io.read, and per step in the summary): the check reads the sources too, often on a share.
            using (CodeCarver.Core.Util.IoMeter.Start(CodeCarver.Core.Util.IoMeter.Kind.Read))
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > maxBytes) return null;
            }
            // Latin-1 is byte-transparent: identifiers are ASCII, and no byte sequence can fail to decode. IoMeter's
            // read decodes once into the string (File.ReadAllText held it twice, in 4 KB reads).
            return CodeCarver.Core.Util.IoMeter.ReadAllText(path, System.Text.Encoding.Latin1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsHeader(string rel) =>
        Path.GetExtension(rel).ToLowerInvariant() is ".h" or ".hpp" or ".hxx" or ".hh" or ".h++" or ".inl"
            or ".ipp" or ".tcc" or ".tpp" or ".inc" or ".def";

    // ---- tokenizer + scope tracking ------------------------------------------------------------------

    /// <param name="Bare">Nothing but #define lines precedes the name in its statement (no return type).</param>
    /// <param name="Data">A file-scope variable, not a function.</param>
    public readonly record struct Definition(string Name, int Line, bool Static, bool Inline, bool Bare = false, bool Data = false);

    public sealed class ScanResult
    {
        public List<Definition> Definitions { get; } = new();
        public List<(string Name, int Line)> Uses { get; } = new();
        /// <summary>Names this file #defines as function-like macros.</summary>
        public HashSet<string> FunctionMacros { get; } = new(StringComparer.Ordinal);
        /// <summary>Each function-like #define, with its line.</summary>
        public List<(string Name, int Line)> FunctionMacroLines { get; } = new();
        /// <summary>What each #include names ("x.h" or &lt;x.h&gt;, without the delimiters).</summary>
        public List<string> Includes { get; } = new();
        /// <summary>Uses written as a call, <c>name(</c>: a function-like macro of that name would expand them.</summary>
        public HashSet<(string Name, int Line)> CallUses { get; } = new();
        /// <summary>Function-like macros this file defines whose body is <c>alias(#arg)</c>.</summary>
        public HashSet<string> AliasMacros { get; } = new(StringComparer.Ordinal);
        /// <summary><c>decl(...) MACRO(target);</c>: a definition of decl when MACRO is an alias macro (which may be
        /// #defined in another file).</summary>
        public List<(string Name, int Line, string Macro, string Target)> MacroDeclarators { get; } = new();
        /// <summary>Names declared weak without a body: a reference to one may stay unresolved.</summary>
        public HashSet<string> WeakDeclarations { get; } = new(StringComparer.Ordinal);
        /// <summary>Asm labels: the C name, its symbol, and the line.</summary>
        public List<(string Name, string Symbol, int Line)> AsmLabels { get; } = new();
    }

    static readonly System.Text.RegularExpressions.Regex FuncLikeDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex IncludeLine = new(
        @"^[ \t]*#[ \t]*include[ \t]*[""<]([^"">\r\n]+)["">]",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Names a file defines without a body: aliases and assembler symbols. A reference to one links.
    //   void hook(void) __attribute__((weak, alias("impl")));      __attribute__((alias("impl"))) void hook(void);
    //   void hook(void) WEAK_ALIAS(impl);   (a macro whose body is alias(#arg))      #pragma weak hook = impl
    //   __asm__(".set hook, impl")   .equ / .equiv      .globl hook ... hook:
    const System.Text.RegularExpressions.RegexOptions Rx = System.Text.RegularExpressions.RegexOptions.Multiline
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
    // name = the alias (a definition), target = what it names (a use: the alias needs it).
    static readonly System.Text.RegularExpressions.Regex AliasAfter = new(
        CodeCarver.Core.Frontend.Attributes.AliasAfterPattern, Rx);
    static readonly System.Text.RegularExpressions.Regex AliasBefore = new(
        CodeCarver.Core.Frontend.Attributes.AliasBeforePattern, Rx);
    static readonly System.Text.RegularExpressions.Regex AliasMacroDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(\s*(\w+)\s*\)[^\n]*\balias\s*\(\s*#\s*\2\b", Rx);
    static readonly System.Text.RegularExpressions.Regex MacroDeclarator = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*([A-Za-z_]\w*)\s*\(\s*(\w+)\s*\)\s*;", Rx);
    // #pragma weak a = b  and  _Pragma("weak a = b"); without "= b" only a is declared weak.
    static readonly System.Text.RegularExpressions.Regex PragmaWeak = new(
        @"(?:^[ \t]*#[ \t]*pragma[ \t]+weak|\b_Pragma\s*\(\s*""\s*weak)[ \t]+([A-Za-z_]\w*)(?:[ \t]*=[ \t]*([A-Za-z_]\w*))?", Rx);
    static readonly System.Text.RegularExpressions.Regex AsmSet = new(
        @"\.(?:set|equ|equiv)\s+([A-Za-z_.$][\w.$]*)\s*,\s*([A-Za-z_.$][\w.$]*)?", Rx);
    // A weak declaration (no body): the reference may stay unresolved, so it needs no definition.
    static readonly System.Text.RegularExpressions.Regex WeakDeclAfter = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*__attribute__\s*\(\([^;{}()]*\bweak\b[^;{}()]*\)\)\s*;", Rx);
    static readonly System.Text.RegularExpressions.Regex WeakDeclBefore = new(
        @"__attribute__\s*\(\([^;{}()]*\bweak\b[^;{}()]*\)\)[\w\s\*]*?\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*;", Rx);
    // `int hidden(int) __asm__("sym");` - the declarator's symbol is sym.
    static readonly System.Text.RegularExpressions.Regex AsmLabel = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*(?:__asm__|__asm|asm)\s*\(\s*""([A-Za-z_.$][\w.$]*)""\s*\)", Rx);
    static readonly System.Text.RegularExpressions.Regex RedefineExtname = new(
        @"^[ \t]*#[ \t]*pragma[ \t]+redefine_extname[ \t]+([A-Za-z_]\w*)[ \t]+([A-Za-z_.$][\w.$]*)", Rx);
    static readonly System.Text.RegularExpressions.Regex AsmGlobal = new(
        @"(?:^|[\s""])\.(?:globl|global|weak|weakref)\s+([A-Za-z_.$][\w.$]*(?:\s*,\s*[A-Za-z_.$][\w.$]*)*)", Rx);
    static readonly System.Text.RegularExpressions.Regex AsmPublic = new(
        @"^[ \t]*(?:PUBLIC|EXPORT|GLOBAL|XDEF|\.public)[ \t]+([A-Za-z_]\w*(?:[ \t]*,[ \t]*[A-Za-z_]\w*)*)|^[ \t]*([A-Za-z_]\w*)[ \t]+PROC\b",
        Rx | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Assembly source: a GNU/ARM/MASM-style file, not C.</summary>
    public static bool IsAssembly(string rel) =>
        Path.GetExtension(rel).ToLowerInvariant() is ".s" or ".asm" or ".sx" or ".a51" or ".s43" or ".s90";

    static int[] LineStarts(string text)
    {
        // Counted first and filled at size: a list grown line by line, then copied, was the scan's largest int garbage.
        var starts = new int[text.AsSpan().Count('\n') + 1];
        var n = 1;
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) starts[n++] = i + 1;
        return starts;
    }

    /// <summary>The name of the function-like #define starting the line at <paramref name="p"/>, as
    /// <see cref="FuncLikeDefine"/> matches there, or null: read by hand, where the regex built a match, its groups and
    /// strings for every function-like macro of a register header.</summary>
    static string? FuncLikeDefineAt(string text, int p)
    {
        var i = p;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (i >= text.Length || text[i] != '#') return null;
        i++;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (string.CompareOrdinal(text, i, "define", 0, 6) != 0) return null;
        i += 6;
        var k = i;
        while (k < text.Length && text[k] is ' ' or '\t') k++;
        if (k == i || k >= text.Length || !(char.IsAsciiLetter(text[k]) || text[k] == '_')) return null;
        var at = k++;
        while (k < text.Length && CodeCarver.Core.Preprocess.DefinerMacros.IsWordChar(text[k])) k++;
        return k < text.Length && text[k] == '(' ? text[at..k] : null;
    }
    static int LineOf(int[] starts, int index)
    {
        var k = Array.BinarySearch(starts, index);
        return (k >= 0 ? k : ~k - 1) + 1;
    }

    /// <summary>Aliases, weak pragmas and assembler symbols defined in C text (see the regexes above). A declarator
    /// followed by a macro goes to <see cref="ScanResult.MacroDeclarators"/>: whether that macro is an alias macro is
    /// decided across files.</summary>
    static void BodilessDefinitions(string text, ScanResult result, bool declarators)
    {
        var starts = LineStarts(text);
        void Add(string name, int index) => result.Definitions.Add(new Definition(name, LineOf(starts, index), false, false));
        // The alias needs its target: a use (it sits in a string or a directive, which the tokenizer skips).
        void Use(System.Text.RegularExpressions.Group g) { if (g.Success) result.Uses.Add((g.Value, LineOf(starts, g.Index))); }
        if (text.Contains("alias", StringComparison.Ordinal) || text.Contains("ifunc", StringComparison.Ordinal) || text.Contains("weakref", StringComparison.Ordinal))   // an ifunc: defined by its resolver
        {
            foreach (System.Text.RegularExpressions.Match m in AliasAfter.Matches(text)) { Add(m.Groups["name"].Value, m.Groups["name"].Index); Use(m.Groups["target"]); }
            foreach (System.Text.RegularExpressions.Match m in AliasBefore.Matches(text)) { Add(m.Groups["name"].Value, m.Groups["name"].Index); Use(m.Groups["target"]); }
            foreach (System.Text.RegularExpressions.Match m in AliasMacroDefine.Matches(text)) result.AliasMacros.Add(m.Groups[1].Value);
        }
        if (declarators) foreach (System.Text.RegularExpressions.Match m in MacroDeclarator.Matches(text))
            result.MacroDeclarators.Add((m.Groups[1].Value, LineOf(starts, m.Groups[1].Index), m.Groups[2].Value, m.Groups[3].Value));
        if (text.Contains("weak", StringComparison.Ordinal))
        {
            foreach (System.Text.RegularExpressions.Match m in PragmaWeak.Matches(text))
            {
                if (m.Groups[2].Success) { Add(m.Groups[1].Value, m.Groups[1].Index); Use(m.Groups[2]); }
                else result.WeakDeclarations.Add(m.Groups[1].Value);
            }
            foreach (System.Text.RegularExpressions.Match m in WeakDeclAfter.Matches(text)) result.WeakDeclarations.Add(m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in WeakDeclBefore.Matches(text)) result.WeakDeclarations.Add(m.Groups[1].Value);
        }
        // `#pragma redefine_extname cname sym` renames like an asm label.
        if (text.Contains("redefine_extname", StringComparison.Ordinal))
            foreach (System.Text.RegularExpressions.Match m in RedefineExtname.Matches(text))
            {
                Add(m.Groups[1].Value, m.Groups[1].Index);
                result.AsmLabels.Add((m.Groups[1].Value, m.Groups[2].Value, LineOf(starts, m.Groups[2].Index)));
            }
        if (text.Contains("asm", StringComparison.Ordinal))
        {
            foreach (System.Text.RegularExpressions.Match m in AsmSet.Matches(text)) { Add(m.Groups[1].Value, m.Groups[1].Index); Use(m.Groups[2]); }
            // An asm label renames the symbol: the C name is declared here (calls to it never reach a same-named
            // function elsewhere); the symbol is defined here when the C name has a body here, else it is used.
            foreach (System.Text.RegularExpressions.Match m in AsmLabel.Matches(text))
            {
                Add(m.Groups[1].Value, m.Groups[1].Index);
                result.AsmLabels.Add((m.Groups[1].Value, m.Groups[2].Value, LineOf(starts, m.Groups[2].Index)));
            }
            // .globl name together with a `name:` label in the same text.
            foreach (var (n, index) in CodeCarver.Core.Frontend.AsmSymbols.DefinedLabels(text)) Add(n, index);
        }
    }

    /// <summary>An assembly file: its exported and aliased symbols are definitions (<c>.globl</c>/<c>.weak</c>,
    /// <c>PUBLIC</c>/<c>EXPORT</c>, <c>name PROC</c>, <c>.set</c>/<c>.equ</c>). Uses are scanned as for C, so a vector
    /// table's <c>.word handler</c> still counts.</summary>
    public static ScanResult ScanAssembly(string text)
    {
        var result = Scan(text);
        result.Definitions.Clear();
        var starts = LineStarts(text);
        void Add(string name, int index) => result.Definitions.Add(new Definition(name, LineOf(starts, index), false, false));
        foreach (System.Text.RegularExpressions.Match m in AsmGlobal.Matches(text))
            foreach (var n in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) Add(n, m.Groups[1].Index);
        foreach (System.Text.RegularExpressions.Match m in AsmPublic.Matches(text))
        {
            if (m.Groups[1].Success)
                foreach (var n in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) Add(n, m.Groups[1].Index);
            else Add(m.Groups[2].Value, m.Groups[2].Index);
        }
        foreach (System.Text.RegularExpressions.Match m in AsmSet.Matches(text)) Add(m.Groups[1].Value, m.Groups[1].Index);
        return result;
    }

    internal readonly record struct Tok(string Text, int Line, bool Ident, bool InDefine);

    // Words that can precede a USE of a name (so "keyword name" is not a declaration of name).
    static readonly HashSet<string> ExprKeywords = new(StringComparer.Ordinal)
    {
        "return", "case", "goto", "sizeof", "else", "do", "typeof", "__typeof__", "alignof", "_Alignof",
        "__alignof__", "throw", "new", "delete", "co_return", "co_await", "co_yield", "and", "or", "not",
        "decltype", "noexcept", "if", "while", "for", "switch",
    };

    static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "switch", "return", "sizeof", "do", "else", "case", "default", "break",
        "continue", "goto", "typedef", "struct", "union", "enum", "class", "namespace", "template",
        "typename", "static", "extern", "const", "volatile", "inline", "void", "int", "char", "short", "long",
        "float", "double", "signed", "unsigned", "auto", "register", "operator", "public", "private",
        "protected", "virtual", "using", "this", "true", "false", "nullptr", "defined", "__attribute__",
        "__declspec", "decltype", "alignof", "static_assert", "_Static_assert", "asm", "__asm__", "__asm",
    };

    /// <summary>Tokenize and collect definitions and uses. In a <paramref name="header"/>, uses inside the body
    /// of a static/inline/template definition, or inside a C++ class body, are not collected: a compiler emits
    /// such a definition only where it is used, and then reachability followed it from that use.</summary>
    /// <param name="declarators">Collect <see cref="ScanResult.MacroDeclarators"/> (only emitted files need them).</param>
    /// <param name="definerMacros">The tree's definer macros (the file's own are always added).</param>
    public static ScanResult Scan(string text, bool header = false, bool declarators = true,
                                  CodeCarver.Core.Preprocess.DefinerMacros.Set? definerMacros = null,
        IEnumerable<(string Name, string Rel, int Line)>? linkUses = null,
        IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? commandLineMacros = null,
        IReadOnlySet<string>? c99InlineFns = null)
    {
        // A scan made ~11 bytes of garbage per character of its text: many big files at once outgrew the machine. One
        // already in a scan on this thread (ScanAssembly's) is counted by the outer one.
        if (t_scanning) return ScanText(text, header, declarators, definerMacros);
        using var lease = s_scanMemory.Value.Acquire(text.Length);
        t_scanning = true;
        try { return ScanText(text, header, declarators, definerMacros); }
        finally { t_scanning = false; }
    }

    [ThreadStatic] static bool t_scanning;

    /// <summary>The scans running at once, held to an eighth of the machine's memory (BudgetBytes) at a cost per character.</summary>
    static readonly Lazy<CodeCarver.Core.Util.MemoryGate> s_scanMemory = new(() =>
    {
        var total = CodeCarver.Core.Util.MemoryGate.BudgetBytes();
        return new CodeCarver.Core.Util.MemoryGate(total > 0 ? total / 8 : 2L << 30, bytesPerChar: 16, minChars: 256 * 1024);
    });

    static ScanResult ScanText(string text, bool header, bool declarators, CodeCarver.Core.Preprocess.DefinerMacros.Set? definerMacros)
    {
        // Trigraphs and names split by a backslash-newline, as the compiler reads them.
        text = CodeCarver.Core.Preprocess.SourceText.Normalize(text);
        // `#if 0` blocks are dead in every configuration and often not C (unbalanced braces): never read them.
        text = CodeCarver.Core.Preprocess.PreprocessorScanner.BlankAlwaysDead(text);
        var toks = Tokenize(text);
        var result = new ScanResult();
        if (text.Contains('#'))
        {
            var starts = LineStarts(text);
            if (text.Contains("define", StringComparison.Ordinal))
                for (var ln = 0; ln < starts.Length; ln++)
                    if (FuncLikeDefineAt(text, starts[ln]) is { } name)
                    {
                        result.FunctionMacros.Add(name);
                        result.FunctionMacroLines.Add((name, ln + 1));
                    }
            if (text.Contains("include", StringComparison.Ordinal))
                foreach (System.Text.RegularExpressions.Match m in IncludeLine.Matches(text)) result.Includes.Add(m.Groups[1].Value);
        }
        BodilessDefinitions(text, result, declarators);

        // Brace stack: 'T' transparent (namespace / extern "C"), 'F' function body, 'Q' function body whose uses
        // don't count (header inline), 'C' class body in a header, 'O' other.
        var braces = new Stack<char>();
        // How many open braces of each kind: asked at every name, where walking the stack (through LINQ, an allocation
        // per call) was most of the scan's garbage.
        var open = new int[128];
        bool AtFileScope() => braces.Count == open['T'];
        var fnLocals = new HashSet<string>(StringComparer.Ordinal);   // declared in the current function
        var stmtDeclared = new HashSet<string>(StringComparer.Ordinal); // declared since the last file-scope stmt
        var stmtStart = 0;
        var pendingBody = false;   // the next '{' at file scope opens a function body
        var pendingQuiet = false;  // ... and that body is a header static/inline definition

        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.InDefine) { if (t.Ident) result.Uses.Add((t.Text, t.Line)); continue; }
            // NB: macro bodies count even in headers — a macro is expanded at its (counted) use site.

            if (!t.Ident)
            {
                switch (t.Text)
                {
                    case "{":
                    {
                        char kind;
                        if (AtFileScope() && pendingBody) kind = pendingQuiet ? 'Q' : 'F';
                        else if (AtFileScope() && IsTransparentOpen(toks, i)) kind = 'T';
                        else if (header && IsClassOpen(toks, i)) kind = 'C';
                        else kind = 'O';
                        if (kind is 'F' or 'Q') { fnLocals.Clear(); fnLocals.UnionWith(stmtDeclared); }
                        braces.Push(kind); open[kind]++;
                        pendingBody = false;
                        if (kind == 'T') { stmtDeclared.Clear(); stmtStart = i + 1; }
                        break;
                    }
                    case "}":
                        if (braces.Count > 0)
                        {
                            var k = braces.Pop(); open[k]--;
                            if (k is 'F' or 'Q') fnLocals.Clear();
                        }
                        if (AtFileScope()) { stmtDeclared.Clear(); stmtStart = i + 1; pendingBody = false; }
                        break;
                    case ";":
                        if (AtFileScope()) { stmtDeclared.Clear(); stmtStart = i + 1; pendingBody = false; }
                        break;
                }
                continue;
            }

            var name = t.Text;
            var prev = i > 0 && !toks[i - 1].InDefine ? toks[i - 1] : default;
            var next = i + 1 < toks.Count && !toks[i + 1].InDefine ? toks[i + 1] : default;
            var inFunction = open['F'] + open['Q'] > 0;
            var quiet = open['Q'] + open['C'] > 0;

            // Member access: obj.name / p->name are fields/methods, not free names.
            if (prev.Text is "." or "->") continue;

            // `int (name)(params) {`: a parenthesised name (it dodges a function-like macro of the same name).
            if (AtFileScope() && prev.Text == "(" && next.Text == ")" && i + 2 < toks.Count && toks[i + 2].Text == "("
                && !NotNames.Contains(name) && MatchParen(toks, i + 2) is var pclose && pclose > 0 && OpensBody(toks, pclose + 1))
            {
                result.Definitions.Add(new Definition(name, t.Line, StaticBefore(toks, stmtStart, i), false));
                stmtDeclared.Add(name);
                pendingBody = true;
                pendingQuiet = false;   // never inline here: compiled even when static (review B7)
                i = pclose;
                continue;
            }
            // `int WRAP(name)(params) {`: the name wrapped in a macro (`#define WRAP(n) n`). A real parameter list is
            // never a lone identifier followed by another parameter list, so the definition is of the inner name.
            if (AtFileScope() && next.Text == "(" && i + 4 < toks.Count && toks[i + 2].Ident && !toks[i + 2].InDefine
                && toks[i + 3].Text == ")" && toks[i + 4].Text == "(" && !NotNames.Contains(name)
                && MatchParen(toks, i + 4) is var wclose && wclose > 0 && OpensBody(toks, wclose + 1))
            {
                var inner = toks[i + 2].Text;
                var st = StaticBefore(toks, stmtStart, i);
                result.Definitions.Add(new Definition(inner, t.Line, st, false));
                stmtDeclared.Add(inner);
                pendingBody = true;
                pendingQuiet = false;
                i = wclose;
                continue;
            }

            // File-scope function definition: name ( ... ) [attrs/qualifiers/init-list] {
            if (AtFileScope() && next.Text == "(" && !NotNames.Contains(name))
            {
                var close = MatchParen(toks, i + 1);
                if (close > 0 && OpensBody(toks, close + 1))
                {
                    bool isStatic = false, isInline = false, bare = true;
                    for (var k = stmtStart; k < i; k++)
                    {
                        if (!toks[k].InDefine) bare = false;
                        var w = toks[k].Text;
                        if (w == "static") isStatic = true;
                        else if (toks[k].Ident && (w.Contains("inline", StringComparison.OrdinalIgnoreCase)
                                 || w is "template" or "constexpr" or "consteval")) isInline = true;
                    }
                    result.Definitions.Add(new Definition(name, t.Line, isStatic, isInline, bare));
                    stmtDeclared.Add(name);
                    pendingBody = true;
                    pendingQuiet = header && isInline;   // a static non-inline one is compiled even unused at -O0 (review B7)
                    continue;
                }
            }

            if (NotNames.Contains(name) && !ExprKeywords.Contains(name)) continue;

            // "Type name" / "Type *name" / "Type &name": a declaration of name, not a use. Inside a function a
            // declared-looking name directly followed by "(" is still treated as a call (x * f(y)).
            var declared = prev.Ident && !ExprKeywords.Contains(prev.Text)
                || (prev.Text is "*" or "&" or "&&" && i >= 2 && toks[i - 2].Ident && !toks[i - 2].InDefine
                    && !ExprKeywords.Contains(toks[i - 2].Text) && next.Text is "=" or ";" or "," or ")" or "[" or "(");
            if (declared && !(inFunction && next.Text == "("))
            {
                if (inFunction) fnLocals.Add(name); else stmtDeclared.Add(name);
                continue;
            }

            if (quiet) continue;
            if (inFunction && fnLocals.Contains(name)) continue;
            if (!inFunction && AtFileScope() && stmtDeclared.Contains(name)) continue;
            if (ExprKeywords.Contains(name)) continue;
            result.Uses.Add((name, t.Line));
            if (next.Text == "(") result.CallUses.Add((name, t.Line));   // `(name)(x)` has ")" next: never a macro call
        }
        DefinerDefinitions(text, result, definerMacros);
        if (!header) DataDefinitions(toks, result);
        foreach (var (cname, sym, line) in result.AsmLabels)
            if (result.Definitions.Any(d => d.Name == cname && !d.Bare && d.Line != line)) result.Definitions.Add(new Definition(sym, line, false, false));
            else result.Uses.Add((sym, line));
        // A by-name lookup (dlsym(h, "name")): the string is a use. The link doesn't need it, the run does.
        if (text.Contains("\"", StringComparison.Ordinal))
        {
            int[]? starts = null;
            foreach (var (name, index) in CodeCarver.Core.Preprocess.DynamicLookup.Names(text))
                result.Uses.Add((name, LineOf(starts ??= LineStarts(text), index)));
        }
        // Linked with --wrap=X, `__real_X` is the original X.
        for (var u = result.Uses.Count - 1; u >= 0; u--)
            if (result.Uses[u].Name is { Length: > 7 } un && un.StartsWith("__real_", StringComparison.Ordinal))
                result.Uses.Add((un[7..], result.Uses[u].Line));
        return result;
    }

    /// <summary>Names a definer macro defines at its file-scope uses (<c>BEGIN_FN(name)</c>, <c>DEFINE_GETTER(speed)</c>,
    /// <c>FW_DECLARE(uart, ...)</c> defining <c>uart_desc</c>): the tree's definers plus the ones this file #defines.</summary>
    static void DefinerDefinitions(string text, ScanResult result, CodeCarver.Core.Preprocess.DefinerMacros.Set? tree)
    {
        // The file's own definers win over the tree's of the same name. Each set is scanned on its own and the uses taken
        // in text order: merging them rebuilt the tree's whole scan for every file that #defines a definer.
        var own = result.FunctionMacros.Count > 0 ? CodeCarver.Core.Preprocess.DefinerMacros.MakeSet(CodeCarver.Core.Preprocess.DefinerMacros.FromText(text)) : null;
        if (own is null && tree is null) return;
        IEnumerable<CodeCarver.Core.Preprocess.DefinerMacros.Use> uses = tree is null ? [] : CodeCarver.Core.Preprocess.DefinerMacros.Uses(text, tree.Use, tree.Definers);
        if (own is not null)
            uses = uses.Where(u => !own.Definers.ContainsKey(u.Macro))
                .Concat(CodeCarver.Core.Preprocess.DefinerMacros.Uses(text, own.Use, own.Definers)).OrderBy(u => u.At);
        var have = result.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var use in uses)
            foreach (var (name, _) in use.Defines)
                if (have.Add(name)) result.Definitions.Add(new Definition(name, use.StartLine, Static: false, Inline: false));
    }

    /// <summary>
    /// File-scope variable definitions of a translation unit (<c>int counter;</c>, <c>const struct cfg board = {...};</c>,
    /// <c>int (*hook)(int) = 0;</c>, <c>int a, b[4];</c>): a dropped file holding data kept code uses fails the link as
    /// surely as a dropped function. Declarations that define nothing are skipped: <c>extern</c>, <c>typedef</c>,
    /// prototypes, macro invocations, a bare <c>struct tag;</c>. A <c>static</c> one is marked so.
    /// </summary>
    static void DataDefinitions(List<Tok> all, ScanResult result)
    {
        var toks = all.Where(t => !t.InDefine).ToList();
        var match = MatchBraces(toks);
        var stmt = new List<Tok>();
        // Inside a K&R head (`int f(a, len) int a; int len; {`): the parameter names, whose declarations up to the body
        // are not file-scope data. Null outside one.
        HashSet<string>? kAndR = null;
        for (var i = 0; i < toks.Count; i++)
        {
            var s = toks[i].Text;
            if (s == "{")
            {
                if (IsTransparentOpen(toks, i)) { stmt.Clear(); kAndR = null; continue; }   // namespace / extern "C": look inside
                var close = match[i];
                // An initializer (or a lambda after `=`) belongs to the declaration, and so does a type's body —
                // `struct cfg {...} board;`, `enum class mode : u8 {...};`, `class D : public Base {...};` — which stands
                // in for the type, so a base class or an underlying type is never read as a definition. Anything else
                // (a function body, also one with trailing words: `struct node *next(void) const {`) ends it. An
                // attribute in the type's head (`struct __attribute__((packed)) cfg {`, `struct ALIGN(4) cfg {`) is no
                // parameter list.
                if (TopLevelAssign(stmt)) { }
                else if (TypeKeyword(stmt) is var kw && kw >= 0 && !WithoutTypeAttributes(stmt, kw).Any(t => t.Text is "(" or ")"))
                {
                    var line = stmt[kw].Line;
                    stmt.RemoveRange(kw, stmt.Count - kw);
                    stmt.Add(new Tok(TypeBody, line, true, false));
                }
                else stmt.Clear();
                kAndR = null;
                if (close < 0)
                {
                    // Braces unbalanced by #if branches (`#if A` / `void f(void) {` / `#else` / `void f(int x) {` /
                    // `#endif` ... `}`): this '{' has no partner. Skip it and read on, so the rest of the file still counts.
                    stmt.Clear();
                    continue;
                }
                i = close;
                continue;
            }
            if (s == "}") { stmt.Clear(); continue; }
            if (s == ";")
            {
                // Each further declaration of a parameter belongs to the head; anything else ends it.
                if (kAndR is not null && DeclaresOnly(stmt, kAndR)) { stmt.Clear(); continue; }
                kAndR = KAndRHead(stmt);
                if (kAndR is null) DataStatement(stmt, result);
                stmt.Clear();
                continue;
            }
            stmt.Add(toks[i]);
        }
    }

    // Stands for a struct/union/enum/class body in a declaration: a type, never a name.
    const string TypeBody = "\u0001type";

    static readonly HashSet<string> NoDefinition = new(StringComparer.Ordinal)
        { "typedef", "extern", "using", "template", "namespace", "friend", "static_assert", "_Static_assert", "return", "operator" };
    static readonly HashSet<string> AttributeWords = new(StringComparer.Ordinal)
        { "__attribute__", "__attribute", "__declspec", "alignas", "_Alignas", "__asm__", "__asm", "asm", "__pragma", "_Pragma" };
    // Words that qualify a declaration without naming its type.
    static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
    {
        "static", "const", "volatile", "extern", "register", "inline", "__inline", "__inline__", "thread_local",
        "_Thread_local", "__thread", "constexpr", "constinit", "mutable", "restrict", "__restrict", "__restrict__",
        "__extension__", "__volatile__", "__const",
    };
    static readonly HashSet<string> BuiltinTypes = new(StringComparer.Ordinal)
    {
        "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned", "bool", "_Bool", "auto",
        "wchar_t", "char8_t", "char16_t", "char32_t", "__int128", "_Complex",
    };

    /// <summary>Index of the last struct/union/enum/class keyword of a statement, or -1.</summary>
    static int TypeKeyword(List<Tok> stmt) => stmt.FindLastIndex(t => t.Text is "struct" or "union" or "enum" or "class");

    /// <summary>An `=` outside parentheses: an initializer follows (`void f(int a = 1) {` is a function, and so is
    /// `X& X::operator=(const X&) {` — the tokenizer splits `operator+=` into its symbols).</summary>
    static bool TopLevelAssign(List<Tok> stmt)
    {
        var level = 0;
        for (var i = 0; i < stmt.Count; i++)
        {
            var t = stmt[i];
            if (t.Text is "(" or "[") level++;
            else if (t.Text is ")" or "]") level--;
            else if (t.Text == "=" && level == 0 && !OperatorName(stmt, i)) return true;
        }
        return false;
    }

    /// <summary>Is the symbol at <paramref name="at"/> part of an operator function's name (<c>operator</c>, then up to
    /// three symbols)?</summary>
    static bool OperatorName(List<Tok> stmt, int at)
    {
        for (var k = at - 1; k >= 0 && k >= at - 3; k--)
        {
            if (stmt[k].Text == "operator") return true;
            if (stmt[k].Ident || stmt[k].Text is ")" or "]") return false;
        }
        return false;
    }

    /// <summary>`name(a, b) type a;`: an old-style head — a parameter list of bare names, then a declaration of one of
    /// them. Its parameter names, or null. (`void f(void) WEAK;` and `MACRO(a, b) int x;` are not heads.)</summary>
    static HashSet<string>? KAndRHead(List<Tok> stmt)
    {
        var open = stmt.FindIndex(t => t.Text == "(");
        if (open < 1 || !stmt[open - 1].Ident) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var k = open + 1;
        for (var wantName = true; k < stmt.Count && stmt[k].Text != ")"; k++, wantName = !wantName)
        {
            var t = stmt[k];
            if (wantName ? !t.Ident || BuiltinTypes.Contains(t.Text) || Qualifiers.Contains(t.Text) : t.Text != ",") return null;
            if (wantName) names.Add(t.Text);
        }
        if (names.Count == 0 || k + 1 >= stmt.Count) return null;
        return DeclaresOnly(stmt.GetRange(k + 1, stmt.Count - k - 1), names) ? names : null;
    }

    /// <summary>Does <paramref name="decl"/> (one statement, no ';') declare only names from <paramref name="names"/>:
    /// a type, then declarators each ending in one of them (<c>int a, len</c>, <c>char *s</c>, <c>int v[4]</c>)?</summary>
    static bool DeclaresOnly(List<Tok> decl, HashSet<string> names)
    {
        if (decl.Count < 2 || !decl[0].Ident) return false;
        var level = 0;
        string? last = null;
        foreach (var t in decl.Append(new Tok(",", 0, false, false)))
        {
            if (t.Text is "(" or "[") { level++; continue; }
            if (t.Text is ")" or "]") { level--; continue; }
            if (level > 0) continue;
            if (t.Text == ",")
            {
                if (last is null || !names.Contains(last)) return false;
                last = null;
            }
            else if (t.Ident) last = t.Text;
            else if (t.Text is not ("*" or "&")) return false;
        }
        return true;
    }

    /// <summary>The tokens of a type's head from its keyword on, without the attributes there:
    /// <c>struct __attribute__((packed)) cfg</c>, <c>struct ALIGN(4) cfg</c> (an attribute-like macro right after the
    /// keyword), <c>alignas(8)</c>.</summary>
    static IEnumerable<Tok> WithoutTypeAttributes(List<Tok> stmt, int kw)
    {
        for (var k = kw; k < stmt.Count; k++)
        {
            var t = stmt[k];
            if (k + 1 < stmt.Count && stmt[k + 1].Text == "("
                && (AttributeWords.Contains(t.Text) || (k == kw + 1 && t.Ident && AttributeLike(t.Text))))
            {
                var depth = 0;
                for (k++; k < stmt.Count; k++)
                    if (stmt[k].Text == "(") depth++;
                    else if (stmt[k].Text == ")" && --depth == 0) break;
                continue;
            }
            yield return t;
        }
    }

    /// <summary>Spelled like an attribute macro (<c>PROGMEM</c>, <c>SECTION</c>, <c>__ALIGNED</c>): all capitals, or a
    /// reserved <c>__</c> name.</summary>
    static bool AttributeLike(string s)
    {
        if (s.StartsWith("__", StringComparison.Ordinal)) return true;
        var letter = false;
        foreach (var c in s)
        {
            if (char.IsLower(c)) return false;
            if (char.IsUpper(c)) letter = true;
        }
        return letter && s.Length >= 2;
    }

    /// <summary>Can <paramref name="toks"/>[<paramref name="at"/>] be a declarator's name: an identifier, not a type or
    /// qualifier word, not a struct tag, with a type before it (<c>const int v</c>, <c>T *v</c>; not <c>static T</c>).</summary>
    static bool DeclaratorName(List<Tok> toks, int at)
    {
        if (at < 1 || !toks[at].Ident || toks[at].Text == TypeBody) return false;
        var w = toks[at].Text;
        if (BuiltinTypes.Contains(w) || Qualifiers.Contains(w) || NotNames.Contains(w)) return false;
        if (toks[at - 1].Text is "struct" or "union" or "enum" or "class") return false;
        for (var k = at - 1; k >= 0; k--)
        {
            var t = toks[k];
            if (t.Text is "*" or "&" or "&&" or ">" || t.Text == TypeBody || BuiltinTypes.Contains(t.Text)) return true;
            if (t.Ident && !Qualifiers.Contains(t.Text) && !AttributeWords.Contains(t.Text)) return true;
            if (t.Text == ",") return false;
        }
        return false;
    }

    static void DataStatement(List<Tok> stmt, ScanResult result)
    {
        if (stmt.Count < 2 || stmt.Any(t => NoDefinition.Contains(t.Text))) return;
        var isStatic = stmt.Any(t => t.Text == "static");
        // Attributes and asm labels carry parentheses that aren't a parameter list; so does an attribute macro after a
        // declarator (`int v SECTION(".noinit");`, `int buf[8] __ALIGNED(4);`). Not an old prototype wrapper, whose
        // argument is a parenthesised parameter list (`static int f __P((int));`).
        var f = new List<Tok>();
        for (var k = 0; k < stmt.Count; k++)
        {
            var attrMacro = k + 2 < stmt.Count && stmt[k + 1].Text == "(" && stmt[k + 2].Text != "(" && stmt[k].Ident
                && AttributeLike(stmt[k].Text) && k >= 1 && (stmt[k - 1].Text is "]" || DeclaratorName(stmt, k - 1))
                && !ParameterList(stmt, k + 1);   // `int EXPORT INIT_ALL(void);` is a prototype of INIT_ALL
            if ((AttributeWords.Contains(stmt[k].Text) && k + 1 < stmt.Count && stmt[k + 1].Text == "(") || attrMacro)
            {
                var depth = 0;
                for (k++; k < stmt.Count; k++)
                    if (stmt[k].Text == "(") depth++;
                    else if (stmt[k].Text == ")" && --depth == 0) break;
                continue;
            }
            f.Add(stmt[k]);
        }
        // Declarators: split at top-level commas (a template argument list before the `=` is one level down).
        var parts = new List<List<Tok>> { new() };
        int level = 0, angle = 0;
        var seenEq = false;
        foreach (var t in f)
        {
            if (t.Text is "(" or "[") level++;
            else if (t.Text is ")" or "]") level--;
            else if (t.Text == "=" && level == 0) seenEq = true;
            else if (!seenEq && level == 0 && t.Text == "<") angle++;            // `int a[A<B ? 1 : 2], b;`: no template
            else if (!seenEq && level == 0 && t.Text == ">" && angle > 0) angle--;
            if (t.Text == "," && level == 0 && angle == 0) { parts.Add(new()); seenEq = false; continue; }
            parts[^1].Add(t);
        }
        for (var p = 0; p < parts.Count; p++)
        {
            var d = parts[p];
            var eq = d.FindIndex(t => t.Text == "=");
            var left = eq < 0 ? d : d.GetRange(0, eq);
            Tok? name = null;
            var paren = left.FindIndex(t => t.Text == "(");
            if (paren >= 0)
            {
                // `(*hook)(int)`: a pointer variable. Any other parameter list is a prototype or a macro invocation —
                // so is `(*sig(int))(int)`, a function returning a function pointer.
                if (paren + 1 < left.Count && left[paren + 1].Text is "*" or "&")
                {
                    var at = left.FindIndex(paren + 1, t => t.Ident && !NotNames.Contains(t.Text));
                    if (at >= 0 && !(at + 1 < left.Count && left[at + 1].Text == "(")) name = left[at];
                }
                if (name is null) { if (p == 0) return; continue; }
            }
            else
            {
                var bracket = left.FindIndex(t => t.Text == "[");
                var head = bracket < 0 ? left : left.GetRange(0, bracket);
                var idx = head.FindLastIndex(t => t.Ident);
                if (idx < 0) continue;
                // The first declarator's name is the word after its type (`U32 COUNTER`, `std::uint32_t REG_C`,
                // `struct REGS BANK0`), and attribute-like words after that are attributes (`const int v PROGMEM = 1;`).
                if (p == 0 && NameAfterType(head) is { } named && (named == idx || AllAttributeLike(head, named + 1, idx)))
                    idx = named;
                else
                {
                    // A trailing attribute macro (`const int v PROGMEM = 1;`) is not the name: the declarator before it is.
                    var at = idx;
                    while (at >= 1 && AttributeLike(head[at].Text) && head[at - 1].Ident && AttributeLike(head[at - 1].Text)) at--;
                    if (at != idx || AttributeLike(head[idx].Text))
                    {
                        if (DeclaratorName(head, at - 1)) idx = at - 1;
                        else if (at != idx) { if (p == 0) return; continue; }   // unsure: record nothing
                    }
                }
                // The first declarator needs a type before its name; `struct tag;` declares a tag, not a variable.
                if (p == 0 && (idx == 0 || head[idx - 1].Text is "struct" or "union" or "enum" or "class")) return;
                name = head[idx];
            }
            var n = name.Value;
            if (NotNames.Contains(n.Text) || n.Text == TypeBody) continue;
            result.Definitions.Add(new Definition(n.Text, n.Line, isStatic, false, Data: true));
        }
    }

    // Macros commonly spelled in place of a storage class or qualifier (CMSIS `__IO`, `STATIC`, `CONST`).
    static readonly HashSet<string> QualifierMacros = new(StringComparer.Ordinal)
        { "STATIC", "LOCAL", "PRIVATE", "CONST", "VOLATILE", "__IO", "__I", "__O", "__IM", "__OM", "__IOM" };

    /// <summary>Index of the identifier right after a declaration's type — qualifiers, then one type (builtin words,
    /// a qualified or template name, a struct/union/enum/class and its tag), then pointers — or null when there is
    /// none (<c>struct tag</c>) or it is a type or qualifier word.</summary>
    static int? NameAfterType(List<Tok> head)
    {
        var t = 0;
        while (t < head.Count && (Qualifiers.Contains(head[t].Text) || QualifierMacros.Contains(head[t].Text)
               // `__packed uint32_t REG`: a reserved word before a type and a name qualifies.
               || (head[t].Text.StartsWith("__", StringComparison.Ordinal) && !BuiltinTypes.Contains(head[t].Text)
                   && t + 2 < head.Count && head[t + 1].Ident && (head[t + 2].Ident || head[t + 2].Text is "*" or "&"))))
            t++;
        if (t >= head.Count) return null;
        var end = t;
        var w = head[t].Text;
        if (w is "struct" or "union" or "enum" or "class")
            end = t + 1 < head.Count && head[t + 1].Ident ? t + 1 : t;
        else if (BuiltinTypes.Contains(w))
            while (end + 1 < head.Count && BuiltinTypes.Contains(head[end + 1].Text)) end++;
        else if (head[t].Ident)
        {
            // `std::uint32_t`, `ns::Type`, `std::array<int, 4>`.
            while (end + 2 < head.Count && head[end + 1].Text == "::" && head[end + 2].Ident) end += 2;
            if (end + 1 < head.Count && head[end + 1].Text == "<")
            {
                var depth = 0;
                for (end++; end < head.Count; end++)
                    if (head[end].Text == "<") depth++;
                    else if (head[end].Text == ">" && --depth == 0) break;
                if (end >= head.Count) return null;
            }
        }
        else return null;
        var c = end + 1;
        while (c < head.Count && (head[c].Text is "*" or "&" or "&&" || Qualifiers.Contains(head[c].Text))) c++;
        if (c >= head.Count || !head[c].Ident) return null;
        var n = head[c].Text;
        return BuiltinTypes.Contains(n) || Qualifiers.Contains(n) || NotNames.Contains(n) || n == TypeBody ? null : c;
    }

    static bool AllAttributeLike(List<Tok> head, int from, int to)
    {
        for (var k = from; k <= to; k++) if (!head[k].Ident || !AttributeLike(head[k].Text)) return false;
        return true;
    }

    /// <summary>Is the parenthesis at <paramref name="open"/> a parameter list — empty, <c>(void)</c>, starting with a
    /// type or qualifier word, or holding a type and a name (<c>(uint32_t x)</c>) — rather than macro arguments
    /// (<c>(".noinit")</c>, <c>(4)</c>)?</summary>
    static bool ParameterList(List<Tok> toks, int open)
    {
        if (open + 1 >= toks.Count) return false;
        var first = toks[open + 1];
        if (first.Text == ")") return true;
        if (BuiltinTypes.Contains(first.Text) || Qualifiers.Contains(first.Text) || first.Text is "struct" or "union" or "enum") return true;
        string At(int k) => k < toks.Count ? toks[k].Text : "";
        for (var k = open + 1; k + 1 < toks.Count && toks[k].Text != ")"; k++)
        {
            if (!toks[k].Ident) continue;
            if (toks[k + 1].Ident) return true;                                                   // `uint32_t x`
            if (At(k + 1) == "*" && (At(k + 2) is ")" or "," or "*"                                // `char *`, `T **`
                                     || (k + 2 < toks.Count && toks[k + 2].Ident && At(k + 3) is ")" or ","))) return true;   // `T *p`
        }
        return false;
    }

    /// <summary>Each '{' paired with its '}' by a stack (-1 when it has none): an extra '{' from an #if branch stays
    /// unpaired, and the braces inside it still pair up.</summary>
    static int[] MatchBraces(List<Tok> toks)
    {
        var match = new int[toks.Count];
        var open = new Stack<int>();
        for (var k = 0; k < toks.Count; k++)
        {
            match[k] = -1;
            if (toks[k].Text == "{") open.Push(k);
            else if (toks[k].Text == "}" && open.Count > 0) match[open.Pop()] = k;
        }
        return match;
    }

    static bool StaticBefore(List<Tok> toks, int from, int to)
    {
        for (var k = from; k < to; k++) if (toks[k].Text == "static") return true;
        return false;
    }

    static bool IsTransparentOpen(List<Tok> toks, int brace)
    {
        // namespace {   namespace a::b {   extern "" {
        var k = brace - 1;
        while (k >= 0 && (toks[k].Ident || toks[k].Text == "::") && toks[k].Text != "namespace") k--;
        if (k >= 0 && toks[k].Text == "namespace") return true;
        return brace >= 2 && toks[brace - 1].Text == "\"\"" && toks[brace - 2].Text == "extern";
    }

    static bool IsClassOpen(List<Tok> toks, int brace)
    {
        // class X {   struct X : Base {   union {   — scan back to the statement start for the keyword.
        for (var k = brace - 1; k >= 0 && k >= brace - 64; k--)
        {
            var s = toks[k].Text;
            if (s is ";" or "{" or "}" or ")" or "=") return false;
            if (s is "class" or "struct" or "union") return true;
        }
        return false;
    }

    static int MatchParen(List<Tok> toks, int open)
    {
        var depth = 0;
        for (var k = open; k < toks.Count; k++)
        {
            if (toks[k].InDefine) return -1;
            if (toks[k].Text == "(") depth++;
            else if (toks[k].Text == ")" && --depth == 0) return k;
            else if (toks[k].Text is "{" or "}" or ";") return -1;
        }
        return -1;
    }

    // After a parameter list: does a function body follow (before any ';' or '=' at paren depth 0)?
    static bool OpensBody(List<Tok> toks, int from)
    {
        var depth = 0;
        for (var k = from; k < toks.Count && k < from + 512; k++)
        {
            if (toks[k].InDefine) return false;
            var s = toks[k].Text;
            if (s == "(") depth++;
            else if (s == ")") depth--;
            else if (depth == 0 && s == "{") return true;
            else if (depth == 0 && s is ";" or "=" or "}") return false;
        }
        return false;
    }

    /// <summary>C/C++ tokens with comments, string/char literals (as <c>""</c>) and preprocessor lines handled.
    /// #define bodies are kept (as InDefine tokens, parameters and ## / # operands removed); every other
    /// directive line is skipped entirely.</summary>
    /// <param name="pool">Shares one string per distinct name across the call (and its #define bodies): a token's text
    /// is otherwise a new string each time.</param>
    internal static List<Tok> Tokenize(string s, bool directives = true, CodeCarver.Core.Util.NamePool? pool = null)
        => Tokenize(s, directives, pool ?? new CodeCarver.Core.Util.NamePool(), 0, null);

    /// <summary>As <see cref="Tokenize(string, bool, CodeCarver.Core.Util.NamePool?)"/>, from <paramref name="from"/>,
    /// adding to <paramref name="into"/> when given.</summary>
    static List<Tok> Tokenize(string s, bool directives, CodeCarver.Core.Util.NamePool pool, int from, List<Tok>? into)
    {
        // Sized for a small file and grown by doubling: a token per four characters held a register header's list at
        // twice the size of its text, where its #define lines are next to no tokens (227 MB of them made 10).
        var toks = into ?? new List<Tok>(Math.Min(s.Length / 8, 1 << 16) + 16);
        // A register header is mostly #define lines: one builder, body token list and parameter set serve them all.
        System.Text.StringBuilder? sb = null;
        DefineScratch? scratch = null;
        var line = 1;
        var i = from;
        var atLineStart = true;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n') { line++; i++; atLineStart = true; continue; }
            if (c is ' ' or '\t' or '\r' or '\f' or '\v') { i++; continue; }
            // A backslash-newline outside a directive is a line splice: whitespace between tokens (SourceText.SplitNames
            // already joined any name it split). The line still counts; a directive can't start on the spliced line.
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '\n' || (s[i + 1] == '\r' && i + 2 < s.Length && s[i + 2] == '\n')))
            { i += s[i + 1] == '\r' ? 3 : 2; line++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                i += 2;
                continue;
            }
            if ((c == '#' || (c == '%' && i + 1 < s.Length && s[i + 1] == ':')) && atLineStart && directives)
            {
                // Collect the logical directive line (with continuations), stripped of comments. `%:` is the # digraph.
                var start = line;
                (sb ??= new System.Text.StringBuilder()).Clear();
                i += c == '%' ? 2 : 1;
                while (i < s.Length && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '\n' || (s[i + 1] == '\r' && i + 2 < s.Length && s[i + 2] == '\n')))
                    { i += s[i + 1] == '\r' ? 3 : 2; line++; sb.Append(' '); continue; }
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; break; }
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
                    {
                        i += 2;
                        while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                        i += 2; sb.Append(' ');
                        continue;
                    }
                    sb.Append(s[i]); i++;
                }
                if (IsDefine(sb)) DefineBody(sb.ToString(), start, toks, pool, scratch ??= new DefineScratch());
                continue;
            }
            atLineStart = false;
            if (c is '"' or '\'')
            {
                // Raw strings R"x(...)x" are rare in firmware; a plain scan to the closing quote is enough.
                var q = c; i++;
                while (i < s.Length && s[i] != q && s[i] != '\n') { if (s[i] == '\\') i++; i++; }
                i++;
                toks.Add(new Tok(q == '"' ? "\"\"" : "''", line, false, false));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$')) i++;
                // A string-literal prefix (L"", u8"", R"") is part of the literal, not a name.
                if (i < s.Length && s[i] is '"' or '\'' && i - st <= 2) continue;
                toks.Add(new Tok(pool.Get(s, st, i), line, true, false));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '\'')) i++;
                toks.Add(new Tok("0", line, false, false));
                continue;
            }
            if (i + 1 < s.Length)
            {
                var two = TwoChar(c, s[i + 1]);
                // Digraphs: <% %> <: :> are { } [ ] (but C++ `<::` before anything but : or > is < ::).
                var di = two switch { "<%" => "{", "%>" => "}", "<:" => "[", ":>" => "]", _ => null };
                if (di is not null && !(two == "<:" && i + 2 < s.Length && s[i + 2] == ':' && (i + 3 >= s.Length || s[i + 3] is not (':' or '>'))))
                { toks.Add(new Tok(di, line, false, false)); i += 2; continue; }
                if (two is "->" or "::" or "&&" or "||" or "==" or "!=" or "<=" or ">=" or "##")
                { toks.Add(new Tok(two, line, false, false)); i += 2; continue; }
            }
            toks.Add(new Tok(CodeCarver.Core.Util.NamePool.Char(c), line, false, false));
            i++;
        }
        return toks;
    }

    /// <summary>The two-character token at a position, or null: the same string instances every time, where a
    /// substring at every punctuation character was a new string each (most of them not tokens at all).</summary>
    static string? TwoChar(char a, char b) => (a, b) switch
    {
        ('<', '%') => "<%", ('%', '>') => "%>", ('<', ':') => "<:", (':', '>') => ":>",
        ('-', '>') => "->", (':', ':') => "::", ('&', '&') => "&&", ('|', '|') => "||", ('=', '=') => "==",
        ('!', '=') => "!=", ('<', '=') => "<=", ('>', '=') => ">=", ('#', '#') => "##",
        _ => null,
    };

    sealed class DefineScratch
    {
        public readonly List<Tok> Inner = new();
        public readonly HashSet<string> Parms = new(StringComparer.Ordinal);
    }

    /// <summary>Whether a directive line (after its '#') is a #define: the only kind <see cref="DefineBody"/> reads.</summary>
    static bool IsDefine(System.Text.StringBuilder sb)
    {
        var k = 0;
        while (k < sb.Length && char.IsWhiteSpace(sb[k])) k++;
        const string define = "define";
        for (var j = 0; j < define.Length; j++, k++)
            if (k >= sb.Length || sb[k] != define[j]) return false;
        return k >= sb.Length || !(char.IsLetter(sb[k]) || sb[k] == '_');
    }

    // #define NAME(params) body  -> body identifiers as InDefine uses; other directives contribute nothing.
    static void DefineBody(string d, int line, List<Tok> toks, CodeCarver.Core.Util.NamePool pool, DefineScratch scratch)
    {
        var k = 0;
        while (k < d.Length && char.IsWhiteSpace(d[k])) k++;
        var w = k;
        while (k < d.Length && (char.IsLetter(d[k]) || d[k] == '_')) k++;
        if (!d.AsSpan(w, k - w).SequenceEqual("define")) return;
        while (k < d.Length && char.IsWhiteSpace(d[k])) k++;
        while (k < d.Length && (char.IsLetterOrDigit(d[k]) || d[k] == '_')) k++;
        var parms = scratch.Parms;
        parms.Clear();
        if (k < d.Length && d[k] == '(')
        {
            var close = d.IndexOf(')', k);
            if (close < 0) return;
            foreach (var p in d[(k + 1)..close].Split(',')) parms.Add(p.Trim().TrimEnd('.').Trim());
            k = close + 1;
        }
        var inner = scratch.Inner;
        inner.Clear();
        Tokenize(d, directives: false, pool, k, inner);
        for (var j = 0; j < inner.Count; j++)
        {
            var t = inner[j];
            if (!t.Ident || parms.Contains(t.Text) || NotNames.Contains(t.Text)) continue;
            var before = j > 0 ? inner[j - 1].Text : "";
            var after = j + 1 < inner.Count ? inner[j + 1].Text : "";
            if (before is "." or "->" or "#" or "##" || after == "##") continue;
            toks.Add(new Tok(t.Text, line, true, true));
        }
    }
}
