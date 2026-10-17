using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Functions whose NAME a macro computes from the includer's macros: a "template header" instantiated once per
/// <c>#include</c>, <c>#define TNAME red</c> / <c>#include "tmpl.h"</c> where tmpl.h has
/// <c>int CAT(TNAME, get)(void) { ... }</c>. The symbol <c>red_get</c> is defined by the translation unit that includes
/// the header, and no parser reading either file alone can see the name. <see cref="Find"/> walks a translation unit's
/// directives in order (#define, #undef, quoted #include, recursively) like the preprocessor, and expands every
/// macro-named definition head it meets. Conditionals are not evaluated: a macro defined inside an #if keeps its
/// earlier values as alternatives, and a head is expanded with each (more names, never fewer).
/// </summary>
public static class TemplateInstances
{
    /// <summary>A definition head whose name is a macro call: <c>NAME(args)(params) {</c>.</summary>
    static readonly Regex MacroNamedHead = new(@"\b([A-Za-z_]\w*)\s*\(((?:[^()\n]|\([^()\n]*\))*)\)\s*\([^(){};]*\)\s*\{", RegexOptions.Compiled);

    /// <summary>Past this many combinations of alternative macro values, each alternative is tried alone.</summary>
    public const int MaxWorlds = 64;

    /// <summary>Quick gate: could <paramref name="text"/> hold a macro-named definition head at all?</summary>
    public static bool HasMacroNamedHead(string text) => text.Contains(')') && MacroNamedHead.IsMatch(text);

    // Looser than MacroNamedHead: `NAME(args)` then `(`, with directive lines (and their continuations) allowed in the
    // gaps - Find reads code with its directive lines blanked, so a head split by an #if is one there. The gaps are atomic
    // (no gap can hold a '(' to give back), so a long run of directive lines is not tried every way it splits.
    static readonly Regex LooseHead = new(
        @"\b[A-Za-z_]\w*(?>(?:\s+|#(?:[^\\\n]|\\(?:\r?\n|.))*)*)\((?:[^()\n]|\([^()\n]*\))*\)(?>(?:\s+|#(?:[^\\\n]|\\(?:\r?\n|.))*)*)\(", RegexOptions.Compiled);

    /// <summary>False only when <see cref="Find"/> can find no head in this text itself (its includes aside).</summary>
    public static bool MayHaveMacroNamedHead(string text) => text.Contains(')') && LooseHead.IsMatch(text);

    // A quoted #include as Parse reads one (#, spaces, include, spaces, then "name"), with line continuations and
    // comments allowed in the gaps: a name too many costs a walk, a name missed would skip one that finds something.
    static readonly Regex QuotedInclude = new(
        @"#(?:[ \t]|\\\r?\n|/\*.*?\*/)*include\b(?:[ \t]|\\\r?\n|/\*.*?\*/)*""([^""\r\n]*)""", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Every name a quoted #include in <paramref name="text"/> may name: all those <see cref="Find"/> follows.</summary>
    public static string[] QuotedIncludeNames(string text)
    {
        if (!text.Contains("include", StringComparison.Ordinal)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (Match m in QuotedInclude.Matches(text)) names.Add(m.Groups[1].Value);
        return names.Count == 0 ? Array.Empty<string>() : names.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The names a translation unit defines through macro-named heads, with the 1-based line in the unit
    /// (the #include that instantiated it, or the head itself). <paramref name="readInclude"/> returns a quoted
    /// include's text, or null when it can't be found; for a header with no macro-named head it may return only its
    /// directive lines (<see cref="DirectivesOnly"/>), and it should return the same string object for the same header
    /// (a header with no macro-named head is read once per unit). <paramref name="initial"/>: macros defined before the
    /// unit (the build's command-line <c>-D</c>), so <c>int HIDE(den)(void) {</c> under <c>-D'HIDE(n)=hid_##n'</c>
    /// defines hid_den. <paramref name="parsing"/>: called with the length of each header about to be parsed for the first
    /// time (its parse is kept with the header string), the result disposed when done: the caller can hold back parses.</summary>
    public static List<(string Name, int Line)> Find(string unit, Func<string, string?> readInclude,
                                                     IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? initial = null,
                                                     Func<int, IDisposable?>? parsing = null)
    {
        var found = new List<(string, int)>();
        if (!unit.Contains('#') && (initial is null || initial.Count == 0)) return found;
        var macros = new Dictionary<string, List<Macro>>(StringComparer.Ordinal);
        if (initial is not null) foreach (var kv in initial) if (kv.Value.Count > 0) macros[kv.Key] = kv.Value.Select(v => new Macro(v.Params, v.Body)).ToList();
        var w = new WalkState(macros, readInclude, found) { Parsing = parsing };
        Walk(Parse(unit, false), w, unitLine: null, depth: 0, ifBase: 0);
        return found.Distinct().ToList();
    }

    /// <summary>Only the directive lines of <paramref name="text"/>, continuations joined: everything <see cref="Find"/>
    /// reads in a header with no macro-named head.</summary>
    public static string DirectivesOnly(string text)
    {
        // Each piece copied once: a directive continued over many lines was copied again for each line it took in
        // (gigabytes for a tree of register-map headers). Measured first, then written at its size: a StringBuilder's
        // chunks and its ToString came to twice the text, for each header a walk reads again (C22). A text that is
        // all directive lines ending in '\n', none continued, is its own answer.
        var length = DirectivesOnly(text, default);
        if (length == text.Length && Same(text)) return text;
        return string.Create(length, text, (dst, t) => DirectivesOnly(t, dst));
    }

    // The directive lines of text (as DirectivesOnly), written to dst when given; their length.
    static int DirectivesOnly(string text, Span<char> dst)
    {
        var n = 0;
        void Put(ReadOnlySpan<char> s, Span<char> d) { if (!d.IsEmpty) s.CopyTo(d[n..]); n += s.Length; }
        for (var start = 0; start <= text.Length; )
        {
            var (end, next) = Line(text, start);
            var k = start;
            while (k < end && char.IsWhiteSpace(text[k])) k++;
            if (k < end && text[k] == '#')
            {
                // A line ending in a backslash takes in the next one, the backslash becoming a space.
                while (end > start && text[end - 1] == '\\' && next <= text.Length)
                {
                    Put(text.AsSpan(start, end - 1 - start), dst);
                    Put(" ", dst);
                    start = next;
                    (end, next) = Line(text, start);
                }
                Put(text.AsSpan(start, end - start), dst);
                Put("\n", dst);
            }
            start = next;
        }
        return n;
    }

    // Is text exactly its directive lines? (Same length was checked: every line kept whole, so only a '\r' or a
    // continuation, which DirectivesOnly drops or joins, could differ.)
    static bool Same(string text) => !text.Contains('\r') && !text.Contains("\\\n", StringComparison.Ordinal) && text.EndsWith('\n');

    // The line from start: where its text ends (before the '\n' and the '\r's before it), and where the next line
    // starts (past the end of the text for the last line).
    static (int End, int Next) Line(string text, int start)
    {
        var nl = text.IndexOf('\n', start);
        var end = nl < 0 ? text.Length : nl;
        var next = nl < 0 ? text.Length + 1 : nl + 1;
        while (end > start && text[end - 1] == '\r') end--;
        return (end, next);
    }

    sealed record Macro(List<string>? Params, string Body);

    /// <summary>A walk's macros are not copied into a table of its own (every #define of every header a unit reached:
    /// hundreds of MB a unit in a tree of register maps). The walk keeps the stretches of text it has walked, in order
    /// (<see cref="Trail"/>), and a name is looked up back along them: the value list a table would hold.</summary>
    sealed record WalkState(Dictionary<string, List<Macro>> Initial, Func<string, string?> ReadInclude, List<(string, int)> Found)
    {
        public Func<int, IDisposable?>? Parsing { get; init; }
        public readonly HashSet<string> Walked = new(ReferenceEqualityComparer.Instance);
        public readonly List<Stretch> Trail = new();
        // Answers since the trail last grew (Walk clears it).
        public readonly Dictionary<string, List<Macro>?> Asked = new(StringComparer.Ordinal);

        public List<Macro>? Lookup(string name)
        {
            if (!Asked.TryGetValue(name, out var l)) Asked[name] = l = Resolve(name);
            return l;
        }

        // A define at #if depth 0 replaces the value, one inside an #if is an alternative to it (not twice the same),
        // an #undef at depth 0 removes it: going back to the first that settles it, then forward as a table would.
        List<Macro>? Resolve(string name)
        {
            var back = new List<(Macro Macro, bool InIf)>();
            var settled = false;
            for (var t = Trail.Count - 1; t >= 0 && !settled; t--)
            {
                var st = Trail[t];
                var p = st.Parsed;
                var ix = p.Index;
                if (!ix.Last.TryGetValue(string.GetHashCode(name.AsSpan()), out var i)) continue;
                for (; i >= st.From; i = ix.Prev[i])
                {
                    // The index goes by the name's hash: an op of another name with the same hash is passed over.
                    if (i >= st.To || !p.NameOf(i).SequenceEqual(name)) continue;
                    var op = p.Ops[i];
                    var inIf = st.IfBase + ix.Depth[i] > 0;
                    if (op.Kind == OpKind.Undef) { if (inIf) continue; settled = true; break; }
                    back.Add((p.Directive(i).Macro!, inIf));
                    if (!inIf) { settled = true; break; }
                }
            }
            List<Macro>? cur = !settled && Initial.TryGetValue(name, out var init) ? new List<Macro>(init) : null;
            for (var k = back.Count - 1; k >= 0; k--)
            {
                var (m, inIf) = back[k];
                if (inIf && cur is not null)
                {
                    if (!cur.Any(a => a.Body == m.Body && (a.Params is null) == (m.Params is null))) cur.Add(m);
                }
                else cur = new List<Macro> { m };
            }
            return cur;
        }
    }

    /// <summary>Ops <c>From..To</c> (exclusive) of a parsed text, walked at #if depth <c>IfBase</c>.</summary>
    sealed class Stretch(Parsed parsed, int ifBase, int from)
    {
        public readonly Parsed Parsed = parsed;
        public readonly int IfBase = ifBase, From = from;
        public int To = from;
    }

    /// <summary>A parsed text's defines and undefs by the hash of their name (no string per name: a register map
    /// has tens of thousands): the last op of each hash, each op's previous one of its hash (-1 for none), and the #if
    /// depth each op is at (within the text).</summary>
    sealed class DefineIndex
    {
        public readonly Dictionary<int, int> Last = new();
        public readonly int[] Prev, Depth;

        public DefineIndex(Parsed p)
        {
            var ops = p.Ops;
            Prev = new int[ops.Length];
            Depth = new int[ops.Length];
            var depth = 0;
            for (var i = 0; i < ops.Length; i++)
            {
                var op = ops[i];
                Depth[i] = depth;
                Prev[i] = -1;
                if (op.Kind == OpKind.Open) depth++;
                else if (op.Kind == OpKind.Close) depth = Math.Max(0, depth - 1);
                else if (op.Kind is OpKind.Define or OpKind.Undef && p.HasName(i))
                {
                    var h = string.GetHashCode(p.NameOf(i));
                    Prev[i] = Last.TryGetValue(h, out var q) ? q : -1;
                    Last[h] = i;
                }
            }
        }
    }

    enum OpKind { Code, Other, Open, Close, Define, Undef, Include }

    /// <summary>One line of a text, or one directive with its continuations (lines <c>First..Last</c>, 0-based, from
    /// character <c>Start</c> to <c>End</c>), as <see cref="Walk"/> reads it. Only where it is: the name, include and
    /// macro are read again from the text when asked (<see cref="Parsed.Directive"/>), a copy of each was 5 times the
    /// header, kept for as long as the header is.</summary>
    readonly record struct Op(OpKind Kind, int First, int Last, int Start, int End);

    /// <summary>What a directive says: its name (or include), and for a #define with one, the macro.</summary>
    readonly record struct Directive(OpKind Kind, string? Text, Macro? Macro);

    /// <summary>A text split into <see cref="Op"/>s, and whether it holds a macro-named head (a template header).</summary>
    sealed record Parsed(string Text, Op[] Ops, bool IsTemplate)
    {
        DefineIndex? _index;
        public DefineIndex Index => LazyInitializer.EnsureInitialized(ref _index, () => new DefineIndex(this));

        /// <summary>The op's text, continuations joined (a backslash and line end becoming a space).</summary>
        public ReadOnlySpan<char> Raw(int i)
        {
            var op = Ops[i];
            if (op.First == op.Last) return Text.AsSpan(op.Start, op.End - op.Start);
            return JoinContinued(Text, op.Start, op.Last - op.First);
        }

        public Directive Directive(int i) => ParseDirective(Raw(i));

        /// <summary>A #define with a name, or an #undef.</summary>
        public bool HasName(int i) => Ops[i].Kind == OpKind.Undef || !NameOf(i).IsEmpty;

        /// <summary>The name of a #define or #undef, read in place unless a comment or continuation could change it.</summary>
        public ReadOnlySpan<char> NameOf(int i)
        {
            var op = Ops[i];
            if (op.First == op.Last && FastName(Text.AsSpan(op.Start, op.End - op.Start), op.Kind, out var name)) return name;
            return Directive(i).Text;
        }
    }

    // The joined text of a directive starting at start and continued over lines more lines.
    static string JoinContinued(string text, int start, int lines)
    {
        var (end, next) = Line(text, start);
        var sb = new StringBuilder(end - start + 64);
        sb.Append(text, start, end - start);
        for (var n = 0; n < lines && sb.Length > 0 && sb[^1] == '\\' && next <= text.Length; n++)
        {
            sb.Length--;
            (end, next) = Line(text, start = next);
            sb.Append(' ').Append(text, start, end - start);
        }
        return sb.ToString();
    }

    // Per header string (readInclude returns the same string for the same header): parsed once for every unit. Behind a
    // Lazy: the table runs its factory on every thread that asks before one has finished, and the check's scans ask
    // for the same big headers on dozens of threads at once.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, Lazy<Parsed>> s_parsed = new();

    static Parsed ParseOf(string header, Func<int, IDisposable?>? parsing)
        => s_parsed.GetValue(header, t => new Lazy<Parsed>(() =>
        {
            using var slot = parsing?.Invoke(t.Length);
            return Parse(t, HasMacroNamedHead(t));
        })).Value;

    // Read by hand, not by the Directive/Conditional/DefineHead regexes: a Match and its Groups for every #define line
    // came to 25 times the header (C20/C21: a walk re-parses every header the check could not hold, 64 walks at once).
    // Same answers as those regexes. Nothing is copied out but continued directives (to read their kind).
    static Parsed Parse(string text, bool isTemplate)
    {
        var ops = new List<Op>(text.Length / 40 + 16);
        var i = -1;   // the 0-based line, as a split on '\n' numbers it
        for (var start = 0; start <= text.Length; )
        {
            var (end, next) = Line(text, start);
            i++;
            var first = i;
            var k = start;
            while (k < end && char.IsWhiteSpace(text[k])) k++;
            if (k >= end || text[k] != '#')
            {
                ops.Add(new Op(OpKind.Code, i, i, start, end));
                start = next;
                continue;
            }
            var opStart = start;
            if (!(end > start && text[end - 1] == '\\' && next <= text.Length))
                ops.Add(new Op(Head(text.AsSpan(start, end - start)).Kind, first, i, opStart, end));
            else
            {
                // As JoinContinued: each line ending in a backslash takes in the next.
                var lastEnd = end;
                while (lastEnd > start && text[lastEnd - 1] == '\\' && next <= text.Length)
                {
                    (lastEnd, next) = Line(text, start = next);
                    i++;
                }
                ops.Add(new Op(Head(JoinContinued(text, opStart, i - first)).Kind, first, i, opStart, lastEnd));
            }
            start = next;
        }
        return new Parsed(text, ops.ToArray(), isTemplate);
    }

    /// <summary>Tests: each op of <see cref="Parse"/> as "kind first-last text (params) body"; throws where the name
    /// read in place is not the one <see cref="ParseDirective"/> reads.</summary>
    internal static IEnumerable<string> DescribeParse(string text)
    {
        var p = Parse(text, false);
        for (var i = 0; i < p.Ops.Length; i++)
        {
            var o = p.Ops[i];
            var d = o.Kind == OpKind.Code ? new Directive(OpKind.Code, p.Raw(i).ToString(), null) : p.Directive(i);
            if (d.Kind != o.Kind) throw new InvalidOperationException($"op {i}: kind {o.Kind}, directive {d.Kind}");
            if (o.Kind is OpKind.Define or OpKind.Undef && p.HasName(i) && !p.NameOf(i).SequenceEqual(d.Text))
                throw new InvalidOperationException($"op {i}: name [{p.NameOf(i).ToString()}], directive [{d.Text}]");
            yield return $"{o.Kind} {o.First}-{o.Last} [{d.Text}] "
                         + (d.Macro is { } m ? $"({(m.Params is null ? "-" : string.Join(",", m.Params))}) [{m.Body}]" : "");
        }
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>A directive line's kind, as the Directive/Conditional regexes read it, and where the text after a
    /// define/undef/include word (and the blanks after it) starts (-1 for other kinds).</summary>
    static (OpKind Kind, int After) Head(ReadOnlySpan<char> raw)
    {
        var k = 0;
        while (k < raw.Length && raw[k] is ' ' or '\t') k++;
        if (k >= raw.Length || raw[k] != '#') return (OpKind.Other, -1);
        k++;
        while (k < raw.Length && raw[k] is ' ' or '\t') k++;
        var w = k;
        while (k < raw.Length && IsWord(raw[k])) k++;
        var word = raw[w..k];
        if (word is "if" or "ifdef" or "ifndef") return (OpKind.Open, -1);
        if (word is "endif") return (OpKind.Close, -1);
        var kind = word switch { "define" => OpKind.Define, "undef" => OpKind.Undef, "include" => OpKind.Include, _ => OpKind.Other };
        if (kind == OpKind.Other) return (kind, -1);
        while (k < raw.Length && raw[k] is ' ' or '\t') k++;
        return (kind, k);
    }

    /// <summary>A directive line (continuations joined), as the Directive/Conditional/DefineHead regexes read it.</summary>
    static Directive ParseDirective(ReadOnlySpan<char> raw)
    {
        var (kind, r) = Head(raw);
        if (r < 0) return new Directive(kind, null, null);
        var rest = raw[r..];
        if (rest.IndexOf("//", StringComparison.Ordinal) is var c and >= 0) rest = rest[..c];
        if (rest.Contains("/*", StringComparison.Ordinal)) rest = StripComment(rest.ToString());
        rest = rest.Trim();
        switch (kind)
        {
            case OpKind.Define:
            {
                if (rest.Length == 0 || !(char.IsAsciiLetter(rest[0]) || rest[0] == '_')) return new Directive(kind, null, null);
                var n = 1;
                while (n < rest.Length && IsWord(rest[n])) n++;
                var name = rest[..n].ToString();
                List<string>? ps = null;
                var b = n;
                if (n < rest.Length && rest[n] == '(' && rest[(n + 1)..].IndexOf(')') is var close and >= 0)
                {
                    ps = rest.Slice(n + 1, close).ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                    b = n + 1 + close + 1;
                }
                while (b < rest.Length && rest[b] is ' ' or '\t') b++;
                return new Directive(kind, name, new Macro(ps, rest[b..].Trim().ToString()));
            }
            case OpKind.Undef:
                var sp = rest.IndexOfAny(' ', '\t');
                return new Directive(kind, (sp < 0 ? rest : rest[..sp]).ToString(), null);
            default:   // include
                var q = rest.Length >= 2 && rest[0] == '"' ? rest[1..].IndexOf('"') : -1;
                return new Directive(kind, q < 0 ? null : rest.Slice(1, q).ToString(), null);
        }
    }

    /// <summary>The name <see cref="ParseDirective"/> gives a one-line #define (empty: none) or #undef, read in place;
    /// false when a comment at or before the name's end could change it (a stripped comment leaves a blank, so one
    /// after it can't).</summary>
    static bool FastName(ReadOnlySpan<char> raw, OpKind kind, out ReadOnlySpan<char> name)
    {
        name = default;
        var (_, r) = Head(raw);
        if (r < 0) return false;
        var rest = raw[r..];
        if (rest.IndexOf("//", StringComparison.Ordinal) is var c and >= 0) rest = rest[..c];
        var trimmed = rest.Trim();
        var s = rest.Length - rest.TrimStart().Length;   // where trimmed starts in rest
        var n = 0;
        if (kind == OpKind.Define)
        {
            if (trimmed.Length > 0 && (char.IsAsciiLetter(trimmed[0]) || trimmed[0] == '_'))
                for (n = 1; n < trimmed.Length && IsWord(trimmed[n]); n++) { }
        }
        else n = trimmed.IndexOfAny(' ', '\t') is var sp and >= 0 ? sp : trimmed.Length;
        if (rest.IndexOf("/*", StringComparison.Ordinal) is var cm and >= 0 && cm <= s + n) return false;
        name = trimmed[..n];
        return true;
    }

    static void Walk(Parsed parsed, WalkState w, int? unitLine, int depth, int ifBase)
    {
        var stretch = new Stretch(parsed, ifBase, 0);
        w.Trail.Add(stretch);
        var code = new StringBuilder();   // the non-directive text since the last define/undef/include, line for line
        var codeStartLine = 1;
        var ifDepth = ifBase;   // an #if around the #include that brought this text in counts too
        void Flush()
        {
            if (code.Length == 0) return;
            var s = code.ToString();
            code.Clear();
            if (!s.Contains('(')) return;
            w.Asked.Clear();   // the trail has grown since
            int at = 0, lines = 0;   // newlines before the match, counted on from the last (a copy of s up to each match is the square of it)
            foreach (Match m in MacroNamedHead.Matches(s))
            {
                if (w.Lookup(m.Groups[1].Value) is null) continue;
                lines += s.AsSpan(at, m.Index - at).Count('\n');
                at = m.Index;
                var line = unitLine ?? codeStartLine + lines;
                foreach (var name in Names(m.Groups[1].Value + "(" + m.Groups[2].Value + ")", w.Lookup)) w.Found.Add((name, line));
            }
        }
        for (var k = 0; k < parsed.Ops.Length; k++)
        {
            var op = parsed.Ops[k];
            stretch.To = k;
            switch (op.Kind)
            {
                case OpKind.Code:
                    if (code.Length == 0) codeStartLine = op.First + 1;
                    code.Append(parsed.Text, op.Start, op.End - op.Start).Append('\n');
                    continue;
                case OpKind.Other or OpKind.Open or OpKind.Close:
                    if (op.Kind == OpKind.Open) ifDepth++;
                    else if (op.Kind == OpKind.Close) ifDepth = Math.Max(ifBase, ifDepth - 1);
                    // Keep the line count, so a head after this directive gets its own line.
                    if (code.Length == 0) codeStartLine = op.First + 1;
                    code.Append('\n', op.Last - op.First + 1);
                    continue;
            }
            Flush();
            codeStartLine = op.Last + 2;
            switch (op.Kind)
            {
                // A define or undef is on the trail (stretch) from the next op on; WalkState.Resolve reads it there.
                case OpKind.Include:
                    if (depth >= 8 || parsed.Directive(k).Text is not { } incName) break;
                    var inc = w.ReadInclude(incName);
                    if (inc is null) break;
                    // A template header is instantiated by every #include; any other header only adds macros, once.
                    var p = ParseOf(inc, w.Parsing);
                    if (!p.IsTemplate && !w.Walked.Add(inc)) break;
                    stretch.To = k + 1;
                    Walk(p, w, unitLine ?? op.First + 1, depth + 1, ifDepth);
                    w.Trail.Add(stretch = new Stretch(parsed, ifBase, k + 1));
                    break;
            }
        }
        stretch.To = parsed.Ops.Length;
        Flush();
    }

    /// <summary>The names <paramref name="head"/> expands to. An expansion depends on the macros only through the
    /// names it looks up, so the worlds vary only the macros with alternatives that some expansion looked up, grown
    /// until none looks up another: the names of every world, without a world each for config macros the head never
    /// reaches.</summary>
    static List<string> Names(string head, Func<string, List<Macro>?> macros)
    {
        var vary = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var asked = new HashSet<string>(StringComparer.Ordinal);
            var names = new List<string>();
            foreach (var world in Worlds(macros, vary))
            {
                var name = Expand(head, n => { asked.Add(n); return world(n); }).Trim();
                if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) names.Add(name);
            }
            var before = vary.Count;
            foreach (var n in asked) if (macros(n) is { Count: > 1 }) vary.Add(n);
            if (vary.Count == before) return names;
        }
    }

    /// <summary>Lookups for every combination of the alternative values of the macros in <paramref name="vary"/> (the
    /// latest values first), or past <see cref="MaxWorlds"/>, the latest values with each alternative alone.</summary>
    static List<Func<string, Macro?>> Worlds(Func<string, List<Macro>?> macros, HashSet<string> vary)
    {
        Macro? Last(string n) => macros(n) is { } l ? l[^1] : null;
        var worlds = new List<Func<string, Macro?>> { Last };
        if (vary.Count == 0) return worlds;
        var multi = vary.Select(n => KeyValuePair.Create(n, macros(n)!)).Where(kv => kv.Value is { Count: > 1 }).ToList();
        if (multi.Count == 0) return worlds;
        long product = 1;
        foreach (var kv in multi) { product *= kv.Value.Count; if (product > MaxWorlds) break; }
        if (product <= MaxWorlds)
        {
            var picks = new List<Dictionary<string, Macro>> { new(StringComparer.Ordinal) };
            foreach (var kv in multi)
                picks = picks.SelectMany(p => kv.Value.Select(v => new Dictionary<string, Macro>(p, StringComparer.Ordinal) { [kv.Key] = v })).ToList();
            foreach (var p in picks) worlds.Add(n => p.TryGetValue(n, out var v) ? v : Last(n));
        }
        else
            foreach (var kv in multi)
                foreach (var v in kv.Value)
                {
                    var (key, val) = (kv.Key, v);
                    worlds.Add(n => n == key ? val : Last(n));
                }
        return worlds;
    }

    static string StripComment(string s)
    {
        var c = s.IndexOf("//", StringComparison.Ordinal);
        if (c >= 0) s = s[..c];
        return s.Contains("/*", StringComparison.Ordinal) ? Regex.Replace(s, @"/\*.*?\*/", " ") : s;
    }

    static readonly Regex Token = new(@"[A-Za-z_]\w*|\d\w*|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|##|\S", RegexOptions.Compiled);

    /// <summary>Past this many tokens an expansion stops growing (object-like chains like <c>A (B+B)</c>,
    /// <c>B (C+C)</c>... double at every level).</summary>
    const int MaxTokens = 4096;

    /// <summary>Expands macros in <paramref name="s"/> (object-like and function-like, # and ##, rescanning, with a
    /// guard against self-reference). Enough to compute a name; not a full preprocessor.</summary>
    public static string Expand(string s, IReadOnlyDictionary<string, (List<string>? Params, string Body)> macros)
    {
        var m = macros.ToDictionary(kv => kv.Key, kv => new Macro(kv.Value.Params, kv.Value.Body), StringComparer.Ordinal);
        return Expand(s, n => m.TryGetValue(n, out var v) ? v : null);
    }

    static string Expand(string s, Func<string, Macro?> macros)
        => string.Join(" ", ExpandTokens(Token.Matches(s).Select(m => m.Value).ToList(), macros, new HashSet<string>(StringComparer.Ordinal), 0));

    static List<string> ExpandTokens(List<string> toks, Func<string, Macro?> macros, HashSet<string> hide, int depth)
    {
        var output = new List<string>();
        if (depth > 32 || toks.Count > MaxTokens) { output.AddRange(toks); return output; }
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (output.Count > MaxTokens || hide.Contains(t) || macros(t) is not { } m) { output.Add(t); continue; }
            if (m.Params is null)
            {
                var inner = new HashSet<string>(hide, StringComparer.Ordinal) { t };
                output.AddRange(ExpandTokens(Substitute(m, new List<List<string>>(), macros, hide, depth), macros, inner, depth + 1));
                continue;
            }
            if (i + 1 >= toks.Count || toks[i + 1] != "(") { output.Add(t); continue; }
            // Collect the arguments.
            var args = new List<List<string>> { new() };
            var level = 0; var j = i + 2;
            for (; j < toks.Count; j++)
            {
                var a = toks[j];
                if (a == "(") level++;
                else if (a == ")") { if (level == 0) break; level--; }
                else if (a == "," && level == 0) { args.Add(new List<string>()); continue; }
                args[^1].Add(a);
            }
            if (j >= toks.Count) { output.Add(t); continue; }
            var hidden = new HashSet<string>(hide, StringComparer.Ordinal) { t };
            output.AddRange(ExpandTokens(Substitute(m, args, macros, hide, depth), macros, hidden, depth + 1));
            i = j;
        }
        return output;
    }

    static List<string> Substitute(Macro m, List<List<string>> args, Func<string, Macro?> macros, HashSet<string> hide, int depth)
    {
        var body = Token.Matches(m.Body).Select(x => x.Value).ToList();
        var ps = m.Params ?? new List<string>();
        int Param(string tok)
        {
            var k = ps.IndexOf(tok);
            if (k < 0 && tok == "__VA_ARGS__") k = ps.FindIndex(p => p == "...");
            return k;
        }
        List<string> Arg(int k)
        {
            if (k < 0) return new List<string>();
            if (ps[k] == "..." ) return args.Skip(k).SelectMany((a, n) => n == 0 ? a : a.Prepend(",")).ToList();
            return k < args.Count ? args[k] : new List<string>();
        }
        var outp = new List<string>();
        for (var i = 0; i < body.Count; i++)
        {
            var t = body[i];
            if (t == "#" && i + 1 < body.Count && Param(body[i + 1]) is var sp and >= 0)
            {
                outp.Add("\"" + string.Join(" ", Arg(sp)).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                i++;
                continue;
            }
            var k = Param(t);
            if (k < 0) { outp.Add(t); continue; }
            var pasted = (i > 0 && body[i - 1] == "##") || (i + 1 < body.Count && body[i + 1] == "##");
            outp.AddRange(pasted ? Arg(k) : ExpandTokens(Arg(k), macros, hide, depth + 1));
        }
        // Token pasting.
        for (var i = 0; i < outp.Count; i++)
        {
            if (outp[i] != "##") continue;
            var left = i > 0 ? outp[i - 1] : "";
            var right = i + 1 < outp.Count ? outp[i + 1] : "";
            var joined = left + right;
            var start = Math.Max(0, i - 1);
            var count = Math.Min(outp.Count, i + 2) - start;
            outp.RemoveRange(start, count);
            outp.Insert(start, joined);
            i = start;
        }
        return outp;
    }
}
