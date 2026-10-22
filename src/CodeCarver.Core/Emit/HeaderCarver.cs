using System.Text;

namespace CodeCarver.Core.Emit;

/// <summary>Outcome of carving big headers: bytes before/after and how many #defines were kept vs dropped.</summary>
public sealed record HeaderCarveResult(long BytesBefore, long BytesAfter, int DefinesKept, int DefinesDropped)
{
    /// <summary>Reads of the big headers the fixpoint took (each pass reads every one).</summary>
    public int Passes { get; init; }

    /// <summary>Names defined in the carved headers that are paste macros, and that have a forwarding definition there (a
    /// function-like define with a call in its body that pastes no parameter itself).</summary>
    public int PasteMacros { get; init; }
    public int ForwardingMacros { get; init; }

    /// <summary>Milliseconds per part: seed, bigDefines, pasteCalls, fixpoint, emit.</summary>
    public IReadOnlyList<(string Part, long Ms)> PartMs { get; init; } = Array.Empty<(string, long)>();
}

/// <summary>
/// EXPERIMENTAL header carving: strips unused <c>#define</c> macros from the giant auto-generated register
/// headers that <see cref="FileTreeEmitter"/> keeps whole (multi-GB files past <c>--max-parse-bytes</c>).
/// A carve that needs a handful of registers shouldn't drag in a million unrelated ones.
///
/// It is SOUND-by-construction and STREAMING (bounded memory, never loads the file):
///   • Everything that is NOT an object/function-like <c>#define</c> is kept verbatim — include guards,
///     <c>#if/#endif</c>, <c>#include</c>, typedefs, structs, comments. So conditional structure and types
///     are never disturbed.
///   • A <c>#define NAME …</c> is kept iff NAME is in the transitively-needed set; otherwise dropped
///     (with its backslash-continuation lines).
///   • "Needed" seeds from every identifier used by the kept code, then grows to a fixpoint: if a needed
///     define's body references other names, those become needed too (register offsets built from a base
///     address, masks built from shifts). Identifiers in the header's own <c>#if/#ifdef/#elif</c>
///     conditions are always needed, so branch selection can't change.
///
/// Over-keeping is the safe side of "must build": anything uncertain (any non-#define line, any name we
/// can't rule out) is kept. Still EXPERIMENTAL — always build-verify the result.
/// </summary>
public static class HeaderCarver
{
    /// <summary>
    /// Carve the given big headers (relative paths) in place under <paramref name="outDir"/>, dropping
    /// #defines not transitively needed by the other kept files there. Returns aggregate size/keep stats.
    /// </summary>
    /// <param name="extraNames">Names used where the carved tree can't show them: an SDK header outside it that
    /// tests a macro, the build's command line (<c>-DBAUD=UART_DIV_115200</c>).</param>
    /// <param name="partDone">Called as each part ends with its numbers (<c>&lt;part&gt;.ms</c>, <c>.cpuMs</c> (process CPU), <c>.allocatedMb</c>, <c>.privateMb</c>, <c>.liveMb</c>):
    /// a memory stop later in the carve still has them.</param>
    public static HeaderCarveResult Carve(string outDir, IReadOnlyCollection<string> bigHeaderRels, IEnumerable<string>? extraNames = null,
                                          Action<string, long>? partDone = null)
    {
        var bigSet = new HashSet<string>(bigHeaderRels, StringComparer.OrdinalIgnoreCase);
        var bigFull = bigHeaderRels.Select(r => Path.Combine(outDir, r)).ToList();
        // Files are read on many threads (one at a time, a work-scale output of 47 GB and GBs of big headers read
        // again each pass took hours); what they found is folded in in file order, so the result is the same.
        CodeCarver.Core.Util.Activity.Begin("files read for the header carve");
        var partMs = new List<(string, long)>();
        var partClock = System.Diagnostics.Stopwatch.StartNew();
        // What each part holds as it ends: private memory, then the heap after a full collection (work's max stage stopped
        // at 20 GB in the fixpoint, 3.9 GB of it from the steps before: which part kept the rest?).
        long partCpuAt, partAllocatedAt = GC.GetTotalAllocatedBytes();
        using (var p = System.Diagnostics.Process.GetCurrentProcess()) partCpuAt = (long)p.TotalProcessorTime.TotalMilliseconds;
        void PartDone(string part)
        {
            var ms = partClock.ElapsedMilliseconds;
            partMs.Add((part, ms));
            if (partDone is not null)
            {
                partDone($"{part}.ms", ms);
                var allocated = GC.GetTotalAllocatedBytes();
                partDone($"{part}.allocatedMb", (allocated - partAllocatedAt) / (1024 * 1024));
                partAllocatedAt = allocated;
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    var cpu = (long)p.TotalProcessorTime.TotalMilliseconds;
                    partDone($"{part}.cpuMs", cpu - partCpuAt);
                    partDone($"{part}.privateMb", p.PrivateMemorySize64 / (1024 * 1024));
                }
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                partDone($"{part}.liveMb", GC.GetTotalMemory(false) / (1024 * 1024));
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) partCpuAt = (long)p.TotalProcessorTime.TotalMilliseconds;   // not the collection
            }
            partClock.Restart();
        }

        // 1. Seed: every identifier used by kept code that ISN'T one of the carved headers. That is the set
        //    of names the compiled output could reference; a define outside its closure is unreachable.
        //    ALSO collect token-paste fragments: if code builds a name with `##` (`REG_##n##_BASE`), the
        //    concrete define (`REG_0_BASE`) never appears literally, so we'd wrongly drop it. Any define
        //    whose name a fragment could form is kept (sound over-approximation — the paste blind spot).
        //    Every TEXT file in the output counts — assembly, linker scripts, .inc tables, scripts — not only C:
        //    a define used only by startup.S or a linker script is still needed (review H1). The caller runs this
        //    after the infrastructure copy so those files are present. Binary files are skipped.
        var needed = new HashSet<string>(extraNames ?? Array.Empty<string>(), StringComparer.Ordinal);
        var fragments = new HashSet<string>(StringComparer.Ordinal);
        var pastes = new Pastes();
        // Hidden files count (a .config, a dot-named generated header): only the system ones are skipped.
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var candidates = Directory.EnumerateFiles(outDir, "*", walk)
            .Where(f => !bigSet.Contains(Path.GetRelativePath(outDir, f).Replace('\\', '/'))).ToList();
        var seedFiles = new List<string>();
        string Learned() => $"{pastes.Counts.Pasters:N0} paste macros, {pastes.Counts.Callers:N0} forwarding macros, {needed.Count:N0} names, {fragments.Count:N0} paste pieces";
        CodeCarver.Core.Util.Activity.Doing("reading the kept files for names and paste macros", Learned);
        foreach (var (i, seen) in CodeCarver.Core.Util.ReadAhead.Ordered(candidates, SeedScan, CodeCarver.Core.Util.ReadAhead.Window))
        {
            if (seen.Names is null) continue;   // binary
            seedFiles.Add(candidates[i]);
            needed.UnionWith(seen.Names);
            // Only a line with a '#' can define or paste.
            foreach (var line in seen.HashLines)
            {
                if (Pastes.ParseDef(line) is { } def) pastes.Learn(def, big: false);
                Pastes.AddLiteralPieces(line, fragments);
                pastes.LearnObject(line);
            }
        }
        PartDone("seed");
        // A paste macro's own call sites name the pieces (`CAT(UART, 2)` builds UART2): learned macros first,
        // in the big headers too, then the calls.
        CodeCarver.Core.Util.Activity.Doing("learning paste macros from the big headers", Learned);
        foreach (var (_, defines) in CodeCarver.Core.Util.ReadAhead.Ordered(bigFull, PasteDefines, CodeCarver.Core.Util.ReadAhead.Window))
            foreach (var def in defines) pastes.Learn(def, big: true);   // its literal pieces count once it's wanted
        pastes.Resolve();
        PartDone("bigDefines");
        CodeCarver.Core.Util.Activity.Doing("reading paste-macro calls in the kept files", Learned);
        if (pastes.Any)
            foreach (var (_, found) in CodeCarver.Core.Util.ReadAhead.Ordered(seedFiles, f => ScanCalls(pastes, f), CodeCarver.Core.Util.ReadAhead.Window))
                foreach (var piece in found) pastes.AddPiece(piece, fragments);

        // A define is wanted if the code references its name, OR a paste fragment could build that name. Only the
        // lengths some fragment has are tried (every prefix and suffix of every define name was a string each).
        // A prefix or suffix is looked up by its hash first, a string made only when a fragment has that hash.
        var fragmentLengths = Array.Empty<int>();
        var fragmentHashes = new HashSet<int>();
        void FreezeFragments()
        {
            fragmentLengths = fragments.Select(f => f.Length).Distinct().Order().ToArray();
            fragmentHashes = fragments.Select(f => f.GetHashCode()).ToHashSet();
        }
        bool IsFragment(ReadOnlySpan<char> s) => fragmentHashes.Contains(string.GetHashCode(s)) && fragments.Contains(s.ToString());
        bool Wanted(string name) => needed.Contains(name) || ReadBySystemHeaders(name) || PieceLength(name) > 0;
        // The length of the shortest piece the name starts or ends with, 0 when none.
        int PieceLength(string name)
        {
            if (fragments.Count <= 64)
                return fragments.Where(fr => fr.Length > 0 && (name.StartsWith(fr, StringComparison.Ordinal) || name.EndsWith(fr, StringComparison.Ordinal)))
                                .Select(fr => fr.Length).DefaultIfEmpty(0).Min();
            foreach (var k in fragmentLengths)
            {
                if (k > name.Length) break;
                if (k > 0 && (IsFragment(name.AsSpan(0, k)) || IsFragment(name.AsSpan(name.Length - k)))) return k;
            }
            return 0;
        }
        // Why the emit kept each define: its name is needed, the compiler's headers read it, or the shortest piece it
        // starts or ends with (1, 2, 3, 4 or more characters). A carve that drops nothing says which.
        long keptByName = 0, keptBySystem = 0;
        var keptByPiece = new long[5];
        bool WantedCounted(string name)
        {
            if (needed.Contains(name)) { Interlocked.Increment(ref keptByName); return true; }
            if (ReadBySystemHeaders(name)) { Interlocked.Increment(ref keptBySystem); return true; }
            var k = PieceLength(name);
            if (k == 0) return false;
            Interlocked.Increment(ref keptByPiece[Math.Min(k, 4)]);
            return true;
        }

        // 2. Fixpoint: grow `needed` with the bodies of wanted defines and all #if-condition identifiers.
        //    Terminates because `needed` only grows and is finite. Passes ≈ the deepest define-dependency chain
        //    (small for register maps).
        //    The header's own non-#define lines (typedefs, enums, inline functions) are kept verbatim, so the
        //    names they use are needed too; and a kept define that pastes (##) can form further names.
        //    Headers are read in batches on many threads against what is needed so far, and what they hand back is
        //    folded in in order. The first pass takes every kept line; a later one only the defines wanted, but those
        //    taken two passes back (a name taken in a pass may have been passed over in a batch before it became
        //    wanted; the pass after takes every line of it). When the paste macros grew, a call or a define taken
        //    before can build more: the next pass takes every line again.
        PartDone("pasteCalls");
        var batches = Batches(bigFull, 256L << 20);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var takenBefore = new HashSet<string>(StringComparer.Ordinal);
        var passes = 0;
        for (var all = true; ;)
        {
            passes++;
            CodeCarver.Core.Util.Activity.Doing($"reading the big headers for what they need, pass {passes}", Learned);
            var grew = false;
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var pastesAt = pastes.Version;
            foreach (var batch in batches)
            {
                FreezeFragments();
                var skip = all ? null : done;
                // Nothing is added to what is needed, the pieces or the paste macros while a batch is read, so each
                // reader hands back only what its file adds, not its lines: work's first pass held its kept lines' text,
                // a batch at a time, and stopped at 20 GB.
                foreach (var take in CodeCarver.Core.Util.ReadAhead.Each(batch, path => Take(path, all, skip, Wanted, needed, pastes)))
                {
                    var n = fragments.Count;
                    // Kept verbatim (not a define) or kept as wanted: what it names is needed, and what its pastes
                    // and paste-macro calls can build.
                    foreach (var name in take.Names) grew |= needed.Add(name);
                    foreach (var piece in take.LiteralPieces) fragments.Add(piece);
                    foreach (var (name, first, last) in take.Defines) { pastes.ExpandDefine(name, first, last, fragments); taken.Add(name); }
                    foreach (var piece in take.CallPieces) pastes.AddPiece(piece, fragments);
                    grew |= fragments.Count != n;
                }
            }
            all = pastes.Version != pastesAt;
            grew |= all;
            done.UnionWith(takenBefore);
            takenBefore = taken;
            if (!grew) break;
        }

        PartDone("fixpoint");
        // 3. Emit: rewrite each header, dropping #defines whose name isn't wanted (and their continuations).
        CodeCarver.Core.Util.Activity.Doing("rewriting the big headers", Learned);
        FreezeFragments();
        var rewritten = CodeCarver.Core.Util.ReadAhead.Each(bigFull, path =>
        {
            var before = new FileInfo(path).Length;
            var tmp = path + ".carve.tmp";
            int kept = 0, dropped = 0;
            var pasteNames = new HashSet<string>(StringComparer.Ordinal);
            RewriteDroppingUnneeded(path, tmp, WantedCounted, ref kept, ref dropped, name => { if (pastes.IsPaster(name)) pasteNames.Add(name); });
            // A read-only source copy (Perforce) must not make the replace throw.
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            File.Move(tmp, path);
            return (Before: before, After: new FileInfo(path).Length, Kept: kept, Dropped: dropped, Pasters: pasteNames);
        });
        PartDone("emit");
        if (partDone is not null)
        {
            partDone("keptByName", keptByName);
            partDone("keptBySystemHeaders", keptBySystem);
            for (var k = 1; k <= 4; k++) partDone(k < 4 ? $"keptByPiece{k}" : "keptByPiece4plus", keptByPiece[k]);
            partDone("pieces", fragments.Count);
            for (var k = 1; k <= 3; k++) partDone($"pieces{k}", fragments.Count(f => f.Length == k));
        }
        CodeCarver.Core.Util.Activity.Doing(null);
        return new HeaderCarveResult(rewritten.Sum(r => r.Before), rewritten.Sum(r => r.After), rewritten.Sum(r => r.Kept), rewritten.Sum(r => r.Dropped))
        {
            Passes = passes, PartMs = partMs,
            PasteMacros = rewritten.SelectMany(r => r.Pasters).Distinct(StringComparer.Ordinal).Count(),
            ForwardingMacros = pastes.BigForwarders,
        };
    }

    /// <summary>A seed file's identifiers and its logical lines with a '#' (null names: a binary file).</summary>
    private static (HashSet<string>? Names, List<string> HashLines) SeedScan(string path)
    {
        if (LooksBinary(path)) return (null, new List<string>());
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hashLines = new List<string>();
        foreach (var line in LogicalLines(path))
        {
            AddIdentifiers(names, line);
            if (line.Contains('#')) hashLines.Add(line);
        }
        return (names, hashLines);
    }

    /// <summary>What a big header's #defines say for paste macros: the paste macros are learned from them.</summary>
    private static List<Pastes.Def> PasteDefines(string path)
    {
        var list = new List<Pastes.Def>();
        foreach (var (_, text, kind) in EnumerateLogicalLines(path))
            if (kind == LineKind.Define && Pastes.ParseDef(text) is { } def) list.Add(def);
        return list;
    }

    /// <summary>The pieces a kept file's paste-macro calls pass.</summary>
    private static List<string> ScanCalls(Pastes pastes, string path)
    {
        var calls = new CallScanner(pastes);
        foreach (var line in LogicalLines(path)) calls.Feed(line);
        calls.Flush();
        return calls.Pieces;
    }

    /// <summary>What a pass takes from one big header's lines (every non-define line when <paramref name="all"/>, and each
    /// wanted define but those named in <paramref name="skip"/>): the names they use not yet <paramref name="needed"/>,
    /// their literal paste pieces, the pieces their paste-macro calls pass, and each define's name with the names at the
    /// edges of its body (an object-like define's expansion can be a piece). Read on many threads at once: nothing it
    /// reads changes until the batch is read.</summary>
    private static FileTake Take(string path, bool all, HashSet<string>? skip, Func<string, bool> wanted, HashSet<string> needed, Pastes pastes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var literal = new HashSet<string>(StringComparer.Ordinal);
        var defines = new List<(string, string?, string?)>();
        var calls = pastes.Any ? new CallScanner(pastes) : null;
        foreach (var (name, text, kind) in EnumerateLogicalLines(path))
        {
            if (kind != LineKind.Define)
            {
                if (!all) continue;
                AddNewIdentifiers(names, needed, text);
                Pastes.AddLiteralPieces(text, literal);
                calls?.Feed(text);
                continue;
            }
            if (name is null || skip?.Contains(name) == true || !wanted(name)) continue;
            // Not its own name: it is wanted already, and stays so (the pieces only grow). Held as needed, it was one more
            // name per kept define, and a define kept for a piece looked kept by name.
            AddNewIdentifiers(names, needed, text, except: name);
            Pastes.AddLiteralPieces(text, literal);
            // Only a define whose body names a paste macro passes it pieces (the paste macros are all known by now, a
            // forwarding macro that reaches one among them): the rest skip the scanner's copies of their text.
            if (calls is not null && pastes.NamesAPaster(text)) calls.Define(text);
            // The edges of its body only matter when an argument expanded its name (see ExpandDefine); a name expanded
            // later in the pass grows the paste state, and the pass after reads every define again.
            var (first, last) = pastes.IsExpanded(name) && Pastes.ObjectBody(text, out _) is { } body ? BoundaryIdents(body) : (null, null);
            // The name as already held (needed, or this file's new names), not one more copy of it per define.
            defines.Add((needed.TryGetValue(name, out var held) || names.TryGetValue(name, out held) ? held : name, first, last));
        }
        calls?.Flush();
        return new FileTake(names, literal, defines, calls?.Pieces ?? new List<string>());
    }

    private sealed record FileTake(HashSet<string> Names, HashSet<string> LiteralPieces, List<(string Name, string? First, string? Last)> Defines,
                                   List<string> CallPieces);

    /// <summary>The headers in runs of about <paramref name="bytes"/> each, in order.</summary>
    private static List<List<string>> Batches(List<string> paths, long bytes)
    {
        var batches = new List<List<string>>();
        var cur = new List<string>();
        long size = 0;
        foreach (var p in paths)
        {
            cur.Add(p);
            size += new FileInfo(p).Length;
            if (size >= bytes) { batches.Add(cur); cur = new List<string>(); size = 0; }
        }
        if (cur.Count > 0) batches.Add(cur);
        return batches;
    }

    private enum LineKind { Define, Conditional, Other }

    /// <summary>
    /// Stream a header yielding one entry per logical line (continuations joined): a <c>#define</c> with its
    /// name and full text, a conditional (<c>#if/#ifdef/#ifndef/#elif</c>) with its whole condition, or any
    /// other line. Bounded memory; Latin-1 so no byte can fail to decode.
    /// </summary>
    private static IEnumerable<(string? Name, string Text, LineKind Kind)> EnumerateLogicalLines(string path)
    {
        using var r = new StreamReader(path, Encoding.Latin1);
        string? line;
        var inComment = Comment.None;
        while ((line = r.ReadLine()) is not null)
        {
            var commented = inComment != Comment.None;   // a #define inside a comment is comment text
            var (isDefine, isCond) = commented ? (false, false) : Directive(line);
            inComment = CommentOpenAfter(line, inComment);
            // A line is copied only when continuation lines join it (each line was copied three more times).
            var full = line;
            if ((isDefine || isCond) && EndsWithContinuation(line))
            {
                var sb = new StringBuilder(line);
                while (EndsWithContinuation(line) && (line = r.ReadLine()) is not null)
                {
                    sb.Append('\n').Append(line);
                    inComment = CommentOpenAfter(line, inComment);
                }
                full = sb.ToString();
            }
            // A define that leaves a comment open is kept whatever it names (the rewrite can't cut the comment):
            // read it as plain text, so what it names counts.
            yield return isDefine && inComment == Comment.None ? (DefineName(full), full, LineKind.Define)
                 : isCond ? (null, full, LineKind.Conditional)
                 : (null, full, LineKind.Other);
        }
    }

    /// <summary>A file's lines with backslash-continuations joined, streamed. Latin-1, or what a byte-order mark says
    /// (a UTF-16 source an MSVC-style toolchain compiles).</summary>
    private static IEnumerable<string> LogicalLines(string path)
    {
        var sb = new StringBuilder();
        using var r = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (EndsWithContinuation(line)) { sb.Append(line).Append('\n'); continue; }
            if (sb.Length == 0) { yield return line; continue; }
            sb.Append(line);
            yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Is a comment open after <paramref name="line"/>, given whether one was open before it? A block
    /// comment, or a line comment ending in a backslash: the splice makes the next line comment text too
    /// (<c>// path: C:\vendor\regs\</c>). String and character literals open nothing.</summary>
    private static Comment CommentOpenAfter(string line, Comment open)
    {
        if (open == Comment.Line) return EndsWithContinuation(line) ? Comment.Line : Comment.None;   // all comment text
        var inBlock = open == Comment.Block;
        var quote = '\0';
        for (var c = 0; c < line.Length; c++)
        {
            var ch = line[c];
            if (inBlock) { if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/') { inBlock = false; c++; } }
            else if (quote != '\0') { if (ch == '\\') c++; else if (ch == quote) quote = '\0'; }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') return EndsWithContinuation(line) ? Comment.Line : Comment.None;
            else if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*') { inBlock = true; c++; }
        }
        return inBlock ? Comment.Block : Comment.None;
    }

    /// <summary>What comment is open at a line's end: none, a block comment, or a line comment a backslash continues.</summary>
    private enum Comment { None, Block, Line }

    /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/>, omitting #defines the predicate rejects.
    /// Byte-transparent (Latin-1) and every kept line keeps its own line ending (review E1). A line inside a comment
    /// (a block comment, or a line comment a trailing backslash continues) is text, and a define that leaves a
    /// comment open is kept: dropping it would cut the comment, or splice the next line into one.</summary>
    private static void RewriteDroppingUnneeded(string src, string dst, Func<string, bool> wanted, ref int kept, ref int dropped, Action<string> defined)
    {
        using var w = new StreamWriter(dst, false, Encoding.Latin1);
        using var e = ReadLinesWithEol(src).GetEnumerator();
        var inComment = Comment.None;
        var define = new List<(string Line, string Eol)>();
        while (e.MoveNext())
        {
            var (line, eol) = e.Current;
            if (inComment == Comment.None && IsDefineDirective(line.TrimStart()))
            {
                // Read the whole (possibly multi-line) define, then decide.
                define.Clear();
                define.Add((line, eol));
                inComment = CommentOpenAfter(line, inComment);
                var cont = line;
                while (EndsWithContinuation(cont) && e.MoveNext())
                {
                    (cont, eol) = e.Current;
                    define.Add((cont, eol));
                    inComment = CommentOpenAfter(cont, inComment);
                }
                var name = DefineName(line);
                if (name is not null) defined(name);
                var drop = name is not null && inComment == Comment.None && !wanted(name);
                if (drop) { dropped++; continue; }
                kept++;
                foreach (var (l, el) in define) { w.Write(l); w.Write(el); }
                continue;
            }
            inComment = CommentOpenAfter(line, inComment);
            w.Write(line);
            w.Write(eol);
        }
    }

    /// <summary>Lines with their exact terminators ("\r\n", "\n", "\r" or "" at EOF), Latin-1, streaming.</summary>
    private static IEnumerable<(string Line, string Eol)> ReadLinesWithEol(string path)
    {
        using var r = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        var sb = new StringBuilder();
        int c;
        while ((c = r.Read()) >= 0)
        {
            if (c == '\n') { yield return (sb.ToString(), "\n"); sb.Clear(); continue; }
            if (c == '\r')
            {
                if (r.Peek() == '\n') { r.Read(); yield return (sb.ToString(), "\r\n"); }
                else yield return (sb.ToString(), "\r");
                sb.Clear();
                continue;
            }
            sb.Append((char)c);
        }
        if (sb.Length > 0) yield return (sb.ToString(), "");
    }

    /// <summary>A NUL byte in the first 8 KB: treat as binary (no identifiers to seed from). UTF-16/32 text with a
    /// byte-order mark is full of NULs but is text: <see cref="LogicalLines"/> decodes it by its mark.</summary>
    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8192];
            var n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
            if (n >= 4 && buf[0] == 0 && buf[1] == 0 && buf[2] == 0xFE && buf[3] == 0xFF) return false;
            return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// A macro the compiler's own headers read, which the tree never names: a reserved name (<c>_GNU_SOURCE</c>,
    /// <c>__STDC_WANT_LIB_EXT1__</c>) or a well-known configuration macro (<c>NDEBUG</c> for <c>&lt;assert.h&gt;</c>).
    /// Dropping its #define changes what the build compiles (review H1).
    /// </summary>
    internal static bool ReadBySystemHeaders(string name) =>
        CodeCarver.Core.Preprocess.MacroTable.IsReserved(name) || SystemConfigMacros.Contains(name);

    private static readonly HashSet<string> SystemConfigMacros = new(StringComparer.Ordinal)
    {
        "NDEBUG", "DEBUG", "WIN32_LEAN_AND_MEAN", "VC_EXTRALEAN", "NOMINMAX", "STRICT", "UNICODE", "NOGDI",
        "NOUSER", "NOCRYPT", "NOSERVICE", "NOMCX", "NOIME", "WINVER", "NTDDI_VERSION",
    };

    /// <summary>Is <paramref name="line"/> a <c>#define</c>, or a conditional directive?</summary>
    private static (bool Define, bool Conditional) Directive(string line)
    {
        var t = line.AsSpan().TrimStart();
        if (IsDefineDirective(t)) return (true, false);
        return (false, IsConditionalDirective(t));
    }

    private static bool IsDefineDirective(ReadOnlySpan<char> trimmed) => StartsWithHash(trimmed, "define");

    private static bool IsConditionalDirective(ReadOnlySpan<char> trimmed) =>
        StartsWithHash(trimmed, "if") || StartsWithHash(trimmed, "ifdef") ||
        StartsWithHash(trimmed, "ifndef") || StartsWithHash(trimmed, "elif") ||
        StartsWithHash(trimmed, "elifdef") || StartsWithHash(trimmed, "elifndef");

    /// <summary>Matches <c>#</c> (optional spaces) then the keyword as a whole word — e.g. <c>#  define</c>.</summary>
    private static bool StartsWithHash(ReadOnlySpan<char> trimmed, string keyword)
    {
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;
        var i = 1;
        while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t')) i++;
        if (i + keyword.Length > trimmed.Length) return false;
        for (var k = 0; k < keyword.Length; k++)
            if (trimmed[i + k] != keyword[k]) return false;
        var after = i + keyword.Length;
        return after == trimmed.Length || !IsIdentChar(trimmed[after]); // whole word (#if not #ifdef)
    }

    /// <summary>The macro name of a <c>#define NAME</c> / <c>#define NAME(args)</c> line, or null.</summary>
    private static string? DefineName(string text)
    {
        var i = text.IndexOf('#');
        if (i < 0) return null;
        i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        i += "define".Length;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        var start = i;
        while (i < text.Length && IsIdentChar(text[i])) i++;
        return i > start ? text[start..i] : null;
    }

    private static bool EndsWithContinuation(string line)
    {
        var t = line.AsSpan().TrimEnd();
        return t.Length > 0 && t[^1] == '\\';
    }

    /// <summary>
    /// The identifier pieces next to each <c>##</c> paste (<c>REG_ ## n ## _BASE</c> → "REG_", "n", "_BASE"),
    /// spaces around the <c>##</c> allowed; a number is never a piece. <c>AfterComma</c>: the piece follows
    /// <c>, ##</c> — GNU's <c>, ##__VA_ARGS__</c>, which deletes the comma for an empty argument list and pastes nothing.
    /// </summary>
    private static IEnumerable<(string Piece, bool AfterComma)> PastePieces(string text)
    {
        var i = text.IndexOf("##", StringComparison.Ordinal);
        while (i >= 0)
        {
            var e = i;
            while (e > 0 && text[e - 1] is ' ' or '\t') e--;
            var b = e;
            while (b > 0 && IsIdentChar(text[b - 1])) b--;
            if (e > b && IsIdentStart(text[b])) yield return (text[b..e], false);
            var s = i + 2;
            while (s < text.Length && text[s] is ' ' or '\t') s++;
            var j = s;
            while (j < text.Length && IsIdentChar(text[j])) j++;
            if (j > s && IsIdentStart(text[s])) yield return (text[s..j], e > 0 && text[e - 1] == ',');
            i = text.IndexOf("##", i + 2, StringComparison.Ordinal);
        }
    }

    private enum Tok { End, Ident, Other }

    /// <summary>The next preprocessing token of <paramref name="text"/> at or after <paramref name="i"/>, past spaces,
    /// line splices and comments: an identifier, or another token (a pp-number such as <c>0x10UL</c>, a string or
    /// character literal, a punctuator).</summary>
    private static Tok NextToken(string text, ref int i, out int start)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '\\' or '\f' or '\v') { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? text.Length : e + 2;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = text.Length; break; }
            start = i;
            if (IsIdentStart(c)) { while (i < text.Length && IsIdentChar(text[i])) i++; return Tok.Ident; }
            if (StartsNumber(text, i)) { i = PpNumberEnd(text, i); return Tok.Other; }
            if (c is '"' or '\'') { i = QuoteEnd(text, i); return Tok.Other; }
            i++;
            return Tok.Other;
        }
        start = i;
        return Tok.End;
    }

    private static bool StartsNumber(string t, int i) =>
        char.IsAsciiDigit(t[i]) || (t[i] == '.' && i + 1 < t.Length && char.IsAsciiDigit(t[i + 1]));

    /// <summary>The end of the pp-number starting at <paramref name="i"/> (<c>0x10UL</c>, <c>1.5e+3f</c>).</summary>
    private static int PpNumberEnd(string t, int i)
    {
        i++;
        while (i < t.Length)
        {
            var c = t[i];
            if ((c is '+' or '-') && t[i - 1] is 'e' or 'E' or 'p' or 'P') { i++; continue; }
            if (IsIdentChar(c) || c == '.') { i++; continue; }
            break;
        }
        return i;
    }

    /// <summary>The end of the string or character literal starting at <paramref name="i"/> (or of its line).</summary>
    private static int QuoteEnd(string t, int i)
    {
        var q = t[i++];
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '\\') { i += 2; continue; }
            i++;
            if (c == q || c == '\n') break;
        }
        return Math.Min(i, t.Length);
    }

    /// <summary>The identifier tokens of <paramref name="text"/> (not those in comments, literals or numbers).</summary>
    private static IEnumerable<string> Identifiers(string text)
    {
        var i = 0;
        Tok k;
        while ((k = NextToken(text, ref i, out var s)) != Tok.End)
            if (k == Tok.Ident) yield return text[s..i];
    }

    /// <summary>A define body's first and last token, each when it is an identifier (<c>USART</c>: both USART;
    /// <c>((t)U)</c>: neither).</summary>
    private static (string? First, string? Last) BoundaryIdents(string body)
    {
        string? first = null;
        var seen = false;
        int ls = 0, le = 0;
        var lastIdent = false;
        var i = 0;
        Tok k;
        while ((k = NextToken(body, ref i, out var s)) != Tok.End)
        {
            if (!seen) { seen = true; if (k == Tok.Ident) first = body[s..i]; }
            ls = s; le = i; lastIdent = k == Tok.Ident;
        }
        return (first, lastIdent ? body[ls..le] : null);
    }

    /// <summary>
    /// What token pasting can build (the rules in TESTING.md, "Header-carve rules"). A define whose name starts or ends
    /// with a piece could be a paste result, so it is kept. Pieces come from the identifiers next to a <c>##</c>
    /// (<c>REG_##n##_BASE</c>: "REG_", "_BASE"; <c>P##n</c>: "P") and from what the calls of a paste macro pass
    /// (<c>CAT(UART, 2)</c>: "UART"), with what such an argument expands to at the edges of its body.
    /// A paste macro pastes a parameter, or forwards to one: a function-like body that names one, or an object-like
    /// body that ends in one (<c>#define F CAT</c>: <c>F(a, b)</c> calls it). Any definition makes it one, in any order.
    /// </summary>
    private sealed class Pastes
    {
        // Name -> the forwarding macros whose body names it: a new paste macro wakes only those (rescanning every
        // forwarding macro for each new paster took hours at work, tens of thousands of each). Let go by Resolve.
        private Dictionary<string, List<string>> calledBy = new(StringComparer.Ordinal);
        // Object-like defines whose body ends in a name, (that name, the define), sorted by Resolve and then let go.
        private List<(string Target, string Name)> aliases = new();
        private (string Target, string Name)[]? aliasIndex;
        private readonly HashSet<string> pasters = new(StringComparer.Ordinal);
        // Hash codes of the pasters: a name read from a line is looked up without a string made for it.
        private readonly HashSet<int> pasterHashes = new();
        // Forwarding macros learned (name -> one defined in a big header), counted, not used.
        private readonly Dictionary<string, bool> forwarders = new(StringComparer.Ordinal);
        public bool Any => pasters.Count > 0;

        /// <summary>Paste macros and forwarding macros learned so far (for the heartbeat).</summary>
        public (int Pasters, int Callers) Counts => (pasters.Count, forwarders.Count);

        /// <summary>Forwarding macros with a definition in the big headers.</summary>
        public int BigForwarders => forwarders.Count(f => f.Value);

        /// <summary>Grows when an argument name's expansion is learned: defines read before may then build more.</summary>
        public int Version => pasters.Count + expanded.Count;

        public bool IsPaster(string name) => pasters.Contains(name);

        /// <summary>Was <paramref name="name"/> passed to a paste macro, or reached from one (its expansion is a piece)?</summary>
        public bool IsExpanded(string name) => expanded.Contains(name);

        /// <summary>Does any identifier in <paramref name="text"/> name a paste macro?</summary>
        public bool NamesAPaster(string text)
        {
            var i = 0;
            while (NextIdentifier(text, ref i, out var start))
                if (IsPaster(text.AsSpan(start, i - start))) return true;
            return false;
        }

        public bool IsPaster(ReadOnlySpan<char> name) =>
            pasterHashes.Contains(string.GetHashCode(name)) && pasters.Contains(name.ToString());

        internal enum DefKind { Paster, Forwarder, Alias }

        /// <summary>What a definition says for paste macros: it pastes a parameter; it is function-like with a call
        /// in its body (the names it uses, its own parameters excepted); or object-like ending in a name.</summary>
        internal readonly record struct Def(string Name, DefKind Kind, string[] Names);

        internal static Def? ParseDef(string text)
        {
            if (!text.Contains("define", StringComparison.Ordinal)) return null;
            var (name, parms, body, variadic) = FunctionLike(text);
            if (name is not null)
            {
                foreach (var (piece, afterComma) in PastePieces(body))
                    if (!(afterComma && piece == variadic) && parms.Contains(piece)) return new Def(name, DefKind.Paster, Array.Empty<string>());
                if (!body.Contains('(')) return null;
                return new Def(name, DefKind.Forwarder, Identifiers(body).Where(id => !parms.Contains(id)).Distinct().ToArray());
            }
            if (ObjectBody(text, out var oname) is { } obody && oname is not null && BoundaryIdents(obody).Last is { } last)
                return new Def(oname, DefKind.Alias, new[] { last });
            return null;
        }

        /// <summary>Learn a definition (<paramref name="big"/>: in a big header, where the counts are kept).</summary>
        public void Learn(Def def, bool big)
        {
            switch (def.Kind)
            {
                case DefKind.Paster:
                    MakePaster(def.Name);
                    break;
                case DefKind.Forwarder:
                    forwarders[def.Name] = big || forwarders.GetValueOrDefault(def.Name);
                    if (pasters.Contains(def.Name)) break;
                    // Not a set overlap with the pasters: that walks every paster for each forwarding macro (1.5M of
                    // each at work: the big headers' paste macros took hours).
                    if (def.Names.Any(pasters.Contains)) { MakePaster(def.Name); break; }
                    foreach (var id in def.Names)
                    {
                        if (!calledBy.TryGetValue(id, out var by)) calledBy[id] = by = new List<string>();
                        by.Add(def.Name);
                    }
                    break;
                case DefKind.Alias:
                    aliases.Add((def.Names[0], def.Name));
                    break;
            }
        }

        /// <summary>Every definition is learned: an alias of a paste macro is one, and so is what forwards to it.</summary>
        public void Resolve()
        {
            aliasIndex = aliases.ToArray();
            aliases = new();
            Array.Sort(aliasIndex, (a, b) => string.CompareOrdinal(a.Target, b.Target));
            Propagate(new Stack<string>(pasters));
            aliasIndex = null;
            calledBy = new(StringComparer.Ordinal);
        }

        /// <summary><paramref name="name"/> is a paster, and so is every forwarding macro whose body names one.</summary>
        private void MakePaster(string name)
        {
            if (!Add(name)) return;
            var work = new Stack<string>();
            work.Push(name);
            Propagate(work);
        }

        private bool Add(string name)
        {
            if (!pasters.Add(name)) return false;
            pasterHashes.Add(name.GetHashCode());
            return true;
        }

        private void Propagate(Stack<string> work)
        {
            while (work.TryPop(out var p))
            {
                if (calledBy.Remove(p, out var by))
                    foreach (var caller in by)
                        if (Add(caller)) work.Push(caller);
                if (aliasIndex is null) continue;
                var lo = 0;
                var hi = aliasIndex.Length;
                while (lo < hi) { var mid = (lo + hi) / 2; if (string.CompareOrdinal(aliasIndex[mid].Target, p) < 0) lo = mid + 1; else hi = mid; }
                for (; lo < aliasIndex.Length && aliasIndex[lo].Target == p; lo++)
                    if (Add(aliasIndex[lo].Name)) work.Push(aliasIndex[lo].Name);
            }
        }

        /// <summary>The identifiers next to each <c>##</c> of a line (a function-like define's own parameters, and
        /// <c>, ##__VA_ARGS__</c>, excepted: its call sites supply them).</summary>
        public static void AddLiteralPieces(string text, HashSet<string> pieces)
        {
            if (!text.Contains("##", StringComparison.Ordinal)) return;
            var (name, parms, body, variadic) = FunctionLike(text);
            if (name is null) { foreach (var (piece, _) in PastePieces(text)) pieces.Add(piece); return; }
            foreach (var (piece, afterComma) in PastePieces(body))
                if (!(afterComma && piece == variadic) && !parms.Contains(piece)) pieces.Add(piece);
        }

        // Object-like defines of the kept files (name -> the names at the edges of each body: `#if BOARD == 1 /
        // #define P USART / #else / #define P LPUART` builds either), and the argument names whose expansion counts.
        private readonly Dictionary<string, List<string>> objects = new(StringComparer.Ordinal);
        private readonly HashSet<string> expanded = new(StringComparer.Ordinal);

        /// <summary>Remember an object-like define of a kept file: a paste argument naming it may expand to it.</summary>
        public void LearnObject(string text)
        {
            if (!text.Contains("define", StringComparison.Ordinal)) return;
            if (ObjectBody(text, out var name) is not { } body || name is null) return;
            var (first, last) = BoundaryIdents(body);
            if (first is null && last is null) return;
            if (!objects.TryGetValue(name, out var ends)) objects[name] = ends = new List<string>();
            if (first is not null && !ends.Contains(first)) ends.Add(first);
            if (last is not null && !ends.Contains(last)) ends.Add(last);
        }

        /// <summary>A name passed to a paste macro is a piece, and so is what it expands to.</summary>
        public void AddPiece(string id, HashSet<string> fragments)
        {
            fragments.Add(id);
            Expand(id, fragments);
        }

        /// <summary>
        /// An argument passed through a forwarding macro is macro-expanded before the paste:
        /// <c>CAT(UART_PREFIX, UART_NUM)</c> with <c>#define UART_PREFIX USART</c> builds USART2. Only a body's first and
        /// last tokens can join a paste (<c>##</c> joins the tokens beside it), so those, when names, are pieces too,
        /// expanded the same way. Bodies in the kept files resolve here; one in a carved header when the fixpoint reads
        /// it (<see cref="ExpandDefine"/>).
        /// </summary>
        private void Expand(string id, HashSet<string> fragments)
        {
            var work = new Stack<string>();
            work.Push(id);
            while (work.TryPop(out var x))
            {
                if (!expanded.Add(x) || !objects.TryGetValue(x, out var ends)) continue;
                foreach (var e in ends) { fragments.Add(e); work.Push(e); }
            }
        }

        /// <summary>A carved header's define for an argument name passed to a paster (the names at the edges of its
        /// body, null when not object-like): its expansion is a piece.</summary>
        public void ExpandDefine(string name, string? first, string? last, HashSet<string> fragments)
        {
            if (!expanded.Contains(name)) return;
            if (first is not null) AddPiece(first, fragments);
            if (last is not null) AddPiece(last, fragments);
        }

        /// <summary>Name, parameters, body and variadic parameter (<c>__VA_ARGS__</c>, or <c>args</c> for GNU's
        /// <c>args...</c>; null when not variadic) of a function-like <c>#define NAME(params) body</c>, or a null name.</summary>
        internal static (string? Name, HashSet<string> Params, string Body, string? Variadic) FunctionLike(string text)
        {
            var none = (default(string), new HashSet<string>(), "", default(string));
            if (!IsDefineDirective(text.TrimStart())) return none;
            var name = DefineName(text);
            if (name is null) return none;
            var at = text.IndexOf(name, text.IndexOf("define", StringComparison.Ordinal) + 6, StringComparison.Ordinal) + name.Length;
            if (at >= text.Length || text[at] != '(') return none;   // object-like
            var close = text.IndexOf(')', at);
            if (close < 0) return none;
            var list = text[(at + 1)..close].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            string? variadic = list.Count > 0 && list[^1].EndsWith("...", StringComparison.Ordinal)
                ? (list[^1] == "..." ? "__VA_ARGS__" : list[^1].TrimEnd('.').Trim()) : null;
            var parms = list.Select(p => p == "..." ? "__VA_ARGS__" : p.TrimEnd('.').Trim()).ToHashSet(StringComparer.Ordinal);
            parms.Add("__VA_ARGS__");
            return (name, parms, text[(close + 1)..], variadic);
        }

        /// <summary>The body of an object-like <c>#define NAME body</c>, or null.</summary>
        public static string? ObjectBody(string text, out string? name)
        {
            name = null;
            if (!IsDefineDirective(text.TrimStart())) return null;
            var n = DefineName(text);
            if (n is null) return null;
            var at = text.IndexOf(n, text.IndexOf("define", StringComparison.Ordinal) + 6, StringComparison.Ordinal) + n.Length;
            if (at < text.Length && text[at] == '(') return null;   // function-like
            name = n;
            return text[at..];
        }
    }

    /// <summary>
    /// The pieces a file's paste-macro calls pass, read a logical line at a time with what is open carried over:
    /// <list type="bullet">
    /// <item>every identifier among a paste macro's arguments, nested calls included; an argument list runs to its
    /// matching <c>)</c> over lines and <c>#if</c> branches (each branch starts from what was open at the <c>#if</c>;
    /// after the <c>#endif</c> the branch leaving the most open goes on);</item>
    /// <item>a paste macro named without a call (<c>APPLY(CAT, A, 2)</c>): every identifier of the call around it;</item>
    /// <item>a call of what a paste-macro call builds (<c>CAT(FWD_, 7)(x)</c>): its arguments too;</item>
    /// <item>a <c>#define</c> is read on its own, its parameters not pieces (its call sites supply them).</item>
    /// </list>
    /// Comments and literals name nothing. A list still open at the end of the file gives its pieces then.
    /// </summary>
    private sealed class CallScanner
    {
        private readonly Pastes pastes;
        public readonly List<string> Pieces = new();
        // Identifiers inside the open parentheses, in order; each frame owns those from its start on.
        private readonly List<string> ids = new();
        private readonly List<Frame> frames = new();
        private readonly Stack<Branch> branches = new();
        private bool inBlock, pending, afterPasterCall;
        private HashSet<string>? exclude;
        private CallScanner? defines;

        private record struct Frame(int Start, bool Paster, bool Bare);

        /// <summary>An <c>#if</c> group: what was open at the <c>#if</c> (the identifiers up to <c>IdsCount</c> are
        /// never let go while it is open), and the branch so far that left the most open, with its own identifiers.</summary>
        private sealed class Branch
        {
            public required Frame[] Frames;
            public required int IdsCount;
            public required bool Pending, AfterPasterCall;
            public Frame[]? DeepFrames;
            public string[]? DeepIds;
            public bool DeepPending, DeepAfterPasterCall;
        }

        public CallScanner(Pastes pastes) => this.pastes = pastes;

        /// <summary>Read the next logical line of the file.</summary>
        public void Feed(string line)
        {
            if (!inBlock)
            {
                var t = line.TrimStart();
                if (t.Length > 0 && t[0] == '#') { Directive(line, t); return; }
            }
            Scan(line, 0);
        }

        /// <summary>Read a <c>#define</c> on its own.</summary>
        public void Define(string text)
        {
            defines ??= new CallScanner(pastes);
            defines.ReadDefine(text);
            Pieces.AddRange(defines.Pieces);
            defines.Pieces.Clear();
        }

        private void ReadDefine(string text)
        {
            var (name, parms, body, _) = Pastes.FunctionLike(text);
            if (name is null)
            {
                if (Pastes.ObjectBody(text, out _) is not { } obody) return;
                body = obody;
                parms = null!;
            }
            exclude = parms;
            Scan(body, 0);
            Flush();
            exclude = null;
            inBlock = false;
        }

        private void Directive(string line, string t)
        {
            if (IsDefineDirective(t)) Define(line);
            else if (StartsWithHash(t, "if") || StartsWithHash(t, "ifdef") || StartsWithHash(t, "ifndef"))
                branches.Push(new Branch { Frames = frames.ToArray(), IdsCount = ids.Count, Pending = pending, AfterPasterCall = afterPasterCall });
            else if (branches.Count > 0 && (StartsWithHash(t, "elif") || StartsWithHash(t, "else") || StartsWithHash(t, "elifdef") || StartsWithHash(t, "elifndef")))
            {
                var b = branches.Peek();
                KeepIfDeeper(b);
                frames.Clear(); frames.AddRange(b.Frames);
                ids.RemoveRange(b.IdsCount, ids.Count - b.IdsCount);
                pending = b.Pending; afterPasterCall = b.AfterPasterCall;
            }
            else if (branches.Count > 0 && StartsWithHash(t, "endif"))
            {
                var b = branches.Pop();
                if (b.DeepFrames is not null && b.DeepFrames.Length > frames.Count)
                {
                    frames.Clear(); frames.AddRange(b.DeepFrames);
                    ids.RemoveRange(b.IdsCount, ids.Count - b.IdsCount);
                    ids.AddRange(b.DeepIds!);
                    pending = b.DeepPending; afterPasterCall = b.DeepAfterPasterCall;
                }
            }
            // Other directives (#include, #pragma, #error, #undef) take no part in a call.
            inBlock = CommentOpenAfter(line, Comment.None) == Comment.Block;
        }

        private void KeepIfDeeper(Branch b)
        {
            if (b.DeepFrames is not null && b.DeepFrames.Length >= frames.Count) return;
            b.DeepFrames = frames.ToArray();
            b.DeepIds = ids.GetRange(b.IdsCount, ids.Count - b.IdsCount).ToArray();
            b.DeepPending = pending; b.DeepAfterPasterCall = afterPasterCall;
        }

        private void Scan(string text, int i)
        {
            while (i < text.Length)
            {
                if (inBlock)
                {
                    var e = text.IndexOf("*/", i, StringComparison.Ordinal);
                    if (e < 0) return;
                    inBlock = false;
                    i = e + 2;
                    continue;
                }
                var c = text[i];
                if (c is ' ' or '\t' or '\r' or '\n' or '\\' or '\f' or '\v') { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { inBlock = true; i += 2; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') return;
                if (IsIdentStart(c))
                {
                    var s = i;
                    while (i < text.Length && IsIdentChar(text[i])) i++;
                    Other();
                    string? id = null;
                    var excluded = exclude is not null && exclude.Contains(id = text[s..i]);
                    if (frames.Count > 0 && !excluded) ids.Add(id ?? text[s..i]);
                    pending = !excluded && (id is not null ? pastes.IsPaster(id) : pastes.IsPaster(text.AsSpan(s, i - s)));
                    continue;
                }
                if (StartsNumber(text, i)) { i = PpNumberEnd(text, i); Other(); continue; }
                if (c is '"' or '\'') { i = QuoteEnd(text, i); Other(); continue; }
                i++;
                if (c == '(')
                {
                    frames.Add(new Frame(ids.Count, pending || afterPasterCall, false));
                    pending = afterPasterCall = false;
                    continue;
                }
                Other();
                if (c != ')' || frames.Count == 0) continue;
                var f = frames[^1];
                frames.RemoveAt(frames.Count - 1);
                if (f.Paster || f.Bare) Emit(f.Start);
                if (frames.Count == 0)
                {
                    var keep = branches.Count > 0 ? branches.Peek().IdsCount : 0;
                    ids.RemoveRange(keep, ids.Count - keep);
                }
                afterPasterCall = f.Paster;
            }
        }

        /// <summary>A token other than '(' follows: a paste macro just named was not called, so the call around it
        /// passes it on.</summary>
        private void Other()
        {
            if (pending && frames.Count > 0) frames[^1] = frames[^1] with { Bare = true };
            pending = afterPasterCall = false;
        }

        private void Emit(int start)
        {
            for (var k = start; k < ids.Count; k++) Pieces.Add(ids[k]);
        }

        /// <summary>The end of the file (or of a define): what is still open gives its pieces.</summary>
        public void Flush()
        {
            Other();
            for (var k = 0; k < frames.Count; k++)
                if (frames[k].Paster || frames[k].Bare) { Emit(frames[k].Start); break; }   // the outermost such holds them all
            frames.Clear();
            ids.Clear();
            branches.Clear();
            pending = afterPasterCall = false;
        }
    }

    /// <summary>Add each C identifier in <paramref name="text"/> not in <paramref name="known"/> to <paramref name="set"/>.</summary>
    private static void AddNewIdentifiers(HashSet<string> set, HashSet<string> known, string text, string? except = null)
    {
        var i = 0;
        while (NextIdentifier(text, ref i, out var start))
        {
            if (except is not null && text.AsSpan(start, i - start).SequenceEqual(except)) continue;
            var id = text[start..i];
            if (!known.Contains(id)) set.Add(id);
        }
    }

    /// <summary>Add every C identifier in <paramref name="text"/> to <paramref name="set"/>; return true if any was new.</summary>
    private static bool AddIdentifiers(HashSet<string> set, string text)
    {
        var grew = false;
        var i = 0;
        while (NextIdentifier(text, ref i, out var start)) grew |= set.Add(text[start..i]);
        return grew;
    }

    /// <summary>The next identifier at or after <paramref name="i"/>: its start, <paramref name="i"/> just past it. A number
    /// is one token and names nothing (<c>0x1Fu</c> is not the name <c>x1Fu</c>: every register value was one, millions of
    /// names in a big header's needed set).</summary>
    private static bool NextIdentifier(string text, ref int i, out int start)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (IsIdentStart(c))
            {
                start = i;
                while (i < text.Length && IsIdentChar(text[i])) i++;
                return true;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                // A preprocessing number: digits, letters, _, '.', and a sign after e, E, p or P.
                for (i++; i < text.Length; i++)
                {
                    var d = text[i];
                    if (IsIdentChar(d) || d == '.') continue;
                    if ((d == '+' || d == '-') && text[i - 1] is 'e' or 'E' or 'p' or 'P') continue;
                    break;
                }
                continue;
            }
            i++;
        }
        start = i;
        return false;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
