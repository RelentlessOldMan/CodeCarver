using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;

namespace CodeCarver.Core.Emit;

/// <summary>Outcome of writing a carved tree: how many files (and bytes) were emitted, and which.</summary>
/// <param name="PrunedFiles">Files the pruned emitter rewrote with unreached definitions removed.</param>
/// <param name="PrunedBytes">Bytes those removals took out.</param>
public sealed record EmitResult(int FilesWritten, long BytesWritten, IReadOnlyList<string> Written, int PrunedFiles = 0, long PrunedBytes = 0);

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
        return Emit(plan.KeptFiles, plan.DroppedFiles, sourceRoot, outDir);
    }

    /// <summary>File-level emit from the kept and dropped file lists alone — what <c>--emit-from</c> replays from
    /// a prior analysis-only run without re-parsing.</summary>
    public static EmitResult Emit(IReadOnlyList<string> keptFiles, IReadOnlyList<string> droppedFiles, string sourceRoot, string outDir)
    {
        var written = new List<string>();
        long bytes = 0;

        foreach (var rel in keptFiles)
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

        CopyUnscannedIncludes(keptFiles, droppedFiles, sourceRoot, outDir, written, ref bytes);
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
        long bytes = 0, prunedBytes = 0;
        var prunedFiles = 0;
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
                {
                    // Bytes in, bytes out: ReadAllText would honour (and strip) a UTF-8 BOM even when told Latin-1.
                    var original = File.ReadAllBytes(src);
                    // Every function removed from this file, static or not (a `STATIC`/`PRIVATE` macro hides the keyword),
                    // unless the same name also has a kept definition here (#if-split twins): its prototypes go with it.
                    var fns = defsByFile[rel].Where(n => n.Kind == NodeKind.Function && n.Span.IsKnown).ToList();
                    var keptNames = fns.Where(n => plan.IsKept(n.Id)).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                    var removed = fns.Where(n => !plan.IsKept(n.Id) && !keptNames.Contains(n.Name))
                                     .Select(n => (n.Span.StartLine, n.Name)).ToList();
                    var pruned = Encoding.Latin1.GetBytes(RemoveLineRanges(Encoding.Latin1.GetString(original), ranges, removed));
                    File.WriteAllBytes(dst, pruned);
                    if (pruned.Length < original.Length) { prunedFiles++; prunedBytes += original.Length - pruned.Length; }
                }
                else
                    File.Copy(src, dst, overwrite: true);
            }
            // A locked/vanished file must not sink the whole emit (review RB5) — same tolerance as Emit.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            bytes += new FileInfo(dst).Length;
            written.Add(rel);
        }

        CopyUnscannedIncludes(plan.KeptFiles, plan.DroppedFiles, sourceRoot, outDir, written, ref bytes);
        return new EmitResult(written.Count, bytes, written, prunedFiles, prunedBytes);
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
        // Identical spans are ONE definition under several names — a head split across #if branches defines each
        // branch's name with the shared body. Merge them (dropped only when every name is) so they don't read
        // as the overlapping mis-parse handled below.
        for (var i = list.Count - 1; i > 0; i--)
            if (list[i].Start == list[i - 1].Start && list[i].End == list[i - 1].End)
            {
                list[i - 1] = (list[i - 1].Start, list[i - 1].End, list[i - 1].Drop && list[i].Drop);
                list.RemoveAt(i);
            }

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
        "^\\s*#\\s*include\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Copy files that emitted code <c>#include</c>s but the carve never modelled — a local include with
    /// a non-source extension (<c>.inc</c>, <c>.def</c>, generated tables) or in an excluded directory.
    /// Such a file is not a graph node, so the include-closure can't keep it, yet the emitted tree won't
    /// compile without it. Resolve each <c>"…"</c> include relative to the including file, confined to the
    /// source tree, and copy any target unknown to the carve — recursing into what it in turn includes.
    /// Sound: it only ADDS files, never touches an emitted/pruned one, and never resurrects a header the
    /// carve deliberately dropped (those are graph-known, so excluded here).
    /// </summary>
    private static void CopyUnscannedIncludes(IReadOnlyList<string> keptFiles, IReadOnlyList<string> droppedFiles, string sourceRoot, string outDir,
                                              List<string> written, ref long bytes)
    {
        var outRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir)) + Path.DirectorySeparatorChar;
        foreach (var trel in IncludeClosure(written, keptFiles, droppedFiles, sourceRoot))
        {
            var target = Path.Combine(sourceRoot, trel);
            var dst = Path.GetFullPath(Path.Combine(outDir, trel));
            if (!dst.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the output
            if (SameFile(target, dst)) continue; // out overlaps source - don't copy onto the original
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
            try { File.Copy(target, dst, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            bytes += new FileInfo(dst).Length;
            written.Add(trel);
        }
    }

    /// <summary>
    /// The files a file-level emit writes beyond the kept files: in-tree files the emitted files <c>#include</c>
    /// (transitively) that are not graph files - an <c>.inc</c> table, a header in an excluded directory. Graph-known
    /// files keep their own keep/drop decision. Computed from the source alone, so an analysis-only run can verify
    /// exactly what an emit would write (review: --emit-from carried over a verify that never saw these files).
    /// </summary>
    public static List<string> IncludeClosure(IEnumerable<string> emitted, IReadOnlyList<string> keptFiles,
                                              IReadOnlyList<string> droppedFiles, string sourceRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)) + Path.DirectorySeparatorChar;
        var known = new HashSet<string>(keptFiles, CodeCarver.Core.Util.PathComparer.Default);
        foreach (var f in droppedFiles) known.Add(f);               // graph-known drops: leave dropped
        var added = new HashSet<string>(CodeCarver.Core.Util.PathComparer.Default);
        var missing = new HashSet<string>(CodeCarver.Core.Util.PathComparer.Default);
        var result = new List<string>();
        var queue = new Queue<string>(emitted);                      // scan every emitted file for includes

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
                var trel = Path.GetRelativePath(sourceRoot, target).Replace(Path.DirectorySeparatorChar, '/');
                if (known.Contains(trel) || added.Contains(trel) || missing.Contains(trel)) continue;   // decided: no disk query
                if (!File.Exists(target)) { missing.Add(trel); continue; }   // unresolved here (system/other -I dir)
                added.Add(trel);
                result.Add(trel);
                queue.Enqueue(trel);                                 // its own includes may be needed too
            }
        }
        return result;
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
    /// Before: a <c>;</c> or <c>}</c> ahead of the first <c>{</c> with more code after it ends some other
    /// declaration (`void later(void); void dead(void) {`). After: anything but <c>;</c>, whitespace and
    /// comments past the final <c>}</c>.</summary>
    private static bool SharesLine(string[] lines, int s, int e)
    {
        if (s < 1 || e > lines.Length) return false;
        var first = CodeOnly(lines[s - 1]);
        for (var c = 0; c < first.Length; c++)
        {
            if (first[c] == '{') break;
            if (first[c] is ';' or '}' && first.AsSpan(c + 1).Trim().Length > 0) return true;
        }
        var last = CodeOnly(lines[e - 1]);
        var close = last.LastIndexOf('}');
        if (close < 0) return false;   // no closing brace on the last line: nothing after it to lose
        return last[(close + 1)..].Any(ch => !char.IsWhiteSpace(ch) && ch != ';');
    }

    /// <summary>Code (comments and literals already blanked) that reads as a function head opening its body:
    /// a parameter list's <c>)</c> and then <c>{</c> ending it, and no scope or control keyword leading it.</summary>
    private static bool IsFunctionHead(string code)
    {
        var t = code.Trim();
        if (!t.EndsWith('{')) return false;
        t = t[..^1].TrimEnd();
        if (!t.EndsWith(')') || t.IndexOf('(') is var open and (< 0 or 0)) return false;
        var first = new string(t.TakeWhile(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').ToArray());
        return first is not ("namespace" or "extern" or "struct" or "class" or "union" or "enum" or "typedef"
                             or "if" or "for" or "while" or "switch" or "do" or "else" or "return");
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

    /// <summary>
    /// True when the nearest code line above 1-based line <paramref name="start"/> (past blank and comment lines)
    /// leaves a declaration open: not part of a directive, and not ending in <c>;</c> <c>{</c> <c>}</c> or a label's
    /// <c>:</c>. Comments are recognized by line shape; a miss reads as code and keeps more (sound).
    /// </summary>
    private static bool UnterminatedAbove(string[] lines, int start)
    {
        var depth = 0;          // inside how many #if blocks that end above the definition (scanning upward)
        var branchEnd = false;  // the next code line is the last of an #if branch: it may be an attribute
        for (var k = start - 1; k >= 1; k--)
        {
            var t = lines[k - 1].Trim();
            if (t.StartsWith("/*", StringComparison.Ordinal) && t.IndexOf("*/", 2, StringComparison.Ordinal) is var close and >= 0)
                t = t[(close + 2)..].Trim();                               // `/* doc */ WEAK`: the code after it
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal)
                || t.StartsWith('*')) continue;
            if (t.EndsWith("*/", StringComparison.Ordinal) && !t.Contains("/*", StringComparison.Ordinal))
            {
                // The last line of a block comment: resume above the line that opens it.
                while (k > 1 && !lines[k - 1].Contains("/*", StringComparison.Ordinal)) k--;
                var at = lines[k - 1].IndexOf("/*", StringComparison.Ordinal);
                if (at < 0) return true;                                   // no opener: unsure, keep
                t = lines[k - 1][..at].Trim();                             // code before the comment
                if (t.Length == 0) continue;
            }
            // The last line of a multi-line #define doesn't start with '#'.
            var head = k;
            while (head > 1 && lines[head - 2].TrimEnd().EndsWith('\\')) head--;
            var ht = lines[head - 1].TrimStart();
            if (ht.StartsWith('#'))
            {
                var word = new string(ht[1..].TrimStart().TakeWhile(char.IsAsciiLetter).ToArray());
                // `#ifdef USE_RAM / RAMFUNC / #endif` above a head: what the #if block holds is attached too, and so
                // is what stands above the #if. Look through the block.
                if (word == "endif") { depth++; branchEnd = true; k = head; continue; }
                if (word is "else" or "elif" or "elifdef" or "elifndef")
                { if (depth > 0) { branchEnd = true; k = head; continue; } return false; }
                if (word is "if" or "ifdef" or "ifndef")
                { if (depth > 0) { depth--; branchEnd = depth > 0; k = head; continue; } return false; }
                // A pragma that applies to the next declaration (`#pragma location=`, `vector=`, `inline=forced`,
                // DATA_SECTION) belongs to the definition; the ones that set a mode for what follows don't.
                if (word == "pragma" && (depth == 0 || branchEnd) && !ModePragma(ht)) return true;
                if (depth > 0) { branchEnd = false; k = head; continue; }
                return false;
            }
            if (depth > 0 && !branchEnd) continue;   // earlier lines of an #if branch: not next to the definition
            // A trailing comment isn't what ends the line.
            if (t.EndsWith("*/", StringComparison.Ordinal) && t.LastIndexOf("/*", StringComparison.Ordinal) is > 0 and var bc) t = t[..bc].TrimEnd();
            if (t.IndexOf("//", StringComparison.Ordinal) is > 0 and var lc) t = t[..lc].TrimEnd();
            if (t.Length > 0 && t[^1] is not (';' or '{' or '}' or ':')) return true;
            if (depth == 0) return false;
            branchEnd = false;
        }
        return false;
    }

    /// <summary>A <c>#pragma</c> that sets a mode for everything after it rather than naming the next declaration.</summary>
    private static bool ModePragma(string directive)
    {
        var rest = directive[1..].TrimStart()["pragma".Length..].TrimStart();
        var word = new string(rest.TakeWhile(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').ToArray());
        return word is "once" or "pack" or "GCC" or "clang" or "warning" or "message" or "region" or "endregion"
            or "push_macro" or "pop_macro" or "diag_suppress" or "diag_default" or "diag_warning" or "diag_error"
            or "diag_remark" or "optimize" or "STDC" or "comment" or "mark" or "ident";
    }

    /// <summary>For each 1-based line (and one past the last): does it begin inside a comment — a block comment, or a
    /// <c>//</c> comment whose line ends in a backslash (the splice continues it onto the next line)? Strings and
    /// character literals are skipped, so a <c>/*</c> inside them opens nothing.</summary>
    private static bool[] CommentStateAtLineStart(string[] lines) => CommentStateAtLineStart(lines, out _);

    /// <param name="inLineComment">Per line: it begins inside a spliced // comment (and so is comment to its end).</param>
    private static bool[] CommentStateAtLineStart(string[] lines, out bool[] inLineComment)
    {
        var at = new bool[lines.Length + 2];
        inLineComment = new bool[lines.Length + 2];
        var inBlock = false;
        var inLine = false;   // a // comment spliced onto this line
        for (var i = 1; i <= lines.Length; i++)
        {
            at[i] = inBlock || inLine;
            inLineComment[i] = inLine;
            var line = lines[i - 1];
            if (inLine) { inLine = Spliced(line); continue; }
            var quote = '\0';
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (inBlock) { if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/') { inBlock = false; c++; } }
                else if (quote != '\0') { if (ch == '\\') c++; else if (ch == quote) quote = '\0'; }
                else if (ch is '"' or '\'') quote = ch;
                else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') { inLine = Spliced(line); break; }
                else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*') { inBlock = true; c++; }
            }
        }
        at[lines.Length + 1] = inBlock || inLine;
        inLineComment[lines.Length + 1] = inLine;
        return at;
    }

    /// <summary>Does the line end in a backslash (spliced onto the next one)? Trailing blanks and the CR are allowed,
    /// as compilers allow them.</summary>
    private static bool Spliced(string line) => line.TrimEnd().EndsWith('\\');

    /// <summary>A line's code when it starts inside a comment: what follows the comment's end (none when it doesn't
    /// end here), with comments and literals blanked like <see cref="CodeOnly"/>.</summary>
    private static string LineCode(string[] lines, bool[] inComment, bool[] inLineComment, int i)
    {
        var line = lines[i - 1];
        if (inLineComment[i]) return "";      // a spliced // comment holds the whole line
        if (inComment[i])
        {
            var close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0) return "";
            line = line[(close + 2)..];
        }
        return CodeOnly(line);
    }

    /// <summary>The text after a directive's introducer (<c>#</c>, <c>%:</c> or <c>??=</c>), past leading block comments
    /// on the line (comments are gone before directives are recognised); null when the line is no directive.</summary>
    private static string? DirectiveBody(string line)
    {
        var t = line.TrimStart();
        while (t.StartsWith("/*", StringComparison.Ordinal))
        {
            var close = t.IndexOf("*/", 2, StringComparison.Ordinal);
            if (close < 0) return null;
            t = t[(close + 2)..].TrimStart();
        }
        if (t.StartsWith('#')) return t[1..];
        if (t.StartsWith("%:", StringComparison.Ordinal)) return t[2..];
        if (t.StartsWith("??=", StringComparison.Ordinal)) return t[3..];
        return null;
    }

    /// <summary>True when every directive line in lines <paramref name="s"/>..<paramref name="e"/> is real (not inside a
    /// comment), part of #if structure, and leaves no comment open: directives stay when the span goes, so one that opens
    /// a comment the span's code closes (`#if FAST /* enable the` / `fast path */`) would swallow what follows.</summary>
    private static bool OnlyConditionalDirectives(string[] lines, bool[] inComment, int s, int e)
    {
        for (var i = s; i <= e; i++)
        {
            if (inComment[i])
            {
                // A directive-looking line inside a comment isn't one, and keeping it would make it one. Code after
                // the comment's end that is a directive (`*/ #if`) is too unusual to judge: keep the span.
                var raw = lines[i - 1];
                if (raw.TrimStart().StartsWith('#')) return false;
                var close = raw.IndexOf("*/", StringComparison.Ordinal);
                if (close >= 0 && DirectiveBody(raw[(close + 2)..]) is not null) return false;
                continue;
            }
            if (DirectiveBody(lines[i - 1]) is not { } d) continue;
            var word = new string(d.TrimStart().TakeWhile(char.IsAsciiLetter).ToArray());
            if (word is not ("if" or "ifdef" or "ifndef" or "elif" or "elifdef" or "elifndef" or "else" or "endif")) return false;
            var j = i;
            while (Spliced(lines[j - 1]) && j < lines.Length) j++;   // its continuation lines
            if (j > e || inComment[j + 1]) return false;            // it leaves a comment open (or runs past the span)
            i = j;
        }
        return true;
    }

    /// <summary>Remove the given 1-based inclusive line ranges from text, preserving the rest verbatim.</summary>
    private static string RemoveLineRanges(string text, List<(int Start, int End)> ranges,
                                           IReadOnlyList<(int Start, string Name)>? droppedFunctions = null)
    {
        var lines = text.Split('\n');
        var drop = DropLines(lines, ranges, null);
        if (droppedFunctions is { Count: > 0 }) DropPrototypes(lines, drop, droppedFunctions);
        var sb = new StringBuilder(text.Length);
        for (var i = 1; i <= lines.Length; i++)
        {
            if (drop[i]) continue;
            sb.Append(lines[i - 1]);
            if (i < lines.Length) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A removed function's forward declaration is left declaring a function never defined. For a static one
    /// (`static void helper(void);`, or spelled `STATIC`/`PRIVATE` through a macro) that is "declared 'static' but
    /// never defined", an error under -Werror. Remove the file-scope prototypes, one line or several, of each
    /// function whose definition was removed: a whole statement at brace depth 0, from a line start to a `;` that
    /// ends its line, with one declarator, no body, initializer or directive. Anything less plain stays (a warning
    /// at worst).
    /// </summary>
    private static void DropPrototypes(string[] lines, bool[] drop, IReadOnlyList<(int Start, string Name)> removed)
    {
        var names = removed.Where(s => s.Start >= 1 && s.Start <= lines.Length && drop[s.Start]).Select(s => s.Name)
                           .ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0) return;
        var inComment = CommentStateAtLineStart(lines, out var inLineComment);
        var depth = DepthAtLineStart(lines);
        var statementStart = true;   // the last code line ended a statement (or a block): a new one may start here
        for (var i = 1; i <= lines.Length; i++)
        {
            if (drop[i]) { statementStart = true; continue; }   // a removed definition ended with its `}`
            if (DirectiveBody(lines[i - 1]) is not null && !inComment[i])
            {
                while (Spliced(lines[i - 1]) && i < lines.Length) i++;   // its continuation lines
                continue;
            }
            var code = LineCode(lines, inComment, inLineComment, i).Trim();
            if (code.Length == 0) continue;
            var startsHere = statementStart && depth[i] == 0 && !inComment[i];
            // A file-scope line ending in `)` is a whole macro call (`DECLARE_COUNTER(hits)`) or a head whose `{` is
            // next: either way a prototype can start on the line after it.
            statementStart = code[^1] is ';' or '{' or '}' || (code[^1] == ')' && depth[i] == 0);
            if (!startsHere || code[^1] == '{') continue;

            // Collect the statement to its `;` — every line plain code at file scope, none removed or a directive.
            var j = i;
            var stmt = new StringBuilder(code);
            while (stmt.ToString().IndexOfAny(StatementEnd) < 0 && j < lines.Length && j - i < 32)
            {
                // A line ending in `)` is a whole head or call already (`DECLARE_COUNTER(hits)`, a macro that supplies
                // its own `;`): a prototype broken over lines breaks inside its parameters or before its name.
                if (stmt.ToString().TrimEnd().EndsWith(')')) { j = -1; break; }
                j++;
                if (drop[j] || inComment[j] || depth[j] != 0 || DirectiveBody(lines[j - 1]) is not null) { j = -1; break; }
                stmt.Append(' ').Append(LineCode(lines, inComment, inLineComment, j).Trim());
            }
            if (j < 0) continue;
            var text = stmt.ToString().Trim();
            if (j > i) { statementStart = text.Length > 0 && text[^1] is ';' or '{' or '}'; }
            if (!text.EndsWith(';') || text.IndexOf(';') != text.Length - 1 || text.IndexOfAny(NotInPrototype) >= 0
                || inComment[j + 1] || text.StartsWith("typedef", StringComparison.Ordinal) || TopLevelComma(text)) { i = j; continue; }
            var m = PrototypeName.Match(text);
            if (m.Success && names.Contains(m.Groups[1].Value))
                for (var k = i; k <= j; k++) drop[k] = true;
            i = j;
        }
    }

    private static readonly char[] StatementEnd = { ';', '{', '}' };

    /// <summary>A comma outside every parenthesis: a second declarator (`int a, f(void);`), which must stay.</summary>
    private static bool TopLevelComma(string code)
    {
        var paren = 0;
        foreach (var ch in code)
        {
            if (ch == '(') paren++;
            else if (ch == ')') paren--;
            else if (ch == ',' && paren == 0) return true;
        }
        return false;
    }

    /// <summary>Brace depth at the start of each 1-based line, carried across lines with comment (block and spliced
    /// //) and string state, so a `{` inside a doc comment doesn't count.</summary>
    private static int[] DepthAtLineStart(string[] lines)
    {
        var at = new int[lines.Length + 2];
        var depth = 0;
        var inBlock = false;
        var inLine = false;
        for (var i = 1; i <= lines.Length; i++)
        {
            at[i] = depth;
            var line = lines[i - 1];
            if (inLine) { inLine = Spliced(line); continue; }
            var quote = '\0';
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (inBlock) { if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/') { inBlock = false; c++; } }
                else if (quote != '\0') { if (ch == '\\') c++; else if (ch == quote) quote = '\0'; }
                else if (ch is '"' or '\'') quote = ch;
                else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') { inLine = Spliced(line); break; }
                else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*') { inBlock = true; c++; }
                else if (ch == '{') depth++;
                else if (ch == '}') depth = Math.Max(0, depth - 1);
            }
        }
        at[lines.Length + 1] = depth;
        return at;
    }

    private static readonly char[] NotInPrototype = { '{', '}', '=', '#' };
    // The declared name: the identifier right before the parameter list's '('. Anchored: before it only declaration
    // words (storage, qualifiers, types, attribute macros), pointers and attributes, so a statement that holds
    // anything else (a macro call before it) is no prototype.
    private static readonly Regex PrototypeName = new(
        @"^(?:(?:__attribute__\s*\(\((?:[^()]|\([^()]*\))*\)\)|__declspec\s*\([^()]*\)|\[\[[^\]]*\]\]|[A-Za-z_]\w*\b|[*&])\s*)*?"
        + @"([A-Za-z_]\w*)\s*\([^()]*(?:\([^()]*\)[^()]*)*\)\s*(?:__attribute__\s*\(\(.*\)\)\s*)?;$", RegexOptions.Compiled);

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
        var inComment = CommentStateAtLineStart(lines, out var inLineComment);
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

            // A comment the span starts inside, or one its last line opens and a later line closes: removing the
            // lines would cut it in half.
            if (s < 1 || e > lines.Length || inComment[s] || inComment[e + 1]) continue;

            // A backslash splices lines: the span's last line ending in one (`} // see C:\tmp\`) carries the next line
            // along, and a span starting right after a spliced line is that line's tail. Removing either way would turn
            // comment into code or code into comment.
            if (Spliced(lines[e - 1]) || (s > 1 && Spliced(lines[s - 2]))) continue;

            // Directives inside the span stay (below), so only #if structure may be there: an #include inside a
            // table or body (X-macro rows) left at file scope breaks the build, and so would a #define, #undef or
            // #pragma moved out of its context. A directive-looking line inside a comment isn't one at all, and
            // keeping it would make it one.
            if (!OnlyConditionalDirectives(lines, inComment, s, e)) continue;

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
                var onlyDirectivesBetween = true;   // nothing but directives, blanks and comments between the two heads
                for (var k = candidate + 1; candidate != s && k < s; k++)
                {
                    if (DirectiveBody(lines[k - 1]) is not null && !inComment[k])
                    {
                        sharedUnderPreproc = true;
                        while (Spliced(lines[k - 1]) && k < s - 1) k++;   // its continuation lines
                        continue;
                    }
                    if (LineCode(lines, inComment, inLineComment, k).Trim().Length > 0) { onlyDirectivesBetween = false; break; }
                }
                // (a) dual-signature: the orphaned open-brace line is a FUNCTION HEAD (its code, comments and strings
                // gone, ends in a parameter list's `)` and then `{`) sharing the drop's body. A `{` / `namespace X {` /
                // `extern "C" {` / `class X {` scope-opener is no head — extending up into it would delete the ENCLOSING
                // scope's brace (and any complete kept definition between), shattering the file. That false positive is
                // what a `namespace pugi {` immediately followed by `#ifndef` hits (real pugixml xpath_exception bug), and
                // a `namespace drv {  // internals (private)` whose comment holds the parens. When unsure, don't extend
                // (keep more = sound).
                if (sharedUnderPreproc && onlyDirectivesBetween && IsFunctionHead(LineCode(lines, inComment, inLineComment, candidate)))
                    start = candidate; // (a) dual-signature — extend up
                // else (b): enclosing scope, leave start = s; remove the balanced span alone.
            }

            // A code line above the span that doesn't end a declaration belongs to this one: an attribute macro
            // on its own line (`WEAK` / `NORET` / `SECTION(".x")` above the head) that the parse left outside the
            // span. Removing the span alone strands it on whatever follows (a compile error, or the next
            // definition silently weak). Keep the definition whole (sound; EmitClosure sees it written).
            if (UnterminatedAbove(lines, start)) continue;
            for (var i = Math.Max(1, start); i <= e && i <= lines.Length; i++)
                drop[i] = true;
            applied?.Add((s, e));
        }

        // Never delete a preprocessor directive (or its backslash-continuation) even inside a dropped
        // function: removing an #if/#endif would unbalance conditionals. The function's code is still
        // removed; a stray #if/#endif left behind is a harmless empty conditional.
        for (var i = 1; i <= lines.Length; i++)
        {
            if (!drop[i] || inComment[i] || DirectiveBody(lines[i - 1]) is null) continue;
            drop[i] = false;
            var j = i;
            while (j <= lines.Length && Spliced(lines[j - 1]))
            {
                j++;
                if (j <= lines.Length) drop[j] = false;
            }
        }
        return drop;
    }
}
