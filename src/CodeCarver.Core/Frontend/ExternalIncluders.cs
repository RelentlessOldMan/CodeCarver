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
        var dirs = includeDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byName = rootFiles.GroupBy(r => r[(r.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        void Enqueue(string full)
        {
            if (seen.Count < maxFiles && CodeExt.Contains(Path.GetExtension(full)) && !skip(full) && seen.Add(full)) queue.Enqueue(full);
        }
        foreach (var f in outsideFiles)
            try { var full = Path.GetFullPath(f); if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) Enqueue(full); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }

        var scanned = 0;
        while (queue.Count > 0)
        {
            var full = queue.Dequeue();
            string? text;
            try { text = File.Exists(full) ? read(full) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = null; }
            if (text is null || !text.Contains("include", StringComparison.Ordinal)) continue;
            scanned++;
            var fromDir = Path.GetDirectoryName(full) ?? "";
            foreach (Match m in Include.Matches(text))
            {
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().Replace('\\', '/');
                if (raw.Length == 0) continue;
                var hit = (m.Groups[1].Success ? Probe(fromDir, raw) : null) ?? dirs.Select(d => Probe(d, raw)).FirstOrDefault(p => p is not null);
                if (hit is not null)
                {
                    if (hit.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        found.Add(Path.GetRelativePath(rootPrefix, hit).Replace('\\', '/'));
                    else Enqueue(hit);
                    continue;
                }
                // Resolves nowhere we know: every root file the include could name.
                var tail = string.Join('/', raw.Split('/').SkipWhile(s => s is "." or ".." or ""));
                if (tail.Length == 0 || !byName.TryGetValue(tail[(tail.LastIndexOf('/') + 1)..], out var cands)) continue;
                foreach (var c in cands)
                    if (c.Equals(tail, StringComparison.OrdinalIgnoreCase) || c.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase))
                        found.Add(c);
            }
        }
        return new Result(found.OrderBy(r => r, StringComparer.Ordinal).ToList(), scanned);
    }

    private static string? Probe(string dir, string raw)
    {
        try { var p = Path.GetFullPath(Path.Combine(dir, raw)); return File.Exists(p) ? p : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
