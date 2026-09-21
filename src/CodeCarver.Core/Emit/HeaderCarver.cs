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
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(outDir, f).Replace('\\', '/');
            if (bigSet.Contains(rel)) continue;
            foreach (var line in File.ReadLines(f))
                AddIdentifiers(needed, line);
        }

        // 2. Fixpoint: grow `needed` with the bodies of needed defines and all #if-condition identifiers,
        //    streaming each header per pass. Terminates because `needed` only grows and is finite. Passes
        //    ≈ the deepest define-dependency chain (small for register maps).
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var path in bigFull)
                foreach (var (name, text, isConditional) in EnumerateLogicalDirectives(path))
                {
                    if (isConditional) { grew |= AddIdentifiers(needed, text); continue; }
                    if (name is not null && needed.Contains(name))
                        grew |= AddIdentifiers(needed, text); // keep it -> its body's names are needed too
                }
        }

        // 3. Emit: rewrite each header, dropping #defines whose name isn't needed (and their continuations).
        long before = 0, after = 0;
        var kept = 0;
        var dropped = 0;
        foreach (var path in bigFull)
        {
            before += new FileInfo(path).Length;
            var tmp = path + ".carve.tmp";
            RewriteDroppingUnneeded(path, tmp, needed, ref kept, ref dropped);
            File.Delete(path);
            File.Move(tmp, path);
            after += new FileInfo(path).Length;
        }
        return new HeaderCarveResult(before, after, kept, dropped);
    }

    /// <summary>
    /// Stream a header yielding one entry per logical directive: for a <c>#define</c>, its
    /// (name, full-text-including-continuations, isConditional=false); for <c>#if/#ifdef/#ifndef/#elif</c>,
    /// (null, the-condition-text, true). Continuation (<c>\</c>) lines are joined. Bounded memory.
    /// </summary>
    private static IEnumerable<(string? Name, string Text, bool IsConditional)> EnumerateLogicalDirectives(string path)
    {
        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            var t = line.TrimStart();
            if (IsConditionalDirective(t)) { yield return (null, line, true); continue; }
            if (!IsDefineDirective(t)) continue;

            var sb = new StringBuilder(line);
            while (EndsWithContinuation(line) && (line = r.ReadLine()) is not null)
                sb.Append('\n').Append(line);
            var full = sb.ToString();
            yield return (DefineName(full), full, false);
        }
    }

    /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/>, omitting #defines whose name isn't needed.</summary>
    private static void RewriteDroppingUnneeded(string src, string dst, HashSet<string> needed, ref int kept, ref int dropped)
    {
        using var r = new StreamReader(src);
        using var w = new StreamWriter(dst);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (IsDefineDirective(line.TrimStart()))
            {
                var name = DefineName(line);
                var drop = name is not null && !needed.Contains(name);
                if (drop) dropped++; else kept++;

                // Consume the whole (possibly multi-line) define; write it only if kept.
                if (!drop) w.Write(line);
                var cont = line;
                while (EndsWithContinuation(cont) && (cont = r.ReadLine()) is not null)
                    if (!drop) { w.Write('\n'); w.Write(cont); }
                if (!drop) w.Write('\n');
                continue;
            }
            w.Write(line);
            w.Write('\n');
        }
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
