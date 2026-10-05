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
    public static HeaderCarveResult Carve(string outDir, IReadOnlyCollection<string> bigHeaderRels)
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
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var fragments = new HashSet<string>(StringComparer.Ordinal);
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var f in Directory.EnumerateFiles(outDir, "*", walk))
        {
            var rel = Path.GetRelativePath(outDir, f).Replace('\\', '/');
            if (bigSet.Contains(rel) || LooksBinary(f)) continue;
            foreach (var line in File.ReadLines(f, Encoding.Latin1))
            {
                AddIdentifiers(needed, line);
                if (line.Contains("##", StringComparison.Ordinal)) AddPasteFragments(fragments, line);
            }
        }

        // A define is wanted if the code references its name, OR a paste fragment could build that name.
        bool Wanted(string name) =>
            needed.Contains(name) ||
            fragments.Any(fr => name.StartsWith(fr, StringComparison.Ordinal)
                             || name.EndsWith(fr, StringComparison.Ordinal));

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
                    if (kind != LineKind.Define) { grew |= AddIdentifiers(needed, text); continue; }
                    if (name is not null && Wanted(name))
                    {
                        grew |= AddIdentifiers(needed, text); // keep it -> its body's names are needed too
                        if (text.Contains("##", StringComparison.Ordinal))
                        {
                            var n = fragments.Count;
                            AddPasteFragments(fragments, text);
                            grew |= fragments.Count != n;
                        }
                    }
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
        while ((line = r.ReadLine()) is not null)
        {
            var t = line.TrimStart();
            var isDefine = IsDefineDirective(t);
            var isCond = !isDefine && IsConditionalDirective(t);
            var sb = new StringBuilder(line);
            if (isDefine || isCond)
                while (EndsWithContinuation(line) && (line = r.ReadLine()) is not null)
                    sb.Append('\n').Append(line);
            var full = sb.ToString();
            yield return isDefine ? (DefineName(full), full, LineKind.Define)
                 : isCond ? (null, full, LineKind.Conditional)
                 : (null, full, LineKind.Other);
        }
    }

    /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/>, omitting #defines the predicate rejects.
    /// Byte-transparent (Latin-1) and every kept line keeps its own line ending (review E1).</summary>
    private static void RewriteDroppingUnneeded(string src, string dst, Func<string, bool> wanted, ref int kept, ref int dropped)
    {
        using var w = new StreamWriter(dst, false, Encoding.Latin1);
        using var e = ReadLinesWithEol(src).GetEnumerator();
        while (e.MoveNext())
        {
            var (line, eol) = e.Current;
            if (IsDefineDirective(line.TrimStart()))
            {
                var name = DefineName(line);
                var drop = name is not null && !wanted(name);
                if (drop) dropped++; else kept++;

                // Consume the whole (possibly multi-line) define; write it only if kept.
                if (!drop) { w.Write(line); w.Write(eol); }
                var cont = line;
                while (EndsWithContinuation(cont) && e.MoveNext())
                {
                    (cont, eol) = e.Current;
                    if (!drop) { w.Write(cont); w.Write(eol); }
                }
                continue;
            }
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

    /// <summary>A NUL byte in the first 8 KB: treat as binary (no identifiers to seed from).</summary>
    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8192];
            var n = fs.Read(buf, 0, buf.Length);
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
    /// Collect the literal identifier fragments adjacent to a <c>##</c> paste (<c>REG_ ## n ## _BASE</c>
    /// → "REG_", "_BASE"). A define whose name starts or ends with such a fragment could be the concrete
    /// paste result, so it must be kept. Single-char fragments (usually the pasted parameter) are ignored.
    /// </summary>
    private static void AddPasteFragments(HashSet<string> set, string line)
    {
        var i = line.IndexOf("##", StringComparison.Ordinal);
        while (i >= 0)
        {
            // fragment immediately before the ##
            var e = i;
            while (e > 0 && (char.IsAsciiLetterOrDigit(line[e - 1]) || line[e - 1] == '_')) e--;
            if (i - e > 1) set.Add(line[e..i]);
            // fragment immediately after the ##
            var s = i + 2;
            var j = s;
            while (j < line.Length && (char.IsAsciiLetterOrDigit(line[j]) || line[j] == '_')) j++;
            if (j - s > 1) set.Add(line[s..j]);
            i = line.IndexOf("##", i + 2, StringComparison.Ordinal);
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
