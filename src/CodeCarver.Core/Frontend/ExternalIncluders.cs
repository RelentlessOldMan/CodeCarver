using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Headers under the carve root that code OUTSIDE it <c>#include</c>s: the rest of the product compiles against
/// them (shared glue reaching into the module's public API), so the carve must keep them whole even when nothing
/// the entry points reach includes them. The outside code is the build's own evidence: the sources the build log
/// compiles outside the root and the files the build trace opened there. Their <c>#include</c>s are followed
/// through other outside files, and through the root headers they land on (what those include is compiled into the
/// outside code too). An include that resolves nowhere (its <c>-I</c> isn't in the log) matches every root file
/// whose path ends with it: keeping a header too many is the sound side.
/// </summary>
public static class ExternalIncluders
{
    /// <param name="Capped">The scan stopped at its file limit: headers past it may have been missed.</param>
    public sealed record Result(IReadOnlyList<string> Headers, int FilesScanned, bool Capped = false);

    private static readonly Regex Include = new(@"^[ \t]*" + SourceText.DirectiveStart + @"[ \t]*include(_next)?[ \t]*(?:""([^""\r\n]+)""|<([^>\r\n]+)>)",
        RegexOptions.Compiled);
    private static readonly Regex Computed = new(@"^[ \t]*" + SourceText.DirectiveStart + @"[ \t]*include(?:_next)?[ \t]+[A-Za-z_]",
        RegexOptions.Compiled);
    // A header name a computed #include could expand to: a string or <...> spelling a file name.
    private static readonly Regex HeaderLiteral = new(@"""([\w./\\+-]+\.\w+)""|<([\w./\\+-]+\.\w+)>", RegexOptions.Compiled);

    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".h", ".hh", ".hpp", ".hxx", ".h++", ".inc", ".def", ".inl", ".ipp", ".tcc", ".tpp",
          ".c", ".cc", ".cpp", ".cxx", ".c++", ".s", ".sx" };

    /// <param name="outsideFiles">Full paths of outside code the build compiled or opened.</param>
    /// <param name="includeDirs">Every include directory of the build log's commands (full paths).</param>
    /// <param name="root">The carve root (full path).</param>
    /// <param name="rootFiles">Every file under the root, relative with '/'.</param>
    /// <param name="skip">Outside paths never to read (compiler and system include trees).</param>
    /// <param name="readLines">A file's lines, streamed (a generated header can be gigabytes), or null.</param>
    /// <param name="files">The run's shared directory listings, or null for fresh ones.</param>
    /// <param name="maxFiles">Code files followed at most.</param>
    public static Result Find(IEnumerable<string> outsideFiles, IEnumerable<string> includeDirs, string root,
                              IEnumerable<string> rootFiles, Func<string, bool> skip, Func<string, IEnumerable<string>?> readLines,
                              FileLookup? files = null, int maxFiles = 200_000)
    {
        var cmp = PathComparer.Comparison;
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var dirs = includeDirs.Select(Path.TrimEndingDirectorySeparator).Distinct(PathComparer.Default).ToList();
        var rootList = rootFiles.ToList();
        var byName = rootList.GroupBy(r => r[(r.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        // A hit is spelled as the #include and -I spell it; the root's walk spells it as the disk does. On Windows and
        // macOS those differ in case and name the same file.
        var walkSpelling = rootList.GroupBy(r => r, PathComparer.Default).ToDictionary(g => g.Key, g => g.First(), PathComparer.Default);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(PathComparer.Default);
        var queue = new Queue<string>();
        var queued = 0;
        var capped = false;
        bool InRoot(string full) => full.StartsWith(rootPrefix, cmp);
        // A header is reached by thousands of includes: decide once. Build and trace seeds are filtered to code (a
        // trace lists objects, libraries and tools too); whatever an #include reaches is code, extension or not.
        void Enqueue(string full, bool included)
        {
            if (!seen.Add(full) || (!included && !CodeExt.Contains(Path.GetExtension(full))) || skip(full)) return;
            if (queued >= maxFiles) { capped = true; return; }
            queued++;
            queue.Enqueue(full);
        }
        void Found(string full)
        {
            var rel = Path.GetRelativePath(rootPrefix, full).Replace('\\', '/');
            found.Add(walkSpelling.TryGetValue(rel, out var w) ? w : rel);
            Enqueue(full, included: true);
        }
        foreach (var f in outsideFiles)
            try { var full = Path.GetFullPath(f); if (!InRoot(full)) Enqueue(full, included: false); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }

        // Thousands of files x hundreds of -I dirs: each name's -I search runs once, each unresolved name's root
        // match once, and existence comes from directory listings (FileLookup), not a File.Exists per probe.
        files ??= new FileLookup();
        var local = new Dictionary<(string, string), string?>();
        var viaDirs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byTail = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void Resolve(string fromDir, string raw, bool quoted, bool next)
        {
            raw = raw.Trim().Replace('\\', '/');
            if (raw.Length == 0) return;
            // A quoted include looks beside its file first. Otherwise the -I search: every directory that has the
            // name, not just the first, since the list is the union of every command's and its order isn't this
            // file's (and #include_next continues past the wrapper that holds it).
            if (quoted && !next)
            {
                if (!local.TryGetValue((fromDir, raw), out var hit)) local[(fromDir, raw)] = hit = files.Probe(fromDir, raw);
                if (hit is not null) { if (InRoot(hit)) Found(hit); else Enqueue(hit, included: true); return; }
            }
            if (!viaDirs.TryGetValue(raw, out var hits))
                viaDirs[raw] = hits = dirs.Select(d => files.Probe(d, raw)).OfType<string>().Distinct(PathComparer.Default).ToList();
            foreach (var h in hits) if (InRoot(h)) Found(h); else Enqueue(h, included: true);
            if (hits.Count > 0) return;
            // Resolves nowhere we know: every root file the include could name.
            if (!byTail.TryGetValue(raw, out var matches))
            {
                byTail[raw] = matches = new List<string>();
                var tail = string.Join('/', raw.Split('/').SkipWhile(s => s is "." or ".." or ""));
                if (tail.Length > 0 && byName.TryGetValue(tail[(tail.LastIndexOf('/') + 1)..], out var cands))
                    matches.AddRange(cands.Where(c => c.Equals(tail, StringComparison.OrdinalIgnoreCase)
                                                   || c.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var m in matches) Found(Path.Combine(rootPrefix, m.Replace('/', Path.DirectorySeparatorChar)));
        }

        var scanned = 0;
        var literals = new List<string>();
        while (queue.Count > 0)
        {
            var full = queue.Dequeue();
            var fromDir = Path.GetDirectoryName(full) ?? "";
            var computed = false;
            literals.Clear();
            try
            {
                if (!files.Exists(full) || readLines(full) is not { } lines) continue;
                scanned++;
                foreach (var line in lines)
                {
                    if (line.Contains("include", StringComparison.Ordinal))
                    {
                        var m = Include.Match(line);
                        if (m.Success) { Resolve(fromDir, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, m.Groups[2].Success, m.Groups[1].Success); continue; }
                        if (Computed.IsMatch(line)) computed = true;
                    }
                    if (line.Contains('"') || line.Contains('<'))
                        foreach (Match l in HeaderLiteral.Matches(line))
                            if (literals.Count < 10_000) literals.Add(l.Groups[1].Success ? l.Groups[1].Value : l.Groups[2].Value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // #include MACRO: the header it expands to is one of the names the file spells (sound: maybe all of them).
            if (computed) foreach (var l in literals.ToList()) Resolve(fromDir, l, quoted: true, next: false);
        }
        return new Result(found.OrderBy(r => r, StringComparer.Ordinal).ToList(), scanned, capped);
    }
}
