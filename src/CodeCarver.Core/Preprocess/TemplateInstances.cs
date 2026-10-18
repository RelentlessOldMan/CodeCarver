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
    public static bool HasMacroNamedHead(string text) => ClosesIntoBody(text) && MacroNamedHead.IsMatch(text);

    /// <summary>False when no <c>)</c> is followed by whitespace and <c>{</c>, which every MacroNamedHead match ends with:
    /// the regex is tried from every name in the text, and most code read by a walk has none (40% of a walk was that
    /// regex). Whitespace as the regex <c>\s</c> reads it.</summary>
    static bool ClosesIntoBody(string text)
    {
        for (var i = text.IndexOf('{'); i >= 0; i = text.IndexOf('{', i + 1))
        {
            var k = i - 1;
            while (k >= 0 && char.IsWhiteSpace(text[k])) k--;
            if (k >= 0 && text[k] == ')') return true;
        }
        return false;
    }

    /// <summary>False only when <see cref="Find"/> can find no head in this text itself (its includes aside).</summary>
    /// <remarks>Looser than MacroNamedHead: `NAME(args)` then `(`, with directive lines (and their continuations) allowed
    /// in the gaps - Find reads code with its directive lines blanked, so a head split by an #if is one there. Read by
    /// hand, as the regex <c>\b[A-Za-z_]\w*GAP\((?:[^()\n]|\([^()\n]*\))*\)GAP\(</c> with GAP the atomic
    /// <c>(?>(?:\s+|#(?:[^\\\n]|\\(?:\r?\n|.))*)*)</c> read it, but with each gap's end found once: the regex scanned a
    /// gap again from every name before it, so a header of n #define lines with no head cost n² (8,000 lines: 4 s).</remarks>
    public static bool MayHaveMacroNamedHead(string text)
    {
        if (!text.Contains(')')) return false;
        var gapEnd = new Dictionary<int, int>(); // where the gap through each '#' read so far ends
        var hashes = new List<int>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') || (i > 0 && IsWordChar(text[i - 1]))) { i++; continue; }
            var e = i + 1;
            while (e < text.Length && IsWordChar(text[e])) e++;
            i = e;
            var p = GapEnd(text, e, gapEnd, hashes);
            if (p >= text.Length || text[p] != '(') continue;
            p = ArgsEnd(text, p + 1);
            if (p < 0) continue;
            p = GapEnd(text, p, gapEnd, hashes);
            if (p < text.Length && text[p] == '(') return true;
        }
        return false;
    }

    // A char \w reads (letters, nonspacing marks, digits, connectors); never more than \w, so no name is cut short.
    static bool IsWordChar(char c) => c < 128
        ? c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'
        : char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.UppercaseLetter
            or System.Globalization.UnicodeCategory.LowercaseLetter or System.Globalization.UnicodeCategory.TitlecaseLetter
            or System.Globalization.UnicodeCategory.ModifierLetter or System.Globalization.UnicodeCategory.OtherLetter
            or System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.DecimalDigitNumber or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    // Where the gap from p ends: whitespace and directive lines (# to an unescaped newline). A gap's end is kept at each
    // '#' it read: a gap from a name inside a directive line meets the outer gap at the next '#' at the latest, so each
    // directive line is read about once whichever name the gap follows.
    static int GapEnd(string text, int p, Dictionary<int, int> memo, List<int> hashes)
    {
        hashes.Clear();
        while (p < text.Length)
        {
            var c = text[p];
            if (char.IsWhiteSpace(c)) { p++; continue; }
            if (c != '#') break;
            if (memo.TryGetValue(p, out var end)) { p = end; break; }
            hashes.Add(p);
            p++;
            while (p < text.Length)
            {
                var d = text[p];
                if (d == '\n') break;
                if (d == '\\')
                {
                    if (p + 1 >= text.Length) break;
                    p += text[p + 1] == '\r' && p + 2 < text.Length && text[p + 2] == '\n' ? 3 : 2;
                    continue;
                }
                p++;
            }
        }
        foreach (var h in hashes) memo[h] = p;
        return p;
    }

    // After `(`: one-level-nested args on one line up to their `)`; the position after it, or -1.
    static int ArgsEnd(string text, int p)
    {
        while (p < text.Length)
        {
            var c = text[p];
            if (c == ')') return p + 1;
            if (c == '\n') return -1;
            if (c == '(')
            {
                p++;
                while (p < text.Length && text[p] is not ('(' or ')' or '\n')) p++;
                if (p >= text.Length || text[p] != ')') return -1;
            }
            p++;
        }
        return -1;
    }

    // An #include (or #include_next) as Parse reads one (#, spaces, include, spaces, then "name", <name> or a macro), with
    // line continuations and comments allowed in the gaps: a name too many costs a walk, a name missed would skip one that
    // finds something. A macro (a computed include) is given as ComputedInclude: it may name any header.
    static readonly Regex AnyInclude = new(
        @"#(?:[ \t]|\\\r?\n|/\*.*?\*/)*include(?:_next)?\b(?:[ \t]|\\\r?\n|/\*.*?\*/)*(?:""([^""\r\n]*)""|<([^>\r\n]*)>|([A-Za-z_]))", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The name <see cref="IncludeNames"/> gives a computed include (#include MACRO): any header.</summary>
    public const string ComputedInclude = "*";

    /// <summary>Every name an #include in <paramref name="text"/> may name ("x.h" or &lt;x.h&gt;, #include_next too, and
    /// <see cref="ComputedInclude"/> for #include MACRO): all those <see cref="Find"/> follows.</summary>
    public static string[] IncludeNames(string text)
    {
        if (!text.Contains("include", StringComparison.Ordinal)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (Match m in AnyInclude.Matches(text))
            names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : ComputedInclude);
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
                                                     Func<int, IDisposable?>? parsing = null, List<string>? lookedUp = null)
    {
        var found = new List<(string, int)>();
        if (!unit.Contains('#') && (initial is null || initial.Count == 0)) return found;
        var macros = new Dictionary<string, List<Macro>>(StringComparer.Ordinal);
        if (initial is not null) foreach (var kv in initial) if (kv.Value.Count > 0) macros[kv.Key] = kv.Value.Select(v => new Macro(v.Params, v.Body)).ToList();
        // A header the caller does not hold comes back as a new string each read, and the walk knows a header (walked, parsed)
        // by its string: each include of it again walked it and all it includes again (C23: one walk resolving 1,592
        // includes). readInclude sees only the name, so one read per name is the same header.
        var read = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? ReadOnce(string raw) => read.TryGetValue(raw, out var t) ? t : read[raw] = readInclude(raw);
        var w = new WalkState(macros, ReadOnce, found) { Parsing = parsing };
        Walk(Parse(unit, false), w, unitLine: null, depth: 0, ifBase: 0);
        lookedUp?.AddRange(w.LookedUp);
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
        var inComment = false;
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
                    inComment = InCommentAfter(text.AsSpan(start, end - start), inComment);
                    Put(text.AsSpan(start, end - 1 - start), dst);
                    Put(" ", dst);
                    start = next;
                    (end, next) = Line(text, start);
                }
                inComment = InCommentAfter(text.AsSpan(start, end - start), inComment);
                Put(text.AsSpan(start, end - start), dst);
                Put("\n", dst);
            }
            // A line of code that opens or closes a block comment leaves a mark, so a directive inside one is still
            // read as inside one ("/* notes:\n#include ..." is no include).
            else if (InCommentAfter(text.AsSpan(start, end - start), inComment) is var after && after != inComment)
            {
                Put(after ? "/*\n" : "*/\n", dst);
                inComment = after;
            }
            start = next;
        }
        return n;
    }

    // Is text exactly its directive lines? (Same length was checked: every line kept whole, so only a '\r' or a
    // continuation, which DirectivesOnly drops or joins, could differ.)
    static bool Same(string text) => !text.Contains('\r') && !text.Contains("\\\n", StringComparison.Ordinal) && text.EndsWith('\n');

    /// <summary>A header's directive lines (<see cref="DirectivesOnly"/>'s text) less each #define and #undef whose name's
    /// hash (<c>string.GetHashCode</c> of the name) <paramref name="keep"/> says no to; the hashes of those dropped are
    /// added to <paramref name="dropped"/>. The walk reads a define only when it looks its name up, so a walk of the
    /// result is a walk of the whole text as long as it looks up no name of a dropped hash (<see cref="Find"/>'s
    /// lookedUp). Kept whatever their names: the first two directives and those after the last #endif (the include guard
    /// is read from them), and a line that opens or closes a block comment (the lines after it would read otherwise).
    /// The same string when nothing is dropped.</summary>
    public static string KeepDefines(string directives, Func<int, bool> keep, List<int> dropped)
    {
        var lastClose = -1;   // where the last #endif line starts
        for (var start = 0; start < directives.Length; )
        {
            var (end, next) = Line(directives, start);
            if (Head(directives.AsSpan(start, end - start)).Kind == OpKind.Close) lastClose = start;
            start = next;
        }
        StringBuilder? sb = null;
        var copied = 0;   // the text before this is in sb
        var directivesSeen = 0;
        var inComment = false;
        for (var start = 0; start < directives.Length; )
        {
            var (end, next) = Line(directives, start);
            var line = directives.AsSpan(start, end - start);
            var after = InCommentAfter(line, inComment);
            var (kind, _) = Head(line);
            if (line.TrimStart().StartsWith("#")) directivesSeen++;
            if (kind is OpKind.Define or OpKind.Undef && directivesSeen > 2 && (lastClose < 0 || start < lastClose) && after == inComment
                && FastName(line, kind, out var name) && !name.IsEmpty && string.GetHashCode(name) is var h && !keep(h))
            {
                dropped.Add(h);
                (sb ??= new StringBuilder(directives.Length / 8)).Append(directives, copied, start - copied);
                copied = Math.Min(next, directives.Length);
            }
            inComment = after;
            start = next;
        }
        if (sb is null) return directives;
        sb.Append(directives, copied, directives.Length - copied);
        return sb.ToString();
    }

    // Whether a block comment is open at the end of line, given whether one was at its start. A "//" outside one ends
    // the line; quotes are not read (a "/*" in a string reads as a comment: what is inside is followed as maybe).
    static bool InCommentAfter(ReadOnlySpan<char> line, bool inComment)
    {
        while (true)
        {
            if (inComment)
            {
                var e = line.IndexOf("*/", StringComparison.Ordinal);
                if (e < 0) return true;
                line = line[(e + 2)..];
                inComment = false;
                continue;
            }
            var o = line.IndexOf('/');
            while (o >= 0 && o + 1 < line.Length && line[o + 1] is not ('*' or '/'))
                o = line[(o + 1)..].IndexOf('/') is var r and >= 0 ? o + 1 + r : -1;
            if (o < 0 || o + 1 >= line.Length || line[o + 1] == '/') return false;
            line = line[(o + 2)..];
            inComment = true;
        }
    }

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

    static readonly string[] Undefined = { "\0" };

    // A macro's values as one comparable key each ("undefined" one of them).
    static string[] KeysOf(List<Macro>? values)
        => values is null ? Undefined : values.Select(m => (m.Params is null ? "" : "(" + string.Join(",", m.Params) + ")") + "\u0001" + m.Body).ToArray();

    /// <summary>A walk under way: the names it looked up whose value came from before it (its free names, by id, with
    /// the earliest trail stretch an answer went back to), and what makes it stand for less than a whole walk. Kept
    /// one to an include depth and used again (a walk's names are merged into the one around it name by name).</summary>
    sealed class Frame
    {
        /// <summary>The trail index of the walk's first stretch: a name settled at or after it is the walk's own.</summary>
        public int Start;
        /// <summary>Tells this walk from the earlier ones the frame was reused for.</summary>
        public long Serial;
        /// <summary>The free names, in the order first used.</summary>
        public readonly List<int> Free = new();
        int[] _from = Array.Empty<int>();
        // Every name of the walks it took on (free here or not: a name answered inside it may later go back past it).
        ulong[] _has = Array.Empty<ulong>();
        /// <summary>The length of the change log when the names it took on were last brought up to date.</summary>
        public int SyncedAt;
        /// <summary>A walk under it stopped at the include depth limit: what it looked up is not all a whole walk would.</summary>
        public bool Cut;
        /// <summary>The headers it skipped as being walked further up (an include cycle): a walk of it elsewhere walks
        /// them (death-lite: a register map walked 196 deep skipped the next one, on the stack, so its record never held
        /// TNAME, and the include under TNAME later was skipped).</summary>
        public readonly HashSet<Header> Deps = new(ReferenceEqualityComparer.Instance);

        public void Reset(int start)
        {
            foreach (var id in Free) _from[id] = int.MaxValue;
            Array.Clear(_has);
            Free.Clear();
            Deps.Clear();
            Cut = false;
            Start = start;
            SyncedAt = int.MaxValue;
        }

        public int FromOf(int id) => _from[id];

        public void Mark(int id)
        {
            if (id >> 6 >= _has.Length) Array.Resize(ref _has, Math.Max((id >> 6) + 1, _has.Length * 2));
            _has[id >> 6] |= 1UL << id;
        }

        public bool Marked(int id) => id >> 6 < _has.Length && (_has[id >> 6] & (1UL << id)) != 0;

        /// <summary>A name looked up, its answer going back to trail stretch <paramref name="from"/> (-1: to before the
        /// trail).</summary>
        public void Use(int id, int from)
        {
            if (from >= Start) return;
            if (id >= _from.Length)
            {
                var had = _from.Length;
                Array.Resize(ref _from, Math.Max(id + 1, had * 2));
                Array.Fill(_from, int.MaxValue, had, _from.Length - had);
            }
            ref var f = ref _from[id];
            if (f == int.MaxValue) Free.Add(id);
            if (from < f) f = from;
        }

        /// <summary>A walk inside this one is over: what of its came from before this one.</summary>
        public void Take(Frame inner)
        {
            Cut |= inner.Cut;
            Deps.UnionWith(inner.Deps);
            foreach (var id in inner.Free) Use(id, inner._from[id]);
            if (inner._has.Length > _has.Length) Array.Resize(ref _has, inner._has.Length);
            for (var i = 0; i < inner._has.Length; i++) _has[i] |= inner._has[i];
            SyncedAt = Math.Min(SyncedAt, inner.SyncedAt);
        }
    }

    /// <summary>A (non-template) header as one unit's walk knows it. A skip asks of each header its walks reach whether it
    /// is on the stack, guarded or matching: as tables by the header's text, those asks were a third of a walk
    /// (death-lite: 46 million a unit).</summary>
    sealed class Header(string text, Parsed parsed)
    {
        public readonly string Text = text;
        public readonly Parsed Parsed = parsed;
        /// <summary>Its walks (see <see cref="WalkState.HeaderOf"/>), null for none.</summary>
        public List<Entry>? Walked;
        /// <summary>Being walked (an include of it inside its walk is nothing, as its guard makes it).</summary>
        public bool Walking;
        /// <summary>Guarded and walked for certain: its guard is defined from then on.</summary>
        public bool Certain;
        /// <summary>The walk of it that matches (null: none) while the walk has not changed since <see cref="MatchedAt"/>.</summary>
        public Entry? Match;
        public long MatchedAt = -1;
        public int GuardId = -1;
        // Its place in the Match under way (NodeAt: which).
        public int Node;
        public long NodeAt = -1;
    }

    /// <summary>One walk of a header: the value each of its free names had where it started (the walk is the same
    /// wherever they have those values again), and the headers it skipped as on the stack.</summary>
    sealed class Entry
    {
        public readonly int[] Ids;
        public readonly string[][] Keys;
        /// <summary>Where each free name's answer went back to when it last matched (kept up with the change log): what
        /// a walk that skips the header takes on.</summary>
        public readonly int[] From;
        /// <summary>Each free name's place in <see cref="Ids"/>.</summary>
        public readonly Dictionary<int, int> Slot;
        public readonly Header[] Deps;
        /// <summary>The length of the change log when <see cref="Miss"/> was last brought up to date (-1: never).</summary>
        public int SeenAt = -1;
        /// <summary>The places of the free names whose value differs from the one the walk started with.</summary>
        public readonly HashSet<int> Miss = new();
        /// <summary>The walk (by its serial) that last took on its free names and those of the walks standing for the
        /// headers it skipped: a walk taken on by one inside it took them on too (WalkState.Covers).</summary>
        public long AppliedBy = -1;

        public Entry(int[] ids, string[][] keys, Header[] deps)
        {
            Ids = ids;
            Keys = keys;
            From = new int[ids.Length];
            Slot = new Dictionary<int, int>(ids.Length);
            for (var i = 0; i < ids.Length; i++) Slot[ids[i]] = i;
            Deps = deps;
        }
    }

    /// <summary>A walk's macros are not copied into a table of its own (every #define of every header a unit reached:
    /// hundreds of MB a unit in a tree of register maps). The walk keeps the stretches of text it has walked, in order
    /// (<see cref="Trail"/>), and a name is looked up back along them: the value list a table would hold.</summary>
    sealed record WalkState(Dictionary<string, List<Macro>> Initial, Func<string, string?> ReadInclude, List<(string, int)> Found)
    {
        public Func<int, IDisposable?>? Parsing { get; init; }
        readonly Dictionary<string, Header> _headers = new(ReferenceEqualityComparer.Instance);

        /// <summary>Each header reached, and its walks (<see cref="Entry"/>). A header included again is walked again only
        /// when no walk of it started with the values its free names have now. Walked once and never again (a header
        /// guard), the first walk took the header for good, and that walk may be no include at all (under an #if, in a
        /// comment): an 8-header chain under an #if took a guarded header from the include that really brings it in,
        /// with TNAME set, and the template instance with it (death-lite: 60% of them missed). Kept as the values each
        /// name was seen with instead, a check of a header went over every name of every header it skipped in a cycle,
        /// and theirs (death-lite: 80 million names in half a minute of one unit).</summary>

        public Header HeaderOf(string text, Parsed parsed)
        {
            if (!_headers.TryGetValue(text, out var h)) _headers[text] = h = new Header(text, parsed);
            return h;
        }
        /// <summary>The template headers being walked. One included again inside its own walk is walked again (each
        /// include instantiates it) only for certain, where the depth limit stops it as gcc's does: a maybe-include adds
        /// no depth, and a template reaching itself that way recursed without end (a parse of a public repo hung).</summary>
        public readonly HashSet<string> WalkingTemplates = new(ReferenceEqualityComparer.Instance);

        /// <summary>Is the name defined on every way here (an #undef or a define only under an #if leaves it open)?</summary>
        public bool Defined(string name) => Now(Id(name)) is { Values: not null, Open: false };
        /// <summary>Every name the walk looked up (a head's, an expansion's, a computed include's, a guard's): a name
        /// is asked only through <see cref="Id"/>.</summary>
        public IReadOnlyList<string> LookedUp => _names;
        /// <summary>The walks under way, innermost last.</summary>
        public readonly List<Frame> Looking = new();
        readonly List<Frame> _frames = new();
        public readonly List<Stretch> Trail = new();
        // For each name looked up (by its hash), the closed stretches of the trail that define or undefine a name of
        // that hash, in trail order. Built at a name's first lookup and kept up as stretches close: only names looked
        // up cost anything (a register map's tens of thousands of #defines are never asked for).
        readonly Dictionary<int, List<int>> _chains = new();
        // A bit per chain hash (of 64K): a closed stretch asks for each name it defines, and most are never looked up.
        readonly ulong[] _chainBits = new ulong[1024];
        bool MayChain(int h) => (_chainBits[((uint)h & 0xFFFF) >> 6] & (1UL << h)) != 0;
        /// <summary>The names looked up whose chain grew (by id), in order: since an entry last matched, only these can
        /// differ.</summary>
        readonly List<int> _changes = new();
        // The names looked up, by id (a frame or entry holds ids: no string hashed to merge one walk's names into the
        // next), with their hash.
        readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        readonly List<string> _names = new();
        readonly List<int> _hashes = new();
        // Each name's values (their keys, and the stretch its answer went back to) as last resolved, with the length of
        // its chain then (-1: never). With the whole trail closed a name changes only as its chain grows; an open last
        // stretch that has no name of its hash answers nothing either.
        readonly List<(int Count, List<Macro>? Values, string[] Keys, int From, bool Open)> _now = new();

        int Id(string name)
        {
            if (_ids.TryGetValue(name, out var id)) return id;
            _ids[name] = id = _names.Count;
            _names.Add(name);
            _hashes.Add(string.GetHashCode(name.AsSpan()));
            _now.Add((-1, null, Undefined, -1, false));
            return id;
        }

        /// <summary>A walk of a header starts at the end of the trail.</summary>
        public Frame Push()
        {
            if (_frames.Count == Looking.Count) _frames.Add(new Frame());
            var f = _frames[Looking.Count];
            f.Reset(Trail.Count);
            f.Serial = _takenInto.Count;
            _takenInto.Add(-1);
            Looking.Add(f);
            _version++;
            return f;
        }

        public void Pop() { Looking.RemoveAt(Looking.Count - 1); _version++; }

        static bool Has(Stretch st, int h)
        {
            var ix = st.Parsed.Index;
            if (!ix.Last.TryGetValue(h, out var i)) return false;
            for (; i >= st.From; i = ix.Prev[i])
                if (i < st.To) return true;
            return false;
        }

        // The closed stretches of the trail, by the text they are of.
        readonly Dictionary<Parsed, List<int>> _byParsed = new(ReferenceEqualityComparer.Instance);

        List<int> Chain(int h)
        {
            if (_chains.TryGetValue(h, out var chain)) return chain;
            chain = new List<int>();
            _chainBits[((uint)h & 0xFFFF) >> 6] |= 1UL << h;
            // Only the texts that define a name of the hash (each stretch of the trail asked: death-lite, a fifth of a walk).
            foreach (var (parsed, at) in _byParsed)
                if (parsed.Index.MayDefine(h) && parsed.Index.Last.ContainsKey(h))
                    foreach (var j in at) if (Has(Trail[j], h)) chain.Add(j);
            chain.Sort();
            return _chains[h] = chain;
        }

        /// <summary>A walk is over: a define it made outside its own #ifs is from now on only an alternative where an #if
        /// or a maybe was around its include, and the names looked up it defined so may differ.</summary>
        public void End(Scope scope, List<Stretch> stretches)
        {
            scope.Active = false;
            if (stretches[0].IfBase == 0 || _chains.Count == 0) return;
            foreach (var st in stretches)
            {
                var p = st.Parsed;
                var ix = p.Index;
                for (var d = ix.DefAt(st.From); d < ix.Defs.Length && ix.Defs[d] < st.To; d++)
                {
                    var i = ix.Defs[d];
                    if (ix.Depth[i] > 0 || !MayChain(ix.Hash[i]) || !_chains.ContainsKey(ix.Hash[i]) || !_ids.TryGetValue(p.NameOf(i).ToString(), out var id)) continue;
                    _changes.Add(id);
                    _now[id] = _now[id] with { Count = -1 };
                }
            }
        }

        /// <summary>A stretch of the trail ends (its To is final): the chains of names it defines take it.</summary>
        public void Close(Stretch st)
        {
            if (st.Closed) return;
            st.Closed = true;
            var p = st.Parsed;
            var ix = p.Index;
            if (ix.Defs.Length == 0 || ix.DefAt(st.From) is var first && (first == ix.Defs.Length || ix.Defs[first] >= st.To)) return;
            var index = Trail.Count - 1;
            while (index >= 0 && !ReferenceEquals(Trail[index], st)) index--;
            if (_byParsed.TryGetValue(p, out var at)) at.Add(index); else _byParsed[p] = new List<int> { index };
            if (_chains.Count == 0) return;
            for (var d = first; d < ix.Defs.Length && ix.Defs[d] < st.To; d++)
            {
                var i = ix.Defs[d];
                if (!MayChain(ix.Hash[i]) || !_chains.TryGetValue(ix.Hash[i], out var chain)) continue;
                if (chain.Count == 0 || chain[^1] != index) chain.Add(index);
                if (_ids.TryGetValue(p.NameOf(i).ToString(), out var id)) _changes.Add(id);
            }
        }

        // Answers since the trail last grew (Walk clears it).
        public readonly Dictionary<string, (List<Macro>? Values, int From, int Id)> Asked = new(StringComparer.Ordinal);

        public List<Macro>? Lookup(string name)
        {
            if (!Asked.TryGetValue(name, out var a)) Asked[name] = a = (Resolve(name, Trail.Count, out _, out var from), from, Id(name));
            if (Looking.Count > 0) Looking[^1].Use(a.Id, a.From);
            return a.Values;
        }

        /// <summary>Lookup outside a head: its answers hold only until the trail grows (Asked is cleared at each head).</summary>
        public List<Macro>? LookupNow(string name)
        {
            var id = Id(name);
            var (values, _, from, _) = Now(id);
            if (Looking.Count > 0) Looking[^1].Use(id, from);
            return values;
        }

        (List<Macro>? Values, string[] Keys, int From, bool Open) Now(int id)
        {
            var h = _hashes[id];
            if (Trail.Count > 0 && !Trail[^1].Closed && Has(Trail[^1], h))
            {
                var v = Resolve(_names[id], Trail.Count, out var o, out var f);
                return (v, KeysOf(v), f, o);
            }
            var n = Chain(h).Count;
            var c = _now[id];
            if (c.Count == n) return (c.Values, c.Keys, c.From, c.Open);
            var values = Resolve(_names[id], Trail.Count, out var open, out var from);
            var keys = KeysOf(values);
            _now[id] = (n, values, keys, from, open);
            return (values, keys, from, open);
        }

        /// <summary>Is an include of the (non-template) header here nothing (on the stack, or its guard defined), or a
        /// walk of it already walked?</summary>
        public bool Skips(Header h)
        {
            if (h.Walking)
            {
                if (Looking.Count > 0) Looking[^1].Deps.Add(h);
                return true;
            }
            if (GuardHolds(h)) return true;
            if (h.Walked is null) return false;
            if (_matchAt != (_changes.Count, _version)) { _epoch++; _matchAt = (_changes.Count, _version); }
            if (!Match(h)) return false;
            // The walk under way is the same again only where this header is skipped again: its free names (and those of
            // the walks that stand for the headers it skipped in a cycle) are those of the walk under way too.
            if (Looking.Count > 0) { Sync(Looking[^1]); Apply(h.Match!, Looking[^1]); }
            return true;
        }

        // Each header matched since the walk last changed (a walk pushed or popped, a name looked up changed): the walk of
        // it that matches here, or null for none. Until the walk changes every include asks the same, so a header's answer
        // is worked out once: each include went over the chain of headers skipped below it again (death-lite: a web unit of
        // 1.3 KB took 14 s, 1.0.288).
        long _epoch, _matchCalls;   // the matches (Header.Match) hold while the epoch is the same
        (int Changes, long Version) _matchAt = (-1, -1);
        long _version;   // Push and Pop: the walk under way, and the headers walking or walked for certain, may differ

        // Match's working lists, kept from call to call (each call made a dozen, and a walk makes hundreds of thousands).
        readonly List<Header> _mNames = new();
        readonly List<Entry> _mCands = new();          // each header's walks that may match, a header's together
        readonly List<int> _mCandAt = new();           // where a header's start in _mCands (added while it is the one popped)
        readonly List<int> _mCandCount = new();
        readonly List<List<(int Node, int Cand)>> _mUsers = new();   // the walks that need a header to match
        readonly Stack<int> _mTodo = new();
        readonly List<Header> _mNeed = new();
        readonly Queue<int> _mQueue = new();
        int[] _mLeft = Array.Empty<int>();
        bool[] _mGone = Array.Empty<bool>(), _mDead = Array.Empty<bool>();

        // Does a walk of the header match here: its free names as they started, and each header it skipped as on the
        // stack nothing here too (on the stack, its guard defined, or a walk of it matching)? Worked out at once for all
        // the headers the question reaches, as the most of them that match together: each has a walk whose free names
        // match and whose skipped headers are on the stack, guarded, or matching too. In a cycle each is then as when it
        // was walked: walked here, one is on the stack where the other's walk skipped it. (A header reached again while
        // being matched taken as not matching, no header in a cycle matched, and death-lite walked it all again and
        // again; taken as matching one at a time, a no further down was undone with every walk tried above it.)
        bool Match(Header inc)
        {
            if (inc.MatchedAt == _epoch) return inc.Match is not null;
            var at = ++_matchCalls;
            var names = _mNames;
            names.Clear(); _mCands.Clear(); _mCandAt.Clear(); _mCandCount.Clear(); _mTodo.Clear();
            int Node(Header h)
            {
                if (h.NodeAt == at) return h.Node;
                var n = h.Node = names.Count;
                h.NodeAt = at;
                names.Add(h);
                _mCandAt.Add(0);
                _mCandCount.Add(0);
                if (_mUsers.Count == n) _mUsers.Add(new List<(int, int)>()); else _mUsers[n].Clear();
                _mTodo.Push(n);
                return n;
            }
            Node(inc);
            var need = _mNeed;
            while (_mTodo.Count > 0)
            {
                var n = _mTodo.Pop();
                _mCandAt[n] = _mCands.Count;
                if (names[n].Walked is not { } entries) continue;
                foreach (var e in entries)
                {
                    if (!FreeMatch(e)) continue;
                    need.Clear();
                    var ok = true;
                    foreach (var dep in e.Deps)
                    {
                        if (dep.Walking || GuardHolds(dep)) continue;
                        if (dep.MatchedAt == _epoch) { if (dep.Match is null) { ok = false; break; } continue; }
                        need.Add(dep);
                    }
                    if (!ok) continue;
                    var c = _mCands.Count;
                    _mCands.Add(e);
                    _mCandCount[n]++;
                    foreach (var dep in need) _mUsers[Node(dep)].Add((n, c));
                    if (need.Count == 0) break;   // matches whatever the others do
                }
            }
            // Take out a header none of whose walks is left, and with it the walks that need it, until none goes.
            var count = names.Count;
            if (_mLeft.Length < count) { _mLeft = new int[Math.Max(count, _mLeft.Length * 2)]; _mDead = new bool[_mLeft.Length]; }
            if (_mGone.Length < _mCands.Count) _mGone = new bool[Math.Max(_mCands.Count, _mGone.Length * 2)];
            var left = _mLeft; var dead = _mDead; var gone = _mGone;
            Array.Clear(gone, 0, _mCands.Count);
            var queue = _mQueue;
            queue.Clear();
            for (var n = 0; n < count; n++)
            {
                left[n] = _mCandCount[n];
                dead[n] = left[n] == 0;
                if (dead[n]) queue.Enqueue(n);
            }
            while (queue.Count > 0)
                foreach (var (u, c) in _mUsers[queue.Dequeue()])
                {
                    if (gone[c]) continue;
                    gone[c] = true;
                    if (--left[u] == 0 && !dead[u]) { dead[u] = true; queue.Enqueue(u); }
                }
            for (var n = 0; n < count; n++)
            {
                Entry? m = null;
                if (!dead[n])
                    for (var c = _mCandAt[n]; ; c++) if (!gone[c]) { m = _mCands[c]; break; }
                (names[n].Match, names[n].MatchedAt) = (m, _epoch);
            }
            var matched = !dead[0];
            names.Clear(); _mCands.Clear(); need.Clear();   // hold no headers or walks past the call
            return matched;
        }

        // The walk under way takes on a matching walk's free names, and those of the walks matching for the headers it skipped.
        void Apply(Entry e, Frame f)
        {
            // Taken on by this walk, or by one inside it (whose names and skipped headers this walk took when it was over):
            // nothing to add, and the walks below it neither. Each skip went over the whole web of walks below it again
            // (death-lite: 42 million steps a unit, half of a walk), once for each walk under way that skipped into it.
            if (Covers(f, e.AppliedBy)) return;
            e.AppliedBy = f.Serial;
            if (f.SyncedAt > _changes.Count) f.SyncedAt = _changes.Count;
            for (var i = 0; i < e.Ids.Length; i++) { f.Use(e.Ids[i], e.From[i]); f.Mark(e.Ids[i]); }
            foreach (var dep in e.Deps)
                // A header matched since the walk last changed was neither on the stack nor guarded then (Match), and is not now.
                if (dep.MatchedAt == _epoch) { if (dep.Match is { } c) Apply(c, f); }
                else if (dep.Walking) f.Deps.Add(dep);
        }

        // Each walk's serial: the one it was taken into when it was over (-1: under way, or the outermost).
        readonly List<long> _takenInto = new();

        bool Covers(Frame f, long by)
        {
            while (by >= 0 && by != f.Serial) by = _takenInto[(int)by];
            return by == f.Serial;
        }

        /// <summary>A walk inside the one under way is over: the one under way takes what of its came from before it.</summary>
        public void TakeInto(Frame inner)
        {
            var f = Looking[^1];
            f.Take(inner);
            _takenInto[(int)inner.Serial] = f.Serial;
        }

        // The names the walk under way took on whose value changed since: an answer may now go back further (an #if's
        // define after the walk began made an alternative of it).
        void Sync(Frame f)
        {
            for (var i = f.SyncedAt; i < _changes.Count; i++)
                if (_changes[i] is var id && f.Marked(id)) f.Use(id, Now(id).From);
            f.SyncedAt = _changes.Count;
        }

        bool GuardHolds(Header h)
        {
            if (!h.Certain || h.Parsed.GuardDefine < 0) return false;
            if (h.GuardId < 0) h.GuardId = Id(h.Parsed.GuardName);
            return Now(h.GuardId) is { Values: not null, Open: false };
        }

        bool Same(Entry e, int slot)
        {
            var now = Now(e.Ids[slot]);
            if (!now.Keys.AsSpan().SequenceEqual(e.Keys[slot])) return false;
            e.From[slot] = now.From;
            return true;
        }
        // The names that differ are kept up with the change log: only names whose chain grew are looked at again (each
        // check after a miss went over every free name: death-lite, a walk under TNAME undefined matched again after each
        // #undef TNAME, 666 names a check).
        bool FreeMatch(Entry e)
        {
            if (e.SeenAt < 0)
                for (var slot = 0; slot < e.Ids.Length; slot++) { if (!Same(e, slot)) e.Miss.Add(slot); }
            else
                for (var i = e.SeenAt; i < _changes.Count; i++)
                    if (e.Slot.TryGetValue(_changes[i], out var slot))
                    {
                        if (Same(e, slot)) e.Miss.Remove(slot);
                        else e.Miss.Add(slot);
                    }
            e.SeenAt = _changes.Count;
            return e.Miss.Count == 0;
        }

        /// <summary>A walk of the header is over: an entry of what its free names were where it started. A cut walk
        /// stands for nothing (the next include walks it again).</summary>
        public void Record(Header h, Frame f)
        {
            if (f.Cut) return;
            var ids = f.Free.ToArray();
            var keys = new string[ids.Length][];
            // A name nothing defined since the walk started has the value it had then.
            for (var i = 0; i < ids.Length; i++)
                keys[i] = Chain(_hashes[ids[i]]) is var c && (c.Count == 0 || c[^1] < f.Start) ? Now(ids[i]).Keys : KeysOf(Resolve(_names[ids[i]], f.Start, out _, out _));
            (h.Walked ??= new List<Entry>()).Add(new Entry(ids, keys, f.Deps.ToArray()));
        }

        // A define at #if depth 0 replaces the value, one inside an #if is an alternative to it (not twice the same),
        // an #undef at depth 0 removes it: going back to the first that settles it, then forward as a table would.
        // Over the first count stretches of the trail (those before them are not changed by what came after). open:
        // undefined is one of the ways (a define only under an #if over none, or an #undef under one). from: the trail
        // stretch that settled it, -1 for none (the answer went back past the trail).
        List<Macro>? Resolve(string name, int count, out bool open, out int from)
        {
            var back = new List<(Macro Macro, bool InIf)>();
            var settled = false;
            var undefInIf = false;
            var h = string.GetHashCode(name.AsSpan());
            // A stretch's defines of the name, last first; true once one settles it.
            bool Take(Stretch st)
            {
                var p = st.Parsed;
                var ix = p.Index;
                if (!ix.Last.TryGetValue(h, out var i)) return false;
                for (; i >= st.From; i = ix.Prev[i])
                {
                    // The index goes by the name's hash: an op of another name with the same hash is passed over.
                    if (i >= st.To || !p.NameOf(i).SequenceEqual(name)) continue;
                    var op = p.Ops[i];
                    var inIf = (st.Scope.Active ? 0 : st.IfBase) + ix.Depth[i] > 0;
                    if (op.Kind == OpKind.Undef) { if (inIf) { undefInIf = true; continue; } return true; }
                    back.Add((p.Directive(i).Macro!, inIf));
                    if (!inIf) return true;
                }
                return false;
            }
            // Only the stretches that define or undefine a name of its hash (going back over every stretch, for a name
            // never settled, was most of a walk: death-lite, minutes a unit), and the one still growing.
            from = -1;
            if (count == Trail.Count && count > 0 && !Trail[count - 1].Closed && Take(Trail[count - 1])) (settled, from) = (true, count - 1);
            if (!settled)
            {
                var chain = Chain(h);
                for (var j = chain.Count - 1; j >= 0 && !settled; j--)
                    if (chain[j] < count && Take(Trail[chain[j]])) (settled, from) = (true, chain[j]);
            }
            List<Macro>? cur = !settled && Initial.TryGetValue(name, out var init) ? new List<Macro>(init) : null;
            open = cur is null;
            for (var k = back.Count - 1; k >= 0; k--)
            {
                var (m, inIf) = back[k];
                if (inIf && cur is not null)
                {
                    if (!cur.Any(a => a.Body == m.Body && (a.Params is null) == (m.Params is null))) cur.Add(m);
                }
                else { cur = new List<Macro> { m }; if (!inIf) open = false; }
            }
            open |= undefInIf;
            return cur;
        }
    }

    /// <summary>One walk of a text. While it is under way it is on the way to whatever is walked: a define it made outside
    /// its own #ifs holds there, an #if or a maybe around its include aside (death-lite: a register map maybe-included
    /// defined the name it then included by, "#define X \"next.h\"" and "#include X", and as an alternative the name went
    /// back past every walk, so every walk around it took it as free: a thousand free names a walk, none walked again
    /// matched).</summary>
    sealed class Scope { public bool Active = true; }

    /// <summary>Ops <c>From..To</c> (exclusive) of a parsed text, walked at #if depth <c>IfBase</c>.</summary>
    sealed class Stretch(Parsed parsed, int ifBase, int from, Scope scope)
    {
        public readonly Parsed Parsed = parsed;
        public readonly int IfBase = ifBase, From = from;
        /// <summary>The walk of the text it is a stretch of.</summary>
        public readonly Scope Scope = scope;
        public int To = from;
        /// <summary>Its To is final, and it is in the trail's index of names.</summary>
        public bool Closed;
    }

    /// <summary>A parsed text's defines and undefs by the hash of their name (no string per name: a register map
    /// has tens of thousands): the last op of each hash, each op's previous one of its hash (-1 for none), and the #if
    /// depth each op is at (within the text).</summary>
    sealed class DefineIndex
    {
        public readonly Dictionary<int, int> Last = new();
        public readonly int[] Prev, Depth, Hash;
        /// <summary>The ops that define or undefine a name, in order (a walk's stretches go over these, not every line).</summary>
        public readonly int[] Defs;
        readonly ulong[] Bits;

        public DefineIndex(Parsed p)
        {
            var ops = p.Ops;
            Prev = new int[ops.Length];
            Depth = new int[ops.Length];
            Hash = new int[ops.Length];
            var defs = new List<int>();
            var depth = 0;
            var c = 0;   // the next commented op
            for (var i = 0; i < ops.Length; i++)
            {
                var op = ops[i];
                Depth[i] = depth;
                Prev[i] = -1;
                // A directive in a comment: an #if or #endif counts for nothing, a #define or #undef as one under an #if
                // (an alternative, never what settles a name).
                if (c < p.Commented.Length && p.Commented[c] == i)
                {
                    c++;
                    if (op.Kind is OpKind.Open or OpKind.Close) continue;
                    Depth[i] = depth + 1;
                }
                // The include guard's #ifndef: what is inside it is unconditional, as the walk takes it (a header walked
                // again with its guard defined is not walked: Header.Certain).
                if (i == p.GuardOpen || i == p.GuardClose) continue;
                if (op.Kind == OpKind.Open) depth++;
                else if (op.Kind == OpKind.Close) depth = Math.Max(0, depth - 1);
                else if (op.Kind is OpKind.Define or OpKind.Undef && p.HasName(i))
                {
                    var h = Hash[i] = string.GetHashCode(p.NameOf(i));
                    defs.Add(i);
                    Prev[i] = Last.TryGetValue(h, out var q) ? q : -1;
                    Last[h] = i;
                }
            }
            Defs = defs.ToArray();
            // A bit per hash, 8 bits a define (at most 64K): a walk asks every text it closed for each name it looks up.
            var bits = 64;
            while (bits < defs.Count * 8 && bits < 1 << 16) bits <<= 1;
            Bits = new ulong[bits >> 6];
            foreach (var h in Last.Keys) Bits[((uint)h & (uint)(bits - 1)) >> 6] |= 1UL << h;
        }

        /// <summary>The place in <see cref="Defs"/> of the first at or after op <paramref name="from"/>.</summary>
        public int DefAt(int from) { var i = Array.BinarySearch(Defs, from); return i < 0 ? ~i : i; }

        /// <summary>False when no name of hash <paramref name="h"/> is defined here (true may still be none).</summary>
        public bool MayDefine(int h) => (Bits[((uint)h & (uint)(Bits.Length * 64 - 1)) >> 6] & (1UL << h)) != 0;
    }

    enum OpKind { Code, Other, Open, Close, Define, Undef, Include }

    /// <summary>One line of a text, or one directive with its continuations (lines <c>First..Last</c>, 0-based, from
    /// character <c>Start</c> to <c>End</c>), as <see cref="Walk"/> reads it. Only where it is: the name, include and
    /// macro are read again from the text when asked (<see cref="Parsed.Directive"/>), a copy of each was 5 times the
    /// header, kept for as long as the header is.</summary>
    readonly record struct Op(OpKind Kind, int First, int Last, int Start, int End);

    /// <summary>What a directive says: its name (or include), and for a #define with one, the macro. Computed: an
    /// #include of a macro (Text the macro's name), not of a "name" or <name>.</summary>
    readonly record struct Directive(OpKind Kind, string? Text, Macro? Macro, bool Computed = false);

    /// <summary>A text split into <see cref="Op"/>s, and whether it holds a macro-named head (a template header).</summary>
    sealed record Parsed(string Text, Op[] Ops, bool IsTemplate)
    {
        /// <summary>The include guard's #ifndef and its #endif (ops), -1 when the text has none: the walk takes what is
        /// inside it as unconditional.</summary>
        public int GuardOpen { get; init; } = -1;
        public int GuardClose { get; init; } = -1;
        /// <summary>The guard's #define (op), -1 when there is none.</summary>
        public int GuardDefine { get; init; } = -1;
        string? _guardName;
        /// <summary>The guard's name (asked at each skip of a header that reaches this one).</summary>
        public string GuardName => _guardName ??= NameOf(GuardDefine).ToString();
        /// <summary>The directives (ops, in order) that start inside a block comment: no directive to a compiler, but a
        /// "/*" in a string can look like one, so the walk follows such an #include as it would one under an #if.</summary>
        public int[] Commented { get; init; } = Array.Empty<int>();
        DefineIndex? _index;
        // Read first: the lambda, made at each call, was half of what a cached walk allocated (death-lite: 1.5 GB a unit,
        // Has and Chain asking it for every stretch).
        public DefineIndex Index => Volatile.Read(ref _index) ?? LazyInitializer.EnsureInitialized(ref _index, () => new DefineIndex(this));

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
        List<int>? commented = null;
        var inComment = false;
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
                if (k < end) inComment = InCommentAfter(text.AsSpan(k, end - k), inComment);
                start = next;
                continue;
            }
            if (inComment) (commented ??= new List<int>()).Add(ops.Count);
            var opStart = start;
            if (!(end > start && text[end - 1] == '\\' && next <= text.Length))
            {
                ops.Add(new Op(Head(text.AsSpan(start, end - start)).Kind, first, i, opStart, end));
                inComment = InCommentAfter(text.AsSpan(start, end - start), inComment);
            }
            else
            {
                // As JoinContinued: each line ending in a backslash takes in the next.
                var lastEnd = end;
                inComment = InCommentAfter(text.AsSpan(start, end - start), inComment);
                while (lastEnd > start && text[lastEnd - 1] == '\\' && next <= text.Length)
                {
                    (lastEnd, next) = Line(text, start = next);
                    inComment = InCommentAfter(text.AsSpan(start, lastEnd - start), inComment);
                    i++;
                }
                ops.Add(new Op(Head(JoinContinued(text, opStart, i - first)).Kind, first, i, opStart, lastEnd));
            }
            start = next;
        }
        var all = ops.ToArray();
        var (open, close, guardDefine) = Guard(text, all, commented);
        return new Parsed(text, all, isTemplate) { GuardOpen = open, GuardClose = close, GuardDefine = guardDefine, Commented = commented?.ToArray() ?? Array.Empty<int>() };
    }

    // The include guard: the first directive an #ifndef X (or #if !defined X), the next a #define of X, and the #endif that
    // closes it the last directive. (-1, -1, -1) when there is none.
    static (int Open, int Close, int Define) Guard(string text, Op[] ops, List<int>? commented)
    {
        int Directive(int from) { while (from < ops.Length && ops[from].Kind == OpKind.Code) from++; return from; }
        var g = Directive(0);
        if (g >= ops.Length || ops[g].Kind != OpKind.Open || commented?.Contains(g) == true) return (-1, -1, -1);
        var name = GuardName(text.AsSpan(ops[g].Start, ops[g].End - ops[g].Start));
        if (name.IsEmpty) return (-1, -1, -1);
        var d = Directive(g + 1);
        if (d >= ops.Length || ops[d].Kind != OpKind.Define) return (-1, -1, -1);
        var (_, after) = Head(text.AsSpan(ops[d].Start, ops[d].End - ops[d].Start));
        var rest = text.AsSpan(ops[d].Start + after, ops[d].End - ops[d].Start - after);
        if (!rest.StartsWith(name) || rest.Length > name.Length && IsWord(rest[name.Length])) return (-1, -1, -1);
        var depth = 0;
        for (var k = g; k < ops.Length; k++)
        {
            if (ops[k].Kind == OpKind.Open) depth++;
            else if (ops[k].Kind == OpKind.Close && --depth == 0)
                return Directive(k + 1) >= ops.Length ? (g, k, d) : (-1, -1, -1);
        }
        return (-1, -1, -1);
    }

    // X of "#ifndef X" or "#if !defined X" / "#if !defined(X)"; empty for any other #if.
    static ReadOnlySpan<char> GuardName(ReadOnlySpan<char> raw)
    {
        var s = raw.TrimStart()[1..].TrimStart();
        if (s.StartsWith("ifndef")) s = s[6..].TrimStart();
        else if (s.StartsWith("if") && s[2..].TrimStart() is var t && t.StartsWith("!") && t[1..].TrimStart() is var u && u.StartsWith("defined"))
        {
            s = u[7..].TrimStart();
            if (s.StartsWith("(")) s = s[1..].TrimStart();
        }
        else return default;
        var n = 0;
        while (n < s.Length && IsWord(s[n])) n++;
        return n > 0 && !char.IsAsciiDigit(s[0]) ? s[..n] : default;
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
        // #include_next: the walk can't tell the next header of a name from the first, and follows it as an #include.
        var kind = word switch { "define" => OpKind.Define, "undef" => OpKind.Undef, "include" or "include_next" => OpKind.Include, _ => OpKind.Other };
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
            default:   // include: "name", <name>, or a macro naming one
                if (rest.Length >= 2 && rest[0] is '"' or '<' && rest[1..].IndexOf(rest[0] == '"' ? '"' : '>') is var q and >= 0)
                    return new Directive(kind, rest.Slice(1, q).ToString(), null);
                if (rest.Length > 0 && (char.IsAsciiLetter(rest[0]) || rest[0] == '_'))
                {
                    var w = 1;
                    while (w < rest.Length && IsWord(rest[w])) w++;
                    return new Directive(kind, rest[..w].ToString(), null, Computed: true);
                }
                return new Directive(kind, null, null);
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

    // gcc's include depth limit, for includes walked for certain. Includes in comments and under #ifs (no include to
    // gcc, or not all of them) are not counted: chains of them ran 200 deep in death-lite, where gcc nests 46. The walk
    // goes as deep as the headers not on its stack take it, and the thread's stack.
    const int MaxIncludeDepth = 200;

    /// <summary>The headers a computed include (#include MACRO) may name: each value of the macro that is a "name" or
    /// &lt;name&gt;, through macros naming macros. A value it can't read (a pasted name) names nothing it can follow.</summary>
    static List<string> IncludedBy(string macro, WalkState w)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>();
        todo.Push(macro);
        while (todo.TryPop(out var m) && seen.Count < 64)
        {
            if (!seen.Add(m) || w.LookupNow(m) is not { } values) continue;
            foreach (var v in values)
            {
                if (v.Params is not null) continue;
                var b = v.Body.Trim();
                if (b.Length >= 2 && (b[0] == '"' && b[^1] == '"' || b[0] == '<' && b[^1] == '>')) names.Add(b[1..^1]);
                else if (b.Length > 0 && (char.IsAsciiLetter(b[0]) || b[0] == '_') && b.All(IsWord)) todo.Push(b);
            }
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Walks a text: its heads, and each header it includes in turn. <paramref name="maybe"/>: the text may not be
    /// included at all (see <see cref="WalkState.HeaderOf"/>).</summary>
    static void Walk(Parsed parsed, WalkState w, int? unitLine, int depth, int ifBase, bool maybe = false)
    {
        var scope = new Scope();
        var stretch = new Stretch(parsed, ifBase, 0, scope);
        var stretches = new List<Stretch> { stretch };
        w.Trail.Add(stretch);
        var code = new StringBuilder();   // the non-directive text since the last define/undef/include, line for line
        var codeStartLine = 1;
        var ifDepth = ifBase;   // an #if around the #include that brought this text in counts too
        var localIf = 0;        // the #ifs open in this text, its include guard aside
        var nextCommented = 0;
        void Flush()
        {
            if (code.Length == 0) return;
            var s = code.ToString();
            code.Clear();
            if (!ClosesIntoBody(s)) return;
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
            var inComment = nextCommented < parsed.Commented.Length && parsed.Commented[nextCommented] == k;
            if (inComment) nextCommented++;
            switch (op.Kind)
            {
                case OpKind.Code:
                    if (code.Length == 0) codeStartLine = op.First + 1;
                    code.Append(parsed.Text, op.Start, op.End - op.Start).Append('\n');
                    continue;
                case OpKind.Other or OpKind.Open or OpKind.Close:
                    // An #if or #endif in a comment is none (as DefineIndex counts them).
                    if (inComment) { }
                    else if (op.Kind == OpKind.Open) { ifDepth++; if (k != parsed.GuardOpen) localIf++; }
                    else if (op.Kind == OpKind.Close) { ifDepth = Math.Max(ifBase, ifDepth - 1); if (k != parsed.GuardClose) localIf = Math.Max(0, localIf - 1); }
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
                    // An include depth limit of 8 missed every template more than 8 includes down (death-lite: 10,108 of
                    // 21,659 instances, down to 46).
                    if (parsed.Directive(k) is not { Text: { } incName } d) break;
                    if (depth >= MaxIncludeDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                    {
                        // Cut: the walks under way never stand for a whole one (a header first walked at the limit looked
                        // up no TNAME, and its include with TNAME set was skipped).
                        if (w.Looking.Count > 0) w.Looking[^1].Cut = true;
                        break;
                    }
                    var names = d.Computed ? IncludedBy(incName, w) : new List<string> { incName };
                    var uncertain = maybe || localIf > 0 || inComment || names.Count > 1;
                    foreach (var name in names)
                    {
                        var inc = w.ReadInclude(name);
                        if (inc is null) continue;
                        // A template header is instantiated by every #include; any other header only adds macros, once.
                        var p = ParseOf(inc, w.Parsing);
                        stretch.To = k + 1;
                        // The whole trail closed at an include: what a name is then is its chain (WalkState.Now).
                        if (!stretch.Closed)
                        {
                            w.Close(stretch);
                            w.Trail.Add(stretch = new Stretch(parsed, ifBase, k + 1, scope)); stretches.Add(stretch);
                        }
                        // A header included inside its own walk: as its guard would, nothing. A guarded header once walked
                        // for certain (no #if, comment or choice of name on the way) is nothing again while its guard is
                        // defined: walked again where a name it looks up has a value now, it put out an instance gcc never
                        // sees (death-lite: 103 in one unit, a register map first included before TNAME was set).
                        var hd = p.IsTemplate ? null : w.HeaderOf(inc, p);
                        if (hd is not null && w.Skips(hd)) continue;
                        if (p.IsTemplate && uncertain && w.WalkingTemplates.Contains(inc)) continue;
                        if (!uncertain && p.GuardDefine >= 0 && hd is not null) hd.Certain = true;
                        w.Close(stretch);
                        var frame = w.Push();
                        if (hd is not null) hd.Walking = true;
                        var outerTemplate = p.IsTemplate && w.WalkingTemplates.Add(inc);
                        // What a walk that may be no include defines is an alternative (as under an #if), never what settles a name.
                        Walk(p, w, unitLine ?? op.First + 1, uncertain ? depth : depth + 1, uncertain ? Math.Max(ifDepth, 1) : ifDepth, uncertain);
                        if (hd is not null) hd.Walking = false;
                        if (outerTemplate) w.WalkingTemplates.Remove(inc);
                        w.Pop();
                        if (hd is not null) frame.Deps.Remove(hd);   // itself, included inside its own walk: nothing, as its guard makes it
                        if (w.Looking.Count > 0) w.TakeInto(frame);
                        if (hd is not null) w.Record(hd, frame);
                        w.Trail.Add(stretch = new Stretch(parsed, ifBase, k + 1, scope)); stretches.Add(stretch);
                    }
                    break;
            }
        }
        stretch.To = parsed.Ops.Length;
        w.Close(stretch);
        Flush();
        w.End(scope, stretches);
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
