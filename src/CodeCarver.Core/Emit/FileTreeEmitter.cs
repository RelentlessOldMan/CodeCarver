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
        // HEADERS are never pruned: a header is the API surface, and its inline/template definitions may
        // be used by any translation unit — including code OUTSIDE the carve (the app that consumes the
        // carved library). Removing an unreached inline method from a header (C++ especially) silently
        // breaks such a caller. Headers are kept whole; only implementation units are pruned. This also
        // sidesteps a class of C++ mis-parses where a giant function's span swallows a nested class.
        // Constructors are never pruned — but that's handled soundly upstream by ConstructorRootProvider,
        // which ROOTS them so reachability keeps the constructor AND everything it calls (its
        // member-initializer list / body). Here they simply appear as reached nodes.
        var spansByFile = new Dictionary<string, List<(int Start, int End, bool Drop)>>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (n.Kind is not (NodeKind.Function or NodeKind.Global)) continue;
            if (n.FilePath is not { } f || !n.Span.IsKnown || IsHeader(f)) continue;
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

            // If a DROPPED function's span overlaps a KEPT one, the parse nested/mis-grouped this file —
            // e.g. a local class or lambda defined INSIDE a big function, whose method is reached (by name /
            // virtual dispatch) while the enclosing function is not. The enclosing function is then kept
            // whole (its span can't be cleanly removed), but it may CALL other top-level functions in the
            // file that reachability dropped — pruning those would leave the kept-whole function dangling
            // (real tinyxml2 xmltest.cpp: a visitor class inside main() is reached via Accept, main is kept,
            // and its helper example_1() was wrongly pruned -> "example_1 was not declared"). We can't tell
            // what the kept-whole function references, so keep the WHOLE FILE (sound over-approximation).
            var kept = list.Where(x => !x.Drop).ToList();
            var droppedOverlapsKept = list.Any(d => d.Drop && kept.Any(k => k.Start <= d.End && d.Start <= k.End));
            if (droppedOverlapsKept) continue;

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

    /// <summary>A C/C++ header — its definitions are API/inline/template code any translation unit may
    /// use, so intra-file pruning leaves headers whole (extensionless includes like <c>&lt;vector&gt;</c>
    /// aren't graph files here). Implementation units (.c/.cc/.cpp/.cxx) are what get pruned.</summary>
    private static bool IsHeader(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && ext[0] == '.' &&
               ext.ToLowerInvariant() is ".h" or ".hpp" or ".hxx" or ".hh" or ".h++"
                                      or ".inl" or ".ipp" or ".tcc" or ".tpp";
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

            // A net-open brace in the lines just above the span can mean two very different things:
            //  (a) an `#if X / TYPE foo(...){ / #else / TYPE bar(...){ / #endif` dual-signature construct —
            //      two signatures share ONE body/closing `}`; tree-sitter captured only the #else one, so
            //      removing its span alone orphans the #if signature's `{`. The drop must extend UP to
            //      swallow that signature line. OR
            //  (b) the span is simply the first member of a class / namespace / extern-"C" block, whose
            //      opening brace is above. Its own braces are balanced (checked just above), so removing it
            //      as-is is correct — extending up would delete the ENCLOSING scope's `{` and shatter it
            //      (a real C++ bug: pruning a class's first method took out `class X {` and `public:`).
            // Only (a) has a preprocessor directive between the orphaned open-brace line and the drop; use
            // that to tell them apart. Without one, don't extend — the balanced span removes on its own.
            var start = s;
            var pre = NetBraces(lines, Math.Max(1, s - 10), s - 1);
            if (pre > 0)
            {
                var candidate = s;
                for (var k = s - 1; k >= Math.Max(1, s - 12); k--)
                    if (NetBraces(lines, k, k) > 0 && NetBraces(lines, k, s - 1) == pre)
                        candidate = k; // the orphaned open-brace signature line
                var sharedUnderPreproc = false;
                for (var k = candidate; candidate != s && k < s; k++)
                    if (lines[k - 1].TrimStart().StartsWith('#')) { sharedUnderPreproc = true; break; }
                // (a) dual-signature: the orphaned open-brace line is a FUNCTION SIGNATURE (has a
                // parameter list) sharing the drop's body. A bare `{` / `namespace X {` / `class X {`
                // scope-opener has no parens — extending up into it would delete the ENCLOSING scope's
                // brace (and any complete kept definition between), shattering the file. That false
                // positive is exactly what a `namespace pugi {` immediately followed by `#ifndef` hits
                // (real pugixml xpath_exception bug). Require parens on the candidate; when unsure, don't
                // extend (keep more = sound).
                var candidateIsSignature = lines[candidate - 1].Contains('(') || lines[candidate - 1].Contains(')');
                if (sharedUnderPreproc && candidateIsSignature) start = candidate; // (a) dual-signature — extend up
                // else (b): enclosing scope, leave start = s; remove the balanced span alone.
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
