using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;

namespace CodeCarver.Core.Emit;

/// <summary>Outcome of writing a carved tree: how many files (and bytes) were emitted, and which.</summary>
public sealed record EmitResult(int FilesWritten, long BytesWritten, IReadOnlyList<string> Written);

/// <summary>
/// The simplest emitter: file-level carve. It copies the plan's kept files (preserving their relative
/// layout) into an output directory and omits everything else — i.e. dead/unnecessary translation
/// units and headers are simply absent from the result. Because a kept .c pulls in the headers it
/// needs (the include closure), the emitted tree is self-consistent to compile.
///
/// This is L0/L1 of the fidelity ladder (whole-file granularity). Intra-file pruning — removing the
/// unused functions inside a kept file — is a later emitter that rewrites file contents; the plan
/// already knows, per node, what is kept, so that emitter slots in beside this one.
/// </summary>
public static class FileTreeEmitter
{
    public static EmitResult Emit(CarvePlan plan, string sourceRoot, string outDir)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var written = new List<string>();
        long bytes = 0;

        foreach (var rel in plan.KeptFiles)
        {
            var src = Path.Combine(sourceRoot, rel);
            if (!File.Exists(src)) continue; // a header/path not present on disk (e.g. synthesized) — skip

            var dst = Path.Combine(outDir, rel);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);

            File.Copy(src, dst, overwrite: true);
            bytes += new FileInfo(dst).Length;
            written.Add(rel);
        }

        CopyUnscannedIncludes(plan, sourceRoot, outDir, written, ref bytes);
        return new EmitResult(written.Count, bytes, written);
    }

    /// <summary>
    /// Intra-file carve: like <see cref="Emit"/>, but each kept file is REWRITTEN to remove the
    /// unreached function definitions inside it (the "4 of 1000 functions" win). This is the tightening
    /// step that actually shrinks a repo whose code concentrates in a few big files.
    ///
    /// Deliberately conservative for soundness: it removes only unreached <b>functions</b> (whose
    /// use-edges — calls + address-taken — we track well), and never touches types, macros, or
    /// includes (whose reference edges we don't yet fully model, so a "dropped" one might still be
    /// needed). Even so, a function used ONLY from inside a macro body or inline asm is a known blind
    /// spot — which is exactly why intra-file output must be validated by actually building it (the
    /// linker-map / build oracle), not trusted blind.
    /// </summary>
    public static EmitResult EmitPruned(CarvePlan plan, CodeGraph graph, string sourceRoot, string outDir)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(graph);

        // Gather ALL prunable definition spans per file — functions AND initialized globals (data
        // tables) — kept + dropped, so we can detect misparse/overlap and remove unreached ones.
        var spansByFile = new Dictionary<string, List<(int Start, int End, bool Drop)>>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (n.Kind is not (NodeKind.Function or NodeKind.Global)) continue;
            if (n.FilePath is not { } f || !n.Span.IsKnown) continue;
            if (!spansByFile.TryGetValue(f, out var list))
                spansByFile[f] = list = new List<(int, int, bool)>();
            list.Add((n.Span.StartLine, n.Span.EndLine, !plan.IsKept(n.Id)));
        }

        // A dropped function is only safe to remove if its span does not overlap another function's.
        // Overlapping spans mean a mis-parse (unusual macro-prefixed or heavily #ifdef'd declarations);
        // removing one would corrupt the other, so keep both — a sound over-approximation.
        var dropByFile = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        foreach (var (f, list) in spansByFile)
        {
            list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
            var drops = new List<(int, int)>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].Drop) continue;
                var overlaps = (i > 0 && list[i - 1].End >= list[i].Start)
                            || (i + 1 < list.Count && list[i + 1].Start <= list[i].End);
                if (!overlaps) drops.Add((list[i].Start, list[i].End));
            }
            if (drops.Count > 0) dropByFile[f] = drops;
        }

        var written = new List<string>();
        long bytes = 0;
        foreach (var rel in plan.KeptFiles)
        {
            var src = Path.Combine(sourceRoot, rel);
            if (!File.Exists(src)) continue;

            var dst = Path.Combine(outDir, rel);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);

            // Only files with something to prune are read+rewritten. Everything else — including
            // multi-GB headers kept whole — is stream-copied, so a big kept file never becomes a
            // >2GB string in memory (it would throw) and is emitted in bounded memory.
            if (dropByFile.TryGetValue(rel, out var ranges) && ranges.Count > 0)
                File.WriteAllText(dst, RemoveLineRanges(File.ReadAllText(src), ranges));
            else
                File.Copy(src, dst, overwrite: true);
            bytes += new FileInfo(dst).Length;
            written.Add(rel);
        }

        CopyUnscannedIncludes(plan, sourceRoot, outDir, written, ref bytes);
        return new EmitResult(written.Count, bytes, written);
    }

    private static readonly Regex LocalInclude = new(
        "^\\s*#\\s*include\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Copy files that emitted code <c>#include</c>s but the carve never modelled — a local include with
    /// a non-source extension (<c>.inc</c>, <c>.def</c>, generated tables) or in an excluded directory.
    /// Such a file is not a graph node, so the include-closure can't keep it, yet the emitted tree won't
    /// compile without it. Resolve each <c>"…"</c> include relative to the including file, confined to the
    /// source tree, and copy any target unknown to the carve — recursing into what it in turn includes.
    /// Sound: it only ADDS files, never touches an emitted/pruned one, and never resurrects a header the
    /// carve deliberately dropped (those are graph-known, so excluded here).
    /// </summary>
    private static void CopyUnscannedIncludes(CarvePlan plan, string sourceRoot, string outDir,
                                              List<string> written, ref long bytes)
    {
        var root = Path.GetFullPath(sourceRoot);
        var known = new HashSet<string>(plan.KeptFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var f in plan.DroppedFiles) known.Add(f);          // graph-known drops: leave dropped
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(written);                      // scan every emitted file for includes

        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();
            var full = Path.Combine(sourceRoot, rel);
            // Read only a bounded prefix: includes live at the top, and a multi-GB kept file must never
            // become a >2GB string. Files without readable text simply contribute no includes.
            string text;
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > 8 * 1024 * 1024) continue;
                text = File.ReadAllText(full);
            }
            catch { continue; }

            var fromDir = Path.GetDirectoryName(full) ?? sourceRoot;
            foreach (Match m in LocalInclude.Matches(text))
            {
                var target = Path.GetFullPath(Path.Combine(fromDir, m.Groups[1].Value));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // outside the tree
                if (!File.Exists(target)) continue;                  // unresolved here (system/other -I dir)
                var trel = Path.GetRelativePath(sourceRoot, target).Replace('\\', '/');
                if (known.Contains(trel) || !copied.Add(trel)) continue; // graph-known or already copied

                var dst = Path.Combine(outDir, trel);
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                File.Copy(target, dst, overwrite: true);
                bytes += new FileInfo(dst).Length;
                written.Add(trel);
                queue.Enqueue(trel);                                 // its own includes may need copying too
            }
        }
    }

    /// <summary>Net <c>{</c> minus <c>}</c> over a line range, ignoring // and /* */ comments and
    /// string/char literals. Zero means the range encloses whole, balanced blocks (safe to remove).</summary>
    private static int NetBraces(string[] lines, int start, int end)
    {
        var net = 0;
        var inBlock = false;
        for (var i = Math.Max(1, start); i <= end && i <= lines.Length; i++)
        {
            var line = lines[i - 1];
            var quote = '\0';
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (inBlock)
                {
                    if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/') { inBlock = false; c++; }
                }
                else if (quote != '\0')
                {
                    // Consume an escaped char whole (handles '\\', '\'', "\"") so the escape can't
                    // swallow the closing quote — the classic `case '\\': {` miscount.
                    if (ch == '\\') c++;
                    else if (ch == quote) quote = '\0';
                }
                else if (ch is '"' or '\'') quote = ch;
                else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') break;
                else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*') { inBlock = true; c++; }
                else if (ch == '{') net++;
                else if (ch == '}') net--;
            }
        }
        return net;
    }

    /// <summary>Remove the given 1-based inclusive line ranges from text, preserving the rest verbatim.</summary>
    private static string RemoveLineRanges(string text, List<(int Start, int End)> ranges)
    {
        var lines = text.Split('\n');
        var drop = new bool[lines.Length + 2];
        foreach (var (s, e) in ranges)
        {
            // A whole definition has balanced braces. If a span's braces don't balance, tree-sitter
            // mis-parsed/truncated it (e.g. `if mi_likely(cond) {` from a macro-wrapped condition) —
            // removing it would strip a head or leave a dangling tail. Keep it whole (sound).
            if (NetBraces(lines, s, e) != 0) continue;

            // Shared-closing-brace case: an `#if X / TYPE foo(...){ / #else / TYPE bar(...){ / #endif`
            // gives two signatures ONE body — tree-sitter captures only the #else one, so its span's
            // closing `}` is really shared with the #if signature above (the compiler compiles exactly
            // one branch, so it sees a matched pair). Removing the span alone orphans the preserved `{`.
            // Extend the removal UP to swallow that signature line so the whole construct goes together.
            var start = s;
            var pre = NetBraces(lines, Math.Max(1, s - 10), s - 1);
            if (pre > 0)
            {
                for (var k = s - 1; k >= Math.Max(1, s - 12); k--)
                    if (NetBraces(lines, k, k) > 0 && NetBraces(lines, k, s - 1) == pre)
                        start = k; // the orphaned open-brace signature line
                if (start == s) continue; // couldn't locate it -> keep whole (sound)
            }
            for (var i = Math.Max(1, start); i <= e && i <= lines.Length; i++)
                drop[i] = true;
        }

        // Never delete a preprocessor directive (or its backslash-continuation) even inside a dropped
        // function: removing an #if/#endif would unbalance conditionals. The function's code is still
        // removed; a stray #if/#endif left behind is a harmless empty conditional.
        for (var i = 1; i <= lines.Length; i++)
        {
            if (!drop[i] || !lines[i - 1].TrimStart().StartsWith('#')) continue;
            drop[i] = false;
            var j = i;
            while (j <= lines.Length && lines[j - 1].TrimEnd().EndsWith('\\'))
            {
                j++;
                if (j <= lines.Length) drop[j] = false;
            }
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 1; i <= lines.Length; i++)
        {
            if (drop[i]) continue;
            sb.Append(lines[i - 1]);
            if (i < lines.Length) sb.Append('\n');
        }
        return sb.ToString();
    }
}
