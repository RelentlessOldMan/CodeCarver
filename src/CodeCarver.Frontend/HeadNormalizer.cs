using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Frontend;

/// <summary>
/// File-scope function heads the C grammar cannot read, made readable for PARSING only (line-preserving; the blanked
/// text is returned so the caller can count its names as uses). Found by a sweep of definition shapes after the
/// 1.0.162 work eval; each one dropped the definition, and its file, while a call still needed it:
/// <list type="bullet">
/// <item>an unknown macro between the type and the name — <c>int WINAPI f(void)</c>, <c>void NORETURN f(void)</c>,
///   <c>char * FAR_PTR f(void)</c>. tree-sitter ends a declaration at <c>int WINAPI</c> and reads <c>f</c> as a type.
///   Every identifier the type does not need is blanked: with a type keyword (<c>int</c>, <c>struct s</c>) all
///   others, without one all but the first.</item>
/// <item>ALL-CAPS macro calls in the head or its parameters — AUTOSAR <c>FUNC(void, COM_CODE) f(P2VAR(uint8,
///   AUTOMATIC, X) p)</c>: the argument lists are blanked, leaving <c>FUNC f(P2VAR p)</c>.</item>
/// <item>a prototype wrapper around the parameter list — <c>int add PROTO((int a, int b)) {</c> (PROTO, __P,
///   _ANSI_ARGS_): <c>PROTO(</c> and the outer <c>)</c> are blanked. Read as written, the wrapper is the name and
///   the real name a type, and no verify scan sees the definition either: a silent miss.</item>
/// <item>directive lines inside the head: a <c>#pragma</c> between the <c>)</c> and the <c>{</c>, or an
///   <c>#if/#else</c> choosing the return type (the first branch is kept).</item>
/// </list>
/// Only heads followed by a body are touched, and never one whose part before the name holds a non-macro
/// parenthesis or a directive together with a parenthesis (a head split across <c>#ifdef</c> is pass 1a's).
/// </summary>
public static class HeadNormalizer
{
    private static readonly Regex Tok = new(@"[A-Za-z_]\w*|\d\w*|\S", RegexOptions.CultureInvariant);
    private static readonly Regex AllCaps = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Wrapper = new(@"^_*[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant);

    // Words tree-sitter's C grammar knows inside a declaration's specifiers.
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "extern", "static", "auto", "register", "inline", "__inline", "__inline__", "__forceinline", "thread_local",
        "__thread", "const", "constexpr", "volatile", "restrict", "__restrict__", "__restrict", "__extension__",
        "_Atomic", "_Noreturn", "noreturn", "alignas", "_Alignas", "__cdecl", "__clrcall", "__stdcall", "__fastcall",
        "__thiscall", "__vectorcall", "__ptr32", "__ptr64", "__sptr", "__uptr", "_unaligned", "__unaligned", "__based",
        "__declspec", "__attribute__", "__attribute", "__asm__", "__asm", "asm", "struct", "union", "enum", "typedef",
    };
    private static readonly HashSet<string> Primitive = new(StringComparer.Ordinal)
    {
        "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned", "_Bool", "bool", "_Complex",
        "size_t", "ssize_t", "ptrdiff_t", "intptr_t", "uintptr_t", "int8_t", "int16_t", "int32_t", "int64_t",
        "uint8_t", "uint16_t", "uint32_t", "uint64_t", "char16_t", "char32_t", "wchar_t",
    };

    internal static bool IsPrimitive(string word) => Primitive.Contains(word);
    internal static bool IsKnownSpecifier(string word) => Known.Contains(word);

    public static string Normalize(string text, out string removed)
    {
        removed = "";
        if (text.IndexOf('(') < 0 || text.IndexOf('{') < 0) return text;
        var code = ImplicitInt.CodeOnly(text);
        var toks = new List<(int Pos, string S)>();
        foreach (Match m in Tok.Matches(code)) toks.Add((m.Index, m.Value));
        if (toks.Count == 0) return text;

        var blank = new List<(int Start, int End)>();   // [Start, End) ranges of the ORIGINAL text to blank
        var stack = new Stack<bool>();                   // per open '{': is it transparent (extern "C")?
        bool AtFileScope() => stack.All(t => t);
        var stmtStart = 0;
        var parenDepth = 0;
        for (var i = 0; i < toks.Count; i++)
        {
            var s = toks[i].S;
            if (s == "(") { parenDepth++; continue; }
            if (s == ")") { parenDepth = Math.Max(0, parenDepth - 1); continue; }
            if (s == "{")
            {
                // `extern "C" {` keeps file scope (CodeOnly blanks the literal, leaving `extern {`).
                var transparent = parenDepth == 0 && i >= 1 && toks[i - 1].S == "extern";
                stack.Push(transparent);
                if (AtFileScope()) stmtStart = i + 1;
                continue;
            }
            if (s == "}") { if (stack.Count > 0) stack.Pop(); if (AtFileScope()) stmtStart = i + 1; continue; }
            if (s == ";" && parenDepth == 0) { if (AtFileScope()) stmtStart = i + 1; continue; }
            if (parenDepth != 0 || !AtFileScope() || !IsIdent(s) || i + 1 >= toks.Count || toks[i + 1].S != "(") continue;
            if (i > stmtStart && toks[i - 1].S is "=" or "," ) continue;

            // Candidate name: its parameter list, then a body.
            var close = MatchParen(toks, i + 1);
            if (close < 0) continue;
            var brace = BodyAfter(toks, close + 1);
            if (brace < 0) continue;
            // `int add PROTO((int a, int b)) {` — a prototype wrapper (PROTO, __P, _ANSI_ARGS_, PARAMS) around the
            // parameter list: the name is the word before it. Blank `PROTO(` and the outer `)`.
            if (Wrapper.IsMatch(s) && !Known.Contains(s) && i > stmtStart && IsIdent(toks[i - 1].S)
                && toks[i + 2].S == "(" && MatchParen(toks, i + 2) == close - 1)
            {
                if (!HeadPrefix(toks, stmtStart, i - 1)) continue;
                blank.Add((toks[i].Pos, toks[i + 1].Pos + 1));
                blank.Add((toks[close].Pos, toks[close].Pos + 1));
                Plan(text, toks, stmtStart, i - 1, close, brace, blank, wrapper: i);
                i = close;
                continue;
            }
            if (!HeadPrefix(toks, stmtStart, i)) continue;
            Plan(text, toks, stmtStart, i, close, brace, blank);
            // Skip the parameter list: no candidate names inside it.
            i = close;
        }
        if (blank.Count == 0) return text;

        var sb = new StringBuilder(text);
        var cut = new StringBuilder();
        foreach (var (start, end) in blank.OrderBy(b => b.Start))
        {
            cut.Append(text, start, end - start).Append('\n');
            for (var k = start; k < end; k++) if (sb[k] is not ('\n' or '\r')) sb[k] = ' ';
        }
        removed = cut.ToString();
        return sb.ToString();
    }

    private static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_');

    private static int MatchParen(List<(int Pos, string S)> toks, int open)
    {
        var depth = 0;
        for (var k = open; k < toks.Count; k++)
        {
            var s = toks[k].S;
            if (s == "(") depth++;
            else if (s == ")" && --depth == 0) return k;
            else if (s is "{" or "}" or ";") return -1;
        }
        return -1;
    }

    // After the parameter list: identifiers and __attribute__((...)) only, then '{'. Returns the '{' index or -1.
    private static int BodyAfter(List<(int Pos, string S)> toks, int from)
    {
        for (var k = from; k < toks.Count && k < from + 64; k++)
        {
            var s = toks[k].S;
            if (s == "{") return k;
            if (s is "__attribute__" or "__attribute" or "__declspec" && k + 1 < toks.Count && toks[k + 1].S == "(")
            {
                var c = MatchParen(toks, k + 1);
                if (c < 0) return -1;
                k = c;
                continue;
            }
            if (!IsIdent(s)) return -1;
        }
        return -1;
    }

    // The part before the name: identifiers, '*', and macro or attribute calls only.
    private static bool HeadPrefix(List<(int Pos, string S)> toks, int start, int name)
    {
        if (name == start) return true;
        if (toks[start].S == "typedef") return false;
        for (var k = start; k < name; k++)
        {
            var s = toks[k].S;
            if (s == "*") continue;
            if (!IsIdent(s)) return false;
            if (k + 1 < name && toks[k + 1].S == "(")
            {
                var c = MatchParen(toks, k + 1);
                if (c < 0 || c >= name) return false;
                k = c;
            }
        }
        return true;
    }

    private static void Plan(string text, List<(int Pos, string S)> toks, int start, int name, int close, int brace,
                             List<(int, int)> blank, int wrapper = -1)
    {
        // 1. #if/#else choosing part of the type: keep the first branch. The #if can come before the statement's first
        // token, so look from the end of the previous statement; only conditionals around a token of this head count.
        var regionStart = start > 0 ? toks[start - 1].Pos + 1 : 0;
        var prefixEnd = toks[name].Pos;
        var groups = ConditionalGroups(text, DirectiveLines(text, regionStart, prefixEnd))
            .Where(g => Enumerable.Range(start, name - start).Any(k => toks[k].Pos > g.Start && toks[k].Pos < g.End))
            .ToList();
        if (groups.Count > 0)
        {
            for (var k = start; k < name; k++) if (toks[k].S == "(") return;   // a head split across #ifdef: pass 1a's
            foreach (var g in groups) BlankAlternatives(text, g.Lines, blank);
        }

        // 2. ALL-CAPS macro calls before the name and in the parameters: blank the argument lists.
        var calls = new List<(int Open, int Close)>();
        for (var k = start; k < close; k++)
        {
            if (k == name || k == wrapper) continue;
            if (IsIdent(toks[k].S) && AllCaps.IsMatch(toks[k].S) && toks[k + 1].S == "(" && !Known.Contains(toks[k].S))
            {
                var c = MatchParen(toks, k + 1);
                if (c < 0 || c > close) break;
                if (k > name && !(toks[k - 1].S is "(" or ",")) { k = c; continue; }   // a parameter's type position only
                calls.Add((k + 1, c));
                k = c;
            }
        }
        foreach (var (o, c) in calls) blank.Add((toks[o].Pos, toks[c].Pos + 1));

        // 3. Identifiers the type does not need, between the type and the name.
        var idents = new List<int>();
        var typed = false;
        for (var k = start; k < name; k++)
        {
            var s = toks[k].S;
            if (s == "(") { k = MatchParen(toks, k); if (k < 0) return; continue; }
            if (!IsIdent(s)) continue;
            if (s is "struct" or "union" or "enum") { typed = true; k++; continue; }   // and its tag
            if (Primitive.Contains(s)) { typed = true; continue; }
            if (Known.Contains(s)) { if (s is "__attribute__" or "__attribute" or "__declspec") SkipCall(toks, ref k); continue; }
            if (k + 1 < name && toks[k + 1].S == "(" && !AllCaps.IsMatch(s)) return;      // something else
            idents.Add(k);
        }
        var extra = typed ? idents : idents.Skip(1).ToList();
        foreach (var k in extra)
        {
            var endPos = toks[k].Pos + toks[k].S.Length;
            blank.Add((toks[k].Pos, endPos));
            // A blanked macro call's name goes with its (already blanked) arguments.
        }

        // 4. Directive lines between ')' and '{' (#pragma, attribute #ifs): blanked.
        foreach (var (ls, le) in DirectiveLines(text, toks[close].Pos + 1, toks[brace].Pos)) blank.Add((ls, le));
    }

    private static void SkipCall(List<(int Pos, string S)> toks, ref int k)
    {
        if (k + 1 < toks.Count && toks[k + 1].S == "(") { var c = MatchParen(toks, k + 1); if (c > 0) k = c; }
    }

    // Directive lines (with continuations) wholly inside [from, to) of the original text, as [start, end) ranges.
    private static List<(int Start, int End)> DirectiveLines(string text, int from, int to)
    {
        var found = new List<(int, int)>();
        var ls = text.LastIndexOf('\n', Math.Max(0, from - 1)) + 1;
        if (ls < from) ls = text.IndexOf('\n', from) is var n && n >= 0 ? n + 1 : text.Length;
        while (ls < to)
        {
            var le = text.IndexOf('\n', ls);
            if (le < 0) le = text.Length;
            var line = text.AsSpan(ls, le - ls);
            if (line.TrimStart().StartsWith("#"))
            {
                var end = le;
                while (end < text.Length && text.AsSpan(ls, end - ls).TrimEnd().EndsWith("\\"))
                {
                    var next = text.IndexOf('\n', end + 1);
                    end = next < 0 ? text.Length : next;
                }
                if (end <= to) found.Add((ls, end));
                le = end;
            }
            ls = le + 1;
        }
        return found;
    }

    private static string WordOf(string text, (int Start, int End) line) => Word(text.AsSpan(line.Start, line.End - line.Start).TrimStart()[1..]);

    // Top-level #if ... #endif groups among the directive lines: (start, end, its conditional lines).
    private static List<(int Start, int End, List<(int Start, int End)> Lines)> ConditionalGroups(string text, List<(int Start, int End)> dirs)
    {
        var groups = new List<(int, int, List<(int, int)>)>();
        List<(int, int)>? cur = null;
        var depth = 0;
        foreach (var d in dirs)
        {
            var word = WordOf(text, d);
            if (word is "if" or "ifdef" or "ifndef") { if (depth++ == 0) cur = new List<(int, int)>(); }
            if (cur is null) continue;
            if (word is "if" or "ifdef" or "ifndef" or "elif" or "else" or "endif") cur.Add(d);
            if (word == "endif" && --depth == 0) { groups.Add((cur[0].Item1, d.End, cur)); cur = null; }
        }
        return groups;
    }

    // One #if ... #else ... #endif group: blank its conditional lines and every line of the branches after the first.
    private static void BlankAlternatives(string text, List<(int Start, int End)> lines, List<(int, int)> blank)
    {
        var depth = 0;
        var skipFrom = -1;
        foreach (var (s, e) in lines)
        {
            var word = WordOf(text, (s, e));
            if (word is "if" or "ifdef" or "ifndef") depth++;
            else if (word is "elif" or "else" && depth == 1 && skipFrom < 0) skipFrom = s;
            else if (word == "endif")
            {
                if (depth == 1 && skipFrom >= 0) { blank.Add((skipFrom, s)); skipFrom = -1; }
                depth = Math.Max(0, depth - 1);
            }
            blank.Add((s, e));
        }
    }

    private static string Word(ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        var n = 0;
        while (n < s.Length && char.IsLetter(s[n])) n++;
        return s[..n].ToString();
    }
}
