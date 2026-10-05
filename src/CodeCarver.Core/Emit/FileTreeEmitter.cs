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
            if (SameFile(src, dst)) continue; // out overlaps source — never copy a file onto itself
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);

            // Best-effort: a kept file that's locked/vanished between planning and emit (rare — antivirus, a
            // Perforce sync, a file kept-whole precisely because it was unreadable) must not crash the whole
            // emit. Skip it (it just won't be in the output); the read-time warning already flagged it.
            try { File.Copy(src, dst, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
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

        // HEADERS are never pruned: a header is the API surface, and its inline/template definitions may
        // be used by any translation unit — including code OUTSIDE the carve (the app that consumes the
        // carved library). Removing an unreached inline method from a header (C++ especially) silently
        // breaks such a caller. Headers are kept whole; only implementation units are pruned. This also
        // sidesteps a class of C++ mis-parses where a giant function's span swallows a nested class.
        // A needed constructor is handled upstream: the CLI's ConstructorGate ROOTS every constructor whose
        // class is named in emitted text, so reachability keeps it AND everything it calls (its
        // member-initializer list / body). Here it simply appears as a reached node.
        var defsByFile = EmitClosure.DefinitionsByFile(graph);

        var written = new List<string>();
        long bytes = 0;
        foreach (var rel in plan.KeptFiles)
        {
            var src = Path.Combine(sourceRoot, rel);
            if (!File.Exists(src)) continue;

            var dst = Path.Combine(outDir, rel);
            // CRITICAL: out overlapping source would make dst==src, and the WriteAllText below would replace
            // the user's source with its function-stripped version. The CLI already refuses overlapping
            // --out; this is the last line of defence for direct library callers.
            if (SameFile(src, dst)) continue;
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);

            // Only files with something to prune are read+rewritten. Everything else — including
            // multi-GB headers kept whole — is stream-copied, so a big kept file never becomes a
            // >2GB string in memory (it would throw) and is emitted in bounded memory.
            var ranges = !IsHeader(rel) && defsByFile.TryGetValue(rel, out var defs) ? DropRangesFor(defs, plan.IsKept) : null;
            // Rewritten byte-transparently (Latin-1): a Latin-1/Shift-JIS/UTF-8 string literal must come out with
            // exactly its original bytes (review E1). UTF-16 text can't be edited line-wise this way: copy it whole
            // (CarvePlan already closed over the whole file being written; see EmitClosure).
            try
            {
                if (ranges is { Count: > 0 } && !IsUtf16(src))
                    // Bytes in, bytes out: ReadAllText would honour (and strip) a UTF-8 BOM even when told Latin-1.
                    File.WriteAllBytes(dst, Encoding.Latin1.GetBytes(RemoveLineRanges(Encoding.Latin1.GetString(File.ReadAllBytes(src)), ranges)));
                else
                    File.Copy(src, dst, overwrite: true);
            }
            // A locked/vanished file must not sink the whole emit (review RB5) — same tolerance as Emit.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            bytes += new FileInfo(dst).Length;
            written.Add(rel);
        }

        CopyUnscannedIncludes(plan, sourceRoot, outDir, written, ref bytes);
        return new EmitResult(written.Count, bytes, written);
    }

    /// <summary>
    /// The definition spans the pruned emitter would like to remove from one implementation file: unreached
    /// functions and initialized globals whose span overlaps no other definition. Null when the file must be
    /// written whole (a dropped span overlaps a kept one — a mis-parse; see below).
    /// </summary>
    static List<(int Start, int End)>? DropRangesFor(IReadOnlyList<Node> defs, Func<NodeId, bool> isKept)
    {
        var list = defs.Where(n => n.Span.IsKnown)
                       .Select(n => (Start: n.Span.StartLine, End: n.Span.EndLine, Drop: !isKept(n.Id))).ToList();
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
        if (list.Any(d => d.Drop && kept.Any(k => k.Start <= d.End && d.Start <= k.End))) return null;

        // A dropped function is only safe to remove if its span does not overlap another function's.
        // Overlapping spans mean a mis-parse (unusual macro-prefixed or heavily #ifdef'd declarations);
        // removing one would corrupt the other, so keep both — a sound over-approximation.
        var drops = new List<(int, int)>();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].Drop) continue;
            var overlaps = (i > 0 && list[i - 1].End >= list[i].Start)
                        || (i + 1 < list.Count && list[i + 1].Start <= list[i].End);
            if (!overlaps) drops.Add((list[i].Start, list[i].End));
        }
        return drops;
    }

    /// <summary>
    /// The unreached definitions in <paramref name="rel"/> that the emitter will nevertheless WRITE — the
    /// policy <see cref="EmitClosure"/> roots so a carved tree links (review F1). File-level: every unreached
    /// definition in a kept implementation file. Pruned: those whose span the pruner keeps (overlap, unbalanced
    /// braces, the whole-file fallback). Headers, either way: only definitions a compiler emits code for.
    /// </summary>
    public static IEnumerable<NodeId> RetainedWhenEmitted(string rel, IReadOnlyList<Node>? defs, Func<NodeId, bool> isKept,
                                                          Func<string, string?> readText, bool pruned)
    {
        if (defs is null || defs.Count == 0) return Array.Empty<NodeId>();
        if (IsHeader(rel))
            return EmitClosure.RetainedInHeader(defs, readText(rel)).Where(id => !isKept(id)).ToList();
        var unreached = defs.Where(n => !isKept(n.Id)).ToList();
        if (!pruned || unreached.Count == 0) return unreached.Select(n => n.Id).ToList();

        var ranges = DropRangesFor(defs, isKept);
        var text = ranges is { Count: > 0 } ? readText(rel) : null;
        if (text is not null && text.Length > 1 && (text[0] == '\u00FF' && text[1] == '\u00FE' || text[0] == '\u00FE' && text[1] == '\u00FF'))
            text = null;   // UTF-16 (read as Latin-1): EmitPruned copies it whole
        if (text is null) return unreached.Select(n => n.Id).ToList();        // written whole
        var applied = AppliedRanges(text.Split('\n'), ranges!);
        return unreached.Where(n => !(n.Span.IsKnown && applied.Contains((n.Span.StartLine, n.Span.EndLine))))
                        .Select(n => n.Id).ToList();
    }

    /// <summary>UTF-16 by byte-order mark.</summary>
    public static bool IsUtf16(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int a = fs.ReadByte(), b = fs.ReadByte();
            return (a == 0xFF && b == 0xFE) || (a == 0xFE && b == 0xFF);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Do these two paths resolve to the same file on disk? Used to refuse writing a carved file
    /// on top of its own source when <c>--out</c> overlaps the source tree. Path case is compared
    /// per-platform; an unresolvable path is treated as "not the same" (the copy will fail loudly instead).</summary>
    private static bool SameFile(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
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
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)) + Path.DirectorySeparatorChar;
        var outRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir)) + Path.DirectorySeparatorChar;
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
                string target;
                try { target = Path.GetFullPath(Path.Combine(fromDir, m.Groups[1].Value)); } catch { continue; }
                // Under the root WITH a separator: "/repo2" must not pass for root "/repo" (review RB9).
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // outside the tree
                if (!File.Exists(target)) continue;                  // unresolved here (system/other -I dir)
                var trel = Path.GetRelativePath(sourceRoot, target).Replace('\\', '/');
                if (known.Contains(trel) || !copied.Add(trel)) continue; // graph-known or already copied

                var dst = Path.GetFullPath(Path.Combine(outDir, trel));
                if (!dst.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the output
                if (SameFile(target, dst)) continue; // out overlaps source — don't copy onto the original
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                try { File.Copy(target, dst, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
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

    /// <summary>Does the span's first line hold code before the definition, or its last line code after it?
    /// Before: a <c>;</c> or <c>}</c> ahead of the definition's first <c>(</c>, <c>=</c> or <c>{</c> ends some
    /// other declaration. After: anything but <c>;</c>, whitespace and comments past the final <c>}</c>.</summary>
    private static bool SharesLine(string[] lines, int s, int e)
    {
        if (s < 1 || e > lines.Length) return false;
        foreach (var ch in CodeOnly(lines[s - 1]))
        {
            if (ch is '(' or '=' or '{') break;
            if (ch is ';' or '}') return true;
        }
        var last = CodeOnly(lines[e - 1]);
        var close = last.LastIndexOf('}');
        if (close < 0) return false;   // no closing brace on the last line: nothing after it to lose
        return last[(close + 1)..].Any(ch => !char.IsWhiteSpace(ch) && ch != ';');
    }

    /// <summary>A line with comments removed and string/char literals emptied (one line; a block comment opened
    /// on an earlier line is not tracked — callers only use this to decide to keep MORE).</summary>
    private static string CodeOnly(string line)
    {
        var sb = new StringBuilder(line.Length);
        var quote = '\0';
        for (var c = 0; c < line.Length; c++)
        {
            var ch = line[c];
            if (quote != '\0')
            {
                if (ch == '\\') c++;
                else if (ch == quote) { quote = '\0'; sb.Append(ch); }
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; sb.Append(ch); continue; }
            if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') break;
            if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*')
            {
                var endC = line.IndexOf("*/", c + 2, StringComparison.Ordinal);
                if (endC < 0) break;
                c = endC + 1;
                sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Remove the given 1-based inclusive line ranges from text, preserving the rest verbatim.</summary>
    private static string RemoveLineRanges(string text, List<(int Start, int End)> ranges)
    {
        var lines = text.Split('\n');
        var drop = DropLines(lines, ranges, null);
        var sb = new StringBuilder(text.Length);
        for (var i = 1; i <= lines.Length; i++)
        {
            if (drop[i]) continue;
            sb.Append(lines[i - 1]);
            if (i < lines.Length) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The requested ranges that <see cref="RemoveLineRanges"/> actually removes.</summary>
    private static HashSet<(int, int)> AppliedRanges(string[] lines, List<(int Start, int End)> ranges)
    {
        var applied = new HashSet<(int, int)>();
        DropLines(lines, ranges, applied);
        return applied;
    }

    private static bool[] DropLines(string[] lines, List<(int Start, int End)> ranges, HashSet<(int, int)>? applied)
    {
        var drop = new bool[lines.Length + 2];
        foreach (var (s, e) in ranges)
        {
            // A whole definition has balanced braces. If a span's braces don't balance, tree-sitter
            // mis-parsed/truncated it (e.g. `if mi_likely(cond) {` from a macro-wrapped condition) —
            // removing it would strip a head or leave a dangling tail. Keep it whole (sound).
            if (NetBraces(lines, s, e) != 0) continue;

            // Removal is by whole lines, so a span whose first or last line also holds other code
            // (`int g; int dead(void){...}`, `} int dead(...)`, `...} int h;`) would take that code with it
            // (review RB9b). Keep such a span whole — sound, and EmitClosure then treats it as written.
            if (SharesLine(lines, s, e)) continue;

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
            applied?.Add((s, e));
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
        return drop;
    }
}
