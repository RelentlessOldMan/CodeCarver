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
                                                  Action<string, long>? partDone = null)
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
        var others = Directory.EnumerateFiles(outDir, "*", walk)
            .Where(f => !CompiledExts.Contains(Path.GetExtension(f)) && !bigSet.Contains(Path.GetRelativePath(outDir, f).Replace('\\', '/')))
            .ToList();
        var otherNames = 0;
        foreach (var (_, names) in ReadAhead.Ordered(others, OtherNames, ReadAhead.Window))
            if (names is not null)
                foreach (var k in names)
                    if (needed.Add(k)) { frontier.Add(k); otherNames++; }
        partDone?.Invoke("used.otherFiles", others.Count);
        partDone?.Invoke("used.otherNames", otherNames);

        var passes = 0;
        var pastes = 0L;
        while (frontier.Count > 0 && passes < UsedClosurePasses)
        {
            passes++;
            var reading = frontier;
            Activity.Doing($"reading the big headers for what other files' names need, pass {passes}", () => $"{needed.Count:N0} names, {reading.Count:N0} to follow");
            var next = new HashSet<ulong>();
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
            RewriteDroppingUnneeded(path, tmp, Wanted, ref kept, ref dropped, _ => { });
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            File.Move(tmp, path);
            return (Before: before, After: new FileInfo(path).Length, Kept: kept, Dropped: dropped);
        });
        partDone?.Invoke("used.names", used.Count);
        partDone?.Invoke("keptByUsed", keptByUsed);
        partDone?.Invoke("keptByOther", keptByOther);
        partDone?.Invoke("used.ms", clock.ElapsedMilliseconds);
        Activity.Doing(null);
        return new HeaderCarveResult(rewritten.Sum(r => r.Before), rewritten.Sum(r => r.After), rewritten.Sum(r => r.Kept), rewritten.Sum(r => r.Dropped))
        {
            Passes = passes,
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

    /// <summary>The names in the bodies of one header's defines of the names in <paramref name="follow"/> that are not
    /// <paramref name="needed"/> yet, and how many of those bodies paste.</summary>
    private static (HashSet<ulong> Names, int Pastes) Follow(string path, HashSet<ulong> follow, HashSet<ulong> needed)
    {
        var names = new HashSet<ulong>();
        var pastes = 0;
        var def = new PpDefine();
        foreach (var (name, text, kind) in EnumerateLogicalLines(path))
        {
            if (kind != LineKind.Define || name is null || !follow.Contains(NameKey.Short(name))) continue;
            if (!def.Parse(text)) { AddNewIdentifiers(names, needed, text, except: name); continue; }
            AddBodyNames(names, needed, def);
            if (def.Body.Any(t => t.Kind == PpTok.HashHash)) pastes++;
        }
        return (names, pastes);
    }
}
