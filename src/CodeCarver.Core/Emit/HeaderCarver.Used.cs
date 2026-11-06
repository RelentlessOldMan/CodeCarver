using CodeCarver.Core.Util;

namespace CodeCarver.Core.Emit;

public static partial class HeaderCarver
{
    /// <summary>C and C++ sources and headers: read only through the preprocessor, so what the compiles used says what
    /// they need (docs/USED-MACROS.md). Every other text file is read for names, as the static carve reads it: a script
    /// or assembler file that names a define directly, or a debugger script that evaluates one from the debug info.</summary>
    private static readonly HashSet<string> CompiledExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm", ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tcc", ".tpp",
    };

    /// <summary>The headers among <see cref="CompiledExts"/>: a small one is kept whole, but its defines can pass a name on.</summary>
    private static readonly HashSet<string> HeaderExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tcc", ".tpp",
    };

    /// <summary>The most passes the closure over the other files' names takes (each reads every big header).</summary>
    private const int UsedClosurePasses = 16;

    /// <summary>
    /// Carve the big headers keeping a define only if its name is one the build's compiles used (<paramref name="used"/>,
    /// from <c>-E -dU</c>: the -D values and the headers outside the tree are part of those compiles), or one a kept
    /// non-C text file names, with the names those defines' bodies hold in turn. That last is a backstop for a file
    /// preprocessed by a command the build log doesn't show as one: the contract is that every preprocessor run is
    /// collected, so names pasted from there are not followed.
    /// </summary>
    public static HeaderCarveResult CarveFromUsed(string outDir, IReadOnlyCollection<string> bigHeaderRels, IReadOnlySet<ulong> used,
                                                  Action<string, long>? partDone = null, IReadOnlySet<ulong>? follow = null)
    {
        var bigSet = new HashSet<string>(bigHeaderRels, StringComparer.OrdinalIgnoreCase);
        var bigFull = bigHeaderRels.Select(r => Path.Combine(outDir, r)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Activity.Begin("files read for the header carve");

        var usedKeys = used;
        var needed = new HashSet<ulong>(usedKeys);
        // Names that reach a define some other way than a compile: their bodies' names are needed too.
        var frontier = new HashSet<ulong>();
        Activity.Doing("reading the kept non-C files for names", () => $"{needed.Count:N0} names");
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var others = new List<string>();
        var smallHeaders = new List<string>();
        foreach (var f in Directory.EnumerateFiles(outDir, "*", walk))
        {
            if (bigSet.Contains(Path.GetRelativePath(outDir, f).Replace('\\', '/'))) continue;
            var ext = Path.GetExtension(f);
            if (!CompiledExts.Contains(ext)) others.Add(f);
            else if (HeaderExts.Contains(ext)) smallHeaders.Add(f);
        }
        var otherNames = 0;
        // Names a compile may have used unseen (one past a stubbed header): kept, their bodies followed, as other files'.
        if (follow is not null) foreach (var k in follow) if (needed.Add(k)) frontier.Add(k);
        foreach (var (_, names) in ReadAhead.Ordered(others, OtherNames, ReadAhead.Window))
            if (names is not null)
                foreach (var k in names)
                    if (needed.Add(k)) { frontier.Add(k); otherNames++; }
        partDone?.Invoke("used.otherFiles", others.Count);
        partDone?.Invoke("used.otherNames", otherNames);

        // The small headers are kept whole, but a name can pass through one: a script preprocessed outside the log
        // includes memmap.h, whose define names a big header's. Their defines are read once, as name → the names its
        // body and the conditions around it hold, and followed with the big headers'.
        Dictionary<ulong, HashSet<ulong>>? small = null;
        if (frontier.Count > 0 && smallHeaders.Count > 0)
        {
            Activity.Doing("reading the small headers' defines", () => $"{smallHeaders.Count:N0} files");
            small = new Dictionary<ulong, HashSet<ulong>>();
            foreach (var (_, defs) in ReadAhead.Ordered(smallHeaders, DefineNames, ReadAhead.Window))
                foreach (var (k, names) in defs)
                    if (small.TryGetValue(k, out var had)) had.UnionWith(names); else small[k] = names;
        }
        partDone?.Invoke("used.smallHeaders", small is null ? 0 : smallHeaders.Count);

        var passes = 0;
        var pastes = 0L;
        while (frontier.Count > 0 && passes < UsedClosurePasses)
        {
            passes++;
            var reading = frontier;
            Activity.Doing($"reading the big headers for what other files' names need, pass {passes}", () => $"{needed.Count:N0} names, {reading.Count:N0} to follow");
            var next = new HashSet<ulong>();
            if (small is not null)
                foreach (var k in reading)
                    if (small.TryGetValue(k, out var names))
                        foreach (var n in names) if (needed.Add(n)) next.Add(n);
            foreach (var (names, paste) in ReadAhead.Each(bigFull, path => Follow(path, reading, needed)))
            {
                pastes += paste;
                foreach (var k in names) if (needed.Add(k)) next.Add(k);
            }
            frontier = next;
        }
        partDone?.Invoke("used.closurePasses", passes);
        partDone?.Invoke("used.closureLeft", frontier.Count);
        partDone?.Invoke("used.otherPastes", pastes);

        Activity.Doing("rewriting the big headers", () => $"{needed.Count:N0} names");
        long keptByUsed = 0, keptByOther = 0;
        bool Wanted(string name)
        {
            var k = NameKey.Short(name);
            if (usedKeys.Contains(k)) { Interlocked.Increment(ref keptByUsed); return true; }
            if (needed.Contains(k)) { Interlocked.Increment(ref keptByOther); return true; }
            return false;
        }
        var rewritten = ReadAhead.Each(bigFull, path =>
        {
            var before = new FileInfo(path).Length;
            var tmp = path + ".carve.tmp";
            int kept = 0, dropped = 0;
            var sha = RewriteDroppingUnneeded(path, tmp, Wanted, ref kept, ref dropped, _ => { });
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            File.Move(tmp, path);
            return (Before: before, After: new FileInfo(path).Length, Kept: kept, Dropped: dropped, Sha: sha);
        });
        partDone?.Invoke("used.names", used.Count);
        partDone?.Invoke("keptByUsed", keptByUsed);
        partDone?.Invoke("keptByOther", keptByOther);
        partDone?.Invoke("used.ms", clock.ElapsedMilliseconds);
        Activity.Doing(null);
        return new HeaderCarveResult(rewritten.Sum(r => r.Before), rewritten.Sum(r => r.After), rewritten.Sum(r => r.Kept), rewritten.Sum(r => r.Dropped))
        {
            Passes = passes, ContentSha = Shas(bigHeaderRels, rewritten.Select(r => r.Sha)),
        };
    }

    /// <summary>A non-C text file's names (null: a binary file).</summary>
    private static HashSet<ulong>? OtherNames(string path)
    {
        if (LooksBinary(path)) return null;
        var names = new HashSet<ulong>();
        foreach (var line in LogicalLines(path)) AddIdentifiers(names, line);
        return names;
    }

    /// <summary>Each define in a small header: its name and the names its body and the conditions around it hold.</summary>
    private static List<(ulong Key, HashSet<ulong> Names)> DefineNames(string path)
    {
        var defs = new List<(ulong, HashSet<ulong>)>();
        if (LooksBinary(path)) return defs;
        var none = new HashSet<ulong>();
        var conds = new List<List<string>>();
        foreach (var (name, text, kind) in EnumerateLogicalLines(path))
        {
            if (TrackConditions(conds, text, kind) || kind != LineKind.Define || name is null) continue;
            var names = new HashSet<ulong>();
            foreach (var frame in conds) foreach (var c in frame) AddNewIdentifiers(names, none, c);
            AddNewIdentifiers(names, none, text, except: name);
            defs.Add((NameKey.Short(name), names));
        }
        return defs;
    }

    /// <summary>Keep the stack of open conditionals (each with its #elif lines) up to date; true if the line was one.</summary>
    private static bool TrackConditions(List<List<string>> conds, string text, LineKind kind)
    {
        if (kind == LineKind.Conditional)
        {
            if (DirectiveWord(text) == "elif" && conds.Count > 0) conds[^1].Add(text); else conds.Add(new List<string> { text });
            return true;
        }
        if (kind == LineKind.Other && DirectiveWord(text) == "endif")
        {
            if (conds.Count > 0) conds.RemoveAt(conds.Count - 1);
            return true;
        }
        return false;
    }

    /// <summary>The word after a directive's <c>#</c> (<c>endif</c> of <c>#  endif</c>), or null.</summary>
    private static string? DirectiveWord(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        if (i >= text.Length || text[i] != '#') return null;
        i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        var s = i;
        while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
        return i > s ? text[s..i] : null;
    }

    /// <summary>The names in the bodies of one header's defines of the names in <paramref name="follow"/> that are not
    /// <paramref name="needed"/> yet, and how many of those bodies paste. The conditions around such a define count too:
    /// the compiles may never have entered its block (a linker-only #ifdef), so what decides whether it is defined, or
    /// which of two it is, was never recorded as used.</summary>
    private static (HashSet<ulong> Names, int Pastes) Follow(string path, HashSet<ulong> follow, HashSet<ulong> needed)
    {
        var names = new HashSet<ulong>();
        var pastes = 0;
        var def = new PpDefine();
        var conds = new List<List<string>>();
        foreach (var (name, text, kind) in EnumerateLogicalLines(path))
        {
            if (TrackConditions(conds, text, kind)) continue;
            if (kind != LineKind.Define || name is null || !follow.Contains(NameKey.Short(name))) continue;
            foreach (var frame in conds) foreach (var c in frame) AddNewIdentifiers(names, needed, c);
            if (!def.Parse(text)) { AddNewIdentifiers(names, needed, text, except: name); continue; }
            AddBodyNames(names, needed, def);
            if (def.Body.Any(t => t.Kind == PpTok.HashHash)) pastes++;
        }
        return (names, pastes);
    }
}
