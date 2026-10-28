namespace CodeCarver.Core.Frontend;

/// <summary>How a source file's code gets into the image, as the build log's link lines say.</summary>
public enum LinkStatus
{
    /// <summary>The log doesn't say (no link line, an object no parsed link or archive names, an unreadable response
    /// file): treated as linked.</summary>
    Unknown,
    /// <summary>Its object is on a link line (or in an archive linked whole, or the source itself is): in the image.</summary>
    Linked,
    /// <summary>Its object is only in archives a link searches: in the image only if something there refers to it.</summary>
    ArchiveOnly,
}

/// <summary>
/// The objects each link in a build log takes, read from its link and archive commands
/// (<see cref="BuildLogScraper.ParseToolSteps"/>) and the compiles that wrote the objects. A linker takes an archive
/// member only when the link so far refers to a symbol it defines; a member's sections the linker script keeps
/// (a driver registering itself in an init list) and its <c>used</c>/constructor code come with it, and not
/// otherwise. So a file that is <see cref="LinkStatus.ArchiveOnly"/> has those kept only once the carve reaches it.
/// Anything the log doesn't settle is <see cref="LinkStatus.Unknown"/>, which keeps them as before.
/// </summary>
public sealed class LinkModel
{
    readonly Dictionary<string, LinkStatus> _status = new(StringComparer.Ordinal);

    /// <summary>Links read from the log.</summary>
    public int Links { get; }
    /// <summary>Archives the links search (named, or found through <c>-l</c>/<c>-L</c>) that an archive command built.</summary>
    public int ArchivesSearched { get; }
    /// <summary>Objects and archives the links take that no command in the log made, where the build's own could be: an
    /// object inside the carve root or beside the objects the log's compiles write, an archive beside them. An
    /// incremental build's log skips the compiles that were up to date, and their objects show up here.</summary>
    public int InputsNotFromLog { get; private set; } = -1;
    /// <summary>The log's links read whole, every input they take that the build could have made made by a command in
    /// the log: no compile the log lacks is in an image, so a source file no command compiles isn't built.</summary>
    public bool CoversTheImages => Links > 0 && InputsNotFromLog == 0;
    /// <summary>Of <see cref="InputsNotFromLog"/>, the archives: what's in them the log doesn't say.</summary>
    public int ArchivesNotFromLog { get; private set; }

    /// <summary>Could a link take an uncompiled file's object from an archive: no link read whole, or one the log didn't build?</summary>
    public bool ArchiveMayHoldUncompiled => InputsNotFromLog < 0 || ArchivesNotFromLog > 0;

    readonly HashSet<string> _objectNamesNotFromLog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Could a link in the log take <paramref name="rel"/>'s object whole though no command in it compiles the file?
    /// Only if an object the links take that the log didn't write is named after it (<c>a.o</c>, <c>a.obj</c>, <c>a.c.o</c>),
    /// or no link was read whole. Otherwise only an archive the log didn't build could hold it
    /// (<see cref="ArchiveMayHoldUncompiled"/>), and a linker takes a member only when the image refers to it.</summary>
    public bool MayLinkUncompiled(string rel)
    {
        if (InputsNotFromLog < 0) return true;
        var name = Path.GetFileName(rel);
        var stem = Path.GetFileNameWithoutExtension(rel);
        return _objectNamesNotFromLog.Contains(stem + ".o") || _objectNamesNotFromLog.Contains(stem + ".obj")
            || _objectNamesNotFromLog.Contains(name + ".o") || _objectNamesNotFromLog.Contains(name + ".obj");
    }

    static readonly string[] ObjectExts = { ".o", ".obj" };
    static readonly string[] ArchiveExts = { ".a", ".lib" };

    /// <param name="compiles">The log's compiles.</param>
    /// <param name="steps">The log's link and archive commands.</param>
    /// <param name="fullDir">A command's directory as written to a full path (relative ones against the carve root).</param>
    /// <param name="relOf">A compile's file relative to the carve root, or null outside it.</param>
    /// <param name="root">The carve root as a full path, for <see cref="InputsNotFromLog"/>; without it that stays -1.</param>
    public LinkModel(IEnumerable<CompileCommand> compiles, IEnumerable<BuildLogScraper.ToolStep> steps,
                     Func<string, string> fullDir, Func<CompileCommand, string?> relOf, string? root = null)
    {
        var stepList = steps.ToList();
        var links = stepList.Where(s => s.Kind == BuildLogScraper.ToolStepKind.Link).ToList();
        Links = links.Count;
        // Nothing to go on, or a link we can't read whole: every file stays Unknown.
        if (links.Count == 0 || links.Any(l => l.Incomplete)) return;

        string? Full(string d, string p)
        {
            try { return Key(Path.GetFullPath(Path.IsPathFullyQualified(p) ? p : Path.Combine(fullDir(d), p))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }

        // Objects each source compiled to; and sources a link compiles itself.
        var objectsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sourceRel = new Dictionary<string, string>(StringComparer.Ordinal);   // full source -> rel
        var unmapped = new HashSet<string>(StringComparer.Ordinal);               // rels with a compile whose object we can't tell
        foreach (var cc in compiles)
        {
            if (relOf(cc) is not { } rel) continue;
            if (Full(cc.Directory, cc.File) is { } fs) sourceRel[fs] = rel;
            if (!cc.Arguments.Contains("-c")) continue;   // a compile-and-link: the link step names the source
            var o = ObjectOf(cc);
            var full = o is null ? null : Full(cc.Directory, o);
            if (full is null) { unmapped.Add(rel); continue; }
            if (!objectsOf.TryGetValue(rel, out var set)) objectsOf[rel] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(full);
        }

        var members = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var s in stepList.Where(s => s.Kind == BuildLogScraper.ToolStepKind.Archive))
        {
            if (s.Incomplete) return;
            var a = s.Arguments.ToList();
            var i = 0;
            while (i < a.Count && a[i].StartsWith("--", StringComparison.Ordinal)) i += a[i] is "--plugin" or "--target" ? 2 : 1;
            if (i + 1 >= a.Count) continue;
            var op = a[i].TrimStart('-');
            if (op.IndexOfAny(new[] { 'd', 'x', 't', 'p' }) >= 0) continue;   // delete, extract, list, print
            if (Full(s.Directory, a[i + 1]) is not { } archive) continue;
            if (!members.TryGetValue(archive, out var m)) members[archive] = m = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in a.Skip(i + 2))
                if (!x.StartsWith('-') && Full(s.Directory, x) is { } f) m.Add(f);
        }

        var direct = new HashSet<string>(StringComparer.Ordinal);       // objects and sources a link takes whole
        var searched = new HashSet<string>(StringComparer.Ordinal);     // members of archives a link searches
        var archivesSearched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            var a = l.Arguments;
            var libDirs = new List<string>();
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] == "-L" && i + 1 < a.Count) libDirs.Add(a[++i]);
                else if (a[i].StartsWith("-L", StringComparison.Ordinal) && a[i].Length > 2) libDirs.Add(a[i][2..]);
            }
            var whole = false;
            void Archive(string full)
            {
                if (!members.TryGetValue(full, out var m)) return;   // a library the log didn't build (system, prebuilt)
                archivesSearched.Add(full);
                (whole ? direct : searched).UnionWith(m);
            }
            void LinkerArg(string x)
            {
                if (x is "--whole-archive" or "-whole-archive") whole = true;
                else if (x is "--no-whole-archive" or "-no-whole-archive") whole = false;
            }
            void Library(string name)
            {
                var files = name.StartsWith(':') ? new[] { name[1..] } : new[] { $"lib{name}.a", $"{name}.lib", $"lib{name}.lib" };
                foreach (var d in libDirs)
                    foreach (var f in files)
                        if (Full(l.Directory, Path.Combine(d, f)) is { } full && members.ContainsKey(full)) { Archive(full); return; }
            }
            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                if (x is "-o" or "-T" or "-Map" or "-e" or "-u" or "-x" or "-MF" or "-MT" or "-MQ" or "-include" or "-isystem"
                         or "-I" or "-D" or "-U" or "-L" or "--entry" or "--undefined" or "--script" or "-z") { i++; continue; }
                if (x == "-Xlinker" && i + 1 < a.Count) { LinkerArg(a[++i]); continue; }
                if (x.StartsWith("-Wl,", StringComparison.Ordinal)) { foreach (var p in x[4..].Split(',')) LinkerArg(p); continue; }
                if (x == "-l" && i + 1 < a.Count) { Library(a[++i]); continue; }
                if (x.StartsWith("-l", StringComparison.Ordinal) && x.Length > 2) { Library(x[2..]); continue; }
                if (x.StartsWith('-')) { LinkerArg(x); continue; }
                if (Full(l.Directory, x) is not { } full) continue;
                if (members.ContainsKey(full)) Archive(full);
                else direct.Add(full);
            }
        }
        ArchivesSearched = archivesSearched.Count;

        if (root is not null)
        {
            var made = new HashSet<string>(objectsOf.Values.SelectMany(o => o), StringComparer.Ordinal);
            made.UnionWith(members.Keys);
            var outDirs = made.Select(Path.GetDirectoryName).Where(d => d is not null).Select(d => d!).ToHashSet(StringComparer.Ordinal);
            var rootKey = Key(Path.GetFullPath(root)).TrimEnd('\\', '/');
            bool InRoot(string full) => full.StartsWith(rootKey, StringComparison.Ordinal) && full.Length > rootKey.Length
                                        && full[rootKey.Length] is '\\' or '/';
            bool HasExt(string full, string[] exts) => exts.Any(e => full.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            var notFromLog = direct.Concat(searched).Distinct(StringComparer.Ordinal).Where(full =>
                !made.Contains(full) && !sourceRel.ContainsKey(full)
                && (HasExt(full, ObjectExts) ? InRoot(full) || outDirs.Contains(Path.GetDirectoryName(full) ?? "")
                    : HasExt(full, ArchiveExts) && outDirs.Contains(Path.GetDirectoryName(full) ?? ""))).ToList();
            InputsNotFromLog = notFromLog.Count;
            ArchivesNotFromLog = notFromLog.Count(f => HasExt(f, ArchiveExts));
            foreach (var f in notFromLog.Where(f => HasExt(f, ObjectExts))) _objectNamesNotFromLog.Add(Path.GetFileName(f));
        }

        foreach (var (full, rel) in sourceRel)
            if (direct.Contains(full)) _status[rel] = LinkStatus.Linked;
        foreach (var (rel, objs) in objectsOf)
        {
            if (_status.ContainsKey(rel) || unmapped.Contains(rel)) continue;
            if (objs.Any(direct.Contains)) _status[rel] = LinkStatus.Linked;
            else if (objs.All(searched.Contains)) _status[rel] = LinkStatus.ArchiveOnly;
        }
    }

    /// <summary>How <paramref name="rel"/>'s code gets into the image.</summary>
    public LinkStatus StatusOf(string rel) => _status.TryGetValue(rel, out var s) ? s : LinkStatus.Unknown;

    /// <summary>The object a <c>-c</c> compile writes: its <c>-o</c>, else the source's name with <c>.o</c> in the
    /// command's directory; null for an MSVC-style command (<c>/Fo</c>), which this doesn't read.</summary>
    static string? ObjectOf(CompileCommand cc)
    {
        var a = cc.Arguments;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] == "-o") return i + 1 < a.Count ? a[i + 1] : null;
            if (a[i].StartsWith("-o", StringComparison.Ordinal) && a[i].Length > 2) return a[i][2..];
            if (a[i].StartsWith("/Fo", StringComparison.OrdinalIgnoreCase) || a[i].StartsWith("-Fo", StringComparison.Ordinal)) return null;
        }
        return Path.ChangeExtension(Path.GetFileName(cc.File), ".o");
    }

    static string Key(string full) => OperatingSystem.IsWindows() ? full.Replace('/', '\\').ToUpperInvariant() : full;
}
