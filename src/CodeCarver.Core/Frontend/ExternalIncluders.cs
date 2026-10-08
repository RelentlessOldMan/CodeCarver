using System.Text.RegularExpressions;
using CodeCarver.Core.Preprocess;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Headers under the carve root that code OUTSIDE it <c>#include</c>s: the rest of the product compiles against
/// them (shared glue reaching into the module's public API), so the carve must keep them whole even when nothing
/// the entry points reach includes them. The outside code is the build's own evidence: the sources the build log
/// compiles outside the root and the files the build trace opened there. Their <c>#include</c>s are followed
/// through other outside files; one that lands in the root names a header to keep. An include that resolves
/// nowhere (its <c>-I</c> isn't in the log) matches every root file whose path ends with it: keeping a header too
/// many is the sound side.
/// </summary>
public static class ExternalIncluders
{
    public sealed record Result(IReadOnlyList<string> Headers, int FilesScanned);

    private static readonly Regex Include = new(@"^[ \t]*" + SourceText.DirectiveStart + @"[ \t]*include(?:_next)?[ \t]*(?:""([^""\r\n]+)""|<([^>\r\n]+)>)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".h", ".hh", ".hpp", ".hxx", ".h++", ".inc", ".def", ".inl", ".ipp", ".tcc", ".tpp",
          ".c", ".cc", ".cpp", ".cxx", ".c++", ".s", ".sx" };

    /// <param name="outsideFiles">Full paths of outside code the build compiled or opened.</param>
    /// <param name="includeDirs">The build log's include directories (full paths).</param>
    /// <param name="root">The carve root (full path).</param>
    /// <param name="rootFiles">Every file under the root, relative with '/'.</param>
    /// <param name="skip">Outside paths never to read (compiler and system include trees).</param>
    /// <param name="read">Reads a file's text, or null when it can't.</param>
    public static Result Find(IEnumerable<string> outsideFiles, IEnumerable<string> includeDirs, string root,
                              IEnumerable<string> rootFiles, Func<string, bool> skip, Func<string, string?> read,
                              int maxFiles = 200_000)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var dirs = includeDirs.Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byName = rootFiles.GroupBy(r => r[(r.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        void Enqueue(string full)   // a header is reached by thousands of includes: decide once
        {
            if (seen.Count < maxFiles && seen.Add(full) && CodeExt.Contains(Path.GetExtension(full)) && !skip(full)) queue.Enqueue(full);
        }
        foreach (var f in outsideFiles)
            try { var full = Path.GetFullPath(f); if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) Enqueue(full); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }

        // Thousands of files x hundreds of -I dirs: each (dir, name) is probed once, each name's -I search once,
        // and each unresolved name's root match once. Whether a file exists is looked up in its directory's listing,
        // read once: millions of File.Exists calls were minutes on a big tree.
        var listings = new Dictionary<string, HashSet<string>>(NameComparer);
        HashSet<string> Listing(string parent)
        {
            if (listings.TryGetValue(parent, out var names)) return names;
            names = new HashSet<string>(NameComparer);
            try { foreach (var f in Directory.EnumerateFiles(parent)) names.Add(Path.GetFileName(f)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            return listings[parent] = names;
        }
        string? Probe(string dir, string raw)   // dir is a full path
        {
            if (IsBareName(raw)) return Listing(dir).Contains(raw) ? Path.Combine(dir, raw) : null;
            string p;
            try { p = Path.GetFullPath(Path.Combine(dir, raw)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            return Path.GetDirectoryName(p) is { } parent && Listing(parent).Contains(Path.GetFileName(p)) ? p : null;
        }
        var local = new Dictionary<(string, string), string?>();
        var viaDirs = new Dictionary<string, string?>(StringComparer.Ordinal);
        var byTail = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        while (queue.Count > 0)
        {
            var full = queue.Dequeue();
            var fromDir = Path.GetDirectoryName(full) ?? "";
            var fromListing = Listing(fromDir);
            string? text;
            try { text = fromListing.Contains(Path.GetFileName(full)) ? read(full) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = null; }
            if (text is null || !text.Contains("include", StringComparison.Ordinal)) continue;
            scanned++;
            foreach (Match m in Include.Matches(text))
            {
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().Replace('\\', '/');
                if (raw.Length == 0) continue;
                string? hit = null;
                if (m.Groups[1].Success)
                {
                    if (IsBareName(raw)) hit = fromListing.Contains(raw) ? Path.Combine(fromDir, raw) : null;
                    else if (!local.TryGetValue((fromDir, raw), out hit)) local[(fromDir, raw)] = hit = Probe(fromDir, raw);
                }
                if (hit is null && !viaDirs.TryGetValue(raw, out hit))
                    viaDirs[raw] = hit = dirs.Select(d => Probe(d, raw)).FirstOrDefault(p => p is not null);
                if (hit is not null)
                {
                    if (hit.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        found.Add(Path.GetRelativePath(rootPrefix, hit).Replace('\\', '/'));
                    else Enqueue(hit);
                    continue;
                }
                // Resolves nowhere we know: every root file the include could name.
                if (!byTail.TryGetValue(raw, out var matches))
                {
                    byTail[raw] = matches = new List<string>();
                    var tail = string.Join('/', raw.Split('/').SkipWhile(s => s is "." or ".." or ""));
                    if (tail.Length > 0 && byName.TryGetValue(tail[(tail.LastIndexOf('/') + 1)..], out var cands))
                        matches.AddRange(cands.Where(c => c.Equals(tail, StringComparison.OrdinalIgnoreCase)
                                                       || c.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase)));
                }
                found.UnionWith(matches);
            }
        }
        return new Result(found.OrderBy(r => r, StringComparer.Ordinal).ToList(), scanned);
    }

    private static bool IsBareName(string raw) => raw.IndexOf('/') < 0 && raw is not "." and not "..";

    // Directory listings compare names as the file system does.
    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
