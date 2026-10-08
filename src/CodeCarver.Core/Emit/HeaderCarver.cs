using System.Text;

namespace CodeCarver.Core.Emit;

/// <summary>Outcome of carving big headers: bytes before/after and how many #defines were kept vs dropped.</summary>
public sealed record HeaderCarveResult(long BytesBefore, long BytesAfter, int DefinesKept, int DefinesDropped);

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
    public static HeaderCarveResult Carve(string outDir, IReadOnlyCollection<string> bigHeaderRels, IEnumerable<string>? extraNames = null)
    {
        var bigSet = new HashSet<string>(bigHeaderRels, StringComparer.OrdinalIgnoreCase);
        var bigFull = bigHeaderRels.Select(r => Path.Combine(outDir, r)).ToList();

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
        var seedFiles = new List<string>();
        foreach (var f in Directory.EnumerateFiles(outDir, "*", walk))
        {
            var rel = Path.GetRelativePath(outDir, f).Replace('\\', '/');
            if (bigSet.Contains(rel) || LooksBinary(f)) continue;
            seedFiles.Add(f);
            foreach (var line in LogicalLines(f))
            {
                AddIdentifiers(needed, line);
                pastes.Learn(line, fragments);
                pastes.LearnObject(line);
            }
        }
        // A paste macro's own call sites name the pieces (`CAT(UART, 2)` builds UART2): learned macros first,
        // in the big headers too, then the calls.
        foreach (var path in bigFull)
            foreach (var (_, text, kind) in EnumerateLogicalLines(path))
                if (kind == LineKind.Define) pastes.Learn(text, literals: null);   // its literal pieces count once it's wanted
        if (pastes.Any)
            foreach (var f in seedFiles)
                foreach (var line in LogicalLines(f))
                    pastes.AddCallArguments(line, fragments);

        // A define is wanted if the code references its name, OR a paste fragment could build that name.
        bool Wanted(string name)
        {
            if (needed.Contains(name)) return true;
            if (fragments.Count <= 64)
                return fragments.Any(fr => name.StartsWith(fr, StringComparison.Ordinal) || name.EndsWith(fr, StringComparison.Ordinal));
            for (var k = 1; k <= name.Length; k++)
                if (fragments.Contains(name[..k]) || fragments.Contains(name[^k..])) return true;
            return false;
        }

        // 2. Fixpoint: grow `needed` with the bodies of wanted defines and all #if-condition identifiers,
        //    streaming each header per pass. Terminates because `needed` only grows and is finite. Passes
        //    ≈ the deepest define-dependency chain (small for register maps).
        //    The header's own non-#define lines (typedefs, enums, inline functions) are kept verbatim, so the
        //    names they use are needed too; and a kept define that pastes (##) can form further names.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var path in bigFull)
                foreach (var (name, text, kind) in EnumerateLogicalLines(path))
                {
                    if (kind == LineKind.Define && (name is null || !Wanted(name))) continue;
                    // Kept verbatim (not a define) or kept as wanted: what it names is needed, and what its pastes
                    // and paste-macro calls can build.
                    grew |= AddIdentifiers(needed, text);
                    var n = fragments.Count;
                    if (kind == LineKind.Define) pastes.Learn(text, fragments);
                    pastes.AddCallArguments(text, fragments);
                    if (kind == LineKind.Define && name is not null) pastes.ExpandDefine(name, text, fragments);
                    grew |= fragments.Count != n;
                }
        }

        // 3. Emit: rewrite each header, dropping #defines whose name isn't wanted (and their continuations).
        long before = 0, after = 0;
        var kept = 0;
        var dropped = 0;
        foreach (var path in bigFull)
        {
            before += new FileInfo(path).Length;
            var tmp = path + ".carve.tmp";
            RewriteDroppingUnneeded(path, tmp, Wanted, ref kept, ref dropped);
            // A read-only source copy (Perforce) must not make the replace throw.
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            File.Move(tmp, path);
            after += new FileInfo(path).Length;
        }
        return new HeaderCarveResult(before, after, kept, dropped);
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
            var t = line.TrimStart();
            var commented = inComment != Comment.None;   // a #define inside a comment is comment text
            var isDefine = !commented && IsDefineDirective(t);
            var isCond = !commented && !isDefine && IsConditionalDirective(t);
            inComment = CommentOpenAfter(line, inComment);
            var sb = new StringBuilder(line);
            if (isDefine || isCond)
                while (EndsWithContinuation(line) && (line = r.ReadLine()) is not null)
                {
                    sb.Append('\n').Append(line);
                    inComment = CommentOpenAfter(line, inComment);
                }
            var full = sb.ToString();
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
    private static void RewriteDroppingUnneeded(string src, string dst, Func<string, bool> wanted, ref int kept, ref int dropped)
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

    private static bool IsDefineDirective(string trimmed) => StartsWithHash(trimmed, "define");

    private static bool IsConditionalDirective(string trimmed) =>
        StartsWithHash(trimmed, "if") || StartsWithHash(trimmed, "ifdef") ||
        StartsWithHash(trimmed, "ifndef") || StartsWithHash(trimmed, "elif");

    /// <summary>Matches <c>#</c> (optional spaces) then the keyword as a whole word — e.g. <c>#  define</c>.</summary>
    private static bool StartsWithHash(string trimmed, string keyword)
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
        var t = line.TrimEnd();
        return t.Length > 0 && t[^1] == '\\';
    }

    /// <summary>
    /// The identifier pieces next to each <c>##</c> paste (<c>REG_ ## n ## _BASE</c> → "REG_", "n", "_BASE"),
    /// spaces around the <c>##</c> allowed. <c>AfterComma</c>: the piece follows <c>, ##</c> — GNU's
    /// <c>, ##__VA_ARGS__</c>, which deletes the comma for an empty argument list and pastes nothing.
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
            if (j > s) yield return (text[s..j], e > 0 && text[e - 1] == ',');
            i = text.IndexOf("##", i + 2, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What token pasting can build. A define whose name starts or ends with a fragment could be a paste result,
    /// so it is kept. Fragments come from the literal text next to a <c>##</c> (<c>REG_##n##_BASE</c>: "REG_",
    /// "_BASE"; <c>P##n</c>: "P") and, for a macro that pastes its parameters (<c>CAT(a, b) a##b</c>, or one that
    /// passes its parameters on to such a macro), from the identifiers its calls pass (<c>CAT(UART, 2)</c>: "UART").
    /// </summary>
    private sealed class Pastes
    {
        private readonly Dictionary<string, string> callers = new(StringComparer.Ordinal);   // name -> body, may forward
        private readonly HashSet<string> pasters = new(StringComparer.Ordinal);
        public bool Any => pasters.Count > 0;

        /// <summary>Learn a logical line: a function-like #define that pastes a parameter becomes a paster; its literal
        /// paste pieces go to <paramref name="literals"/> (null: not yet, the define isn't known to be wanted).</summary>
        public void Learn(string text, HashSet<string>? literals)
        {
            if (!text.Contains('#')) return;
            var (name, parms, body, variadic) = FunctionLike(text);
            if (name is null)
            {
                if (literals is not null && text.Contains("##", StringComparison.Ordinal))
                    foreach (var (piece, _) in PastePieces(text)) if (piece.Length > 1) literals.Add(piece);   // unknown context
                return;
            }
            var pastesParam = false;
            foreach (var (piece, afterComma) in PastePieces(body))
                if (afterComma && piece == variadic) continue;   // `, ##__VA_ARGS__` (a logging macro) builds no name
                else if (parms.Contains(piece)) pastesParam = true;
                else literals?.Add(piece);
            if (pastesParam) { if (pasters.Add(name)) Propagate(); return; }
            if (parms.Count > 0 && body.Contains('('))
            {
                callers[name] = body;
                if (CallsPaster(body)) { pasters.Add(name); Propagate(); }
            }
        }

        /// <summary>Every identifier passed to a paster called in <paramref name="text"/> (a #define's own parameters
        /// excepted: they are placeholders, not names).</summary>
        public void AddCallArguments(string text, HashSet<string> fragments)
        {
            if (pasters.Count == 0) return;
            var (_, parms, _, _) = FunctionLike(text);
            var i = 0;
            while (i < text.Length)
            {
                if (!IsIdentStart(text[i]) || (i > 0 && IsIdentChar(text[i - 1]))) { i++; continue; }
                var s = i;
                while (i < text.Length && IsIdentChar(text[i])) i++;
                if (!pasters.Contains(text[s..i])) continue;
                var p = i;
                while (p < text.Length && text[p] is ' ' or '\t') p++;
                if (p >= text.Length || text[p] != '(') continue;
                var depth = 0;
                var a = p;
                for (; a < text.Length; a++)
                {
                    if (text[a] == '(') depth++;
                    else if (text[a] == ')' && --depth == 0) break;
                }
                var args = new HashSet<string>(StringComparer.Ordinal);
                AddIdentifiers(args, text[(p + 1)..Math.Min(a, text.Length)]);
                foreach (var id in args)
                    if (!parms.Contains(id)) { fragments.Add(id); Expand(id, fragments); }
            }
        }

        // Object-like defines of the kept files (name -> every body: `#if BOARD == 1 / #define P USART / #else /
        // #define P LPUART` builds either), and the argument names whose expansion counts.
        private readonly Dictionary<string, List<string>> objects = new(StringComparer.Ordinal);
        private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
        private const int MaxBodiesPerName = 16;
        // In a body of several names, ones shorter than this name too many defines to be worth a fragment (a
        // one-letter operand beside a cast): the longer names around them carry the paste.
        private const int MinExpandedFragment = 3;

        /// <summary>Remember an object-like define of a kept file: a paste argument naming it may expand to it.</summary>
        public void LearnObject(string text)
        {
            if (!text.Contains("define", StringComparison.Ordinal)) return;
            if (ObjectBody(text, out var name) is not { } body || name is null || body.Length > 512) return;
            if (!objects.TryGetValue(name, out var bodies)) objects[name] = bodies = new List<string>();
            if (bodies.Count < MaxBodiesPerName && !bodies.Contains(body)) bodies.Add(body);
        }

        /// <summary>
        /// An argument passed through a forwarding macro is macro-expanded before the paste:
        /// <c>CAT(UART_PREFIX, UART_NUM)</c> with <c>#define UART_PREFIX USART</c> builds USART2. So what an argument
        /// that is an object-like macro expands to (recursively) is a fragment too. Bodies in the kept files resolve
        /// here; one in a carved header resolves when the fixpoint reads it (<see cref="ExpandDefine"/>).
        /// </summary>
        private void Expand(string id, HashSet<string> fragments)
        {
            if (!expanded.Add(id) || !objects.TryGetValue(id, out var bodies)) return;
            foreach (var body in bodies) AddExpansion(body, fragments);
        }

        /// <summary>A carved header's define for an argument name passed to a paster: its expansion is a fragment.</summary>
        public void ExpandDefine(string name, string text, HashSet<string> fragments)
        {
            if (!expanded.Contains(name) || ObjectBody(text, out _) is not { } body) return;
            AddExpansion(body, fragments);
        }

        private void AddExpansion(string body, HashSet<string> fragments)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            AddIdentifiers(ids, body);
            // A body that is one name is a plain rename (`#define PORT U`): that name is the piece, however short.
            foreach (var b in ids)
            {
                if (ids.Count == 1 || b.Length >= MinExpandedFragment) fragments.Add(b);
                Expand(b, fragments);
            }
        }

        private bool CallsPaster(string body)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            AddIdentifiers(ids, body);
            return ids.Overlaps(pasters);
        }

        private void Propagate()
        {
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (name, body) in callers)
                    if (!pasters.Contains(name) && CallsPaster(body)) { pasters.Add(name); grew = true; }
            }
        }

        /// <summary>Name, parameters and body of a function-like <c>#define NAME(params) body</c>, or a null name.</summary>
        /// <summary>Name, parameters, body and variadic parameter (<c>__VA_ARGS__</c>, or <c>args</c> for GNU's
        /// <c>args...</c>; null when not variadic) of a function-like <c>#define NAME(params) body</c>, or a null name.</summary>
        private static (string? Name, HashSet<string> Params, string Body, string? Variadic) FunctionLike(string text)
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

    /// <summary>Add every C identifier in <paramref name="text"/> to <paramref name="set"/>; return true if any was new.</summary>
    private static bool AddIdentifiers(HashSet<string> set, string text)
    {
        var grew = false;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsIdentStart(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsIdentChar(text[i])) i++;
            grew |= set.Add(text[start..i]);
        }
        return grew;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
