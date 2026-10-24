using System.Buffers;
using System.Runtime.CompilerServices;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Emit;

// Rule 3 of docs/TESTING.md: a define kept only for a paste is a name ONE paste use builds. A use is a call of a paste
// macro (or a call passing one), instantiated through forwarders, aliases and higher-order passes with the arguments it
// was given; each ## chain builds the cross product of its slots. Pieces from different calls never pair up: work's
// 1.0.337 max stage took 18.9M defines and kept 3.9M, most of them a start piece of one call and an end piece of another.
public static partial class HeaderCarver
{
    // ---------------- tokens ----------------

    private enum PpTok : byte { Ident, Number, LParen, RParen, Comma, HashHash, Other }

    private readonly record struct PpToken(PpTok Kind, int Start, int Len);

    /// <summary>A logical line's kind for a call stream: a <c>#define</c>, an <c>#if</c>-family directive opening, one
    /// restarting (<c>#elif</c>, <c>#else</c>), <c>#endif</c>, another directive, or text.</summary>
    private enum PpLine : byte { Text, Define, Cond, Else, EndIf, OtherDirective }

    /// <summary>The tokens of <paramref name="s"/> from <paramref name="from"/>: comments skipped (a block comment open at
    /// the end sets <paramref name="inBlock"/>, which the next line starts with), a string or character literal one Other,
    /// a preprocessing number one Number, <c>##</c> HashHash, any other character Other. A backslash ending a joined
    /// line is a blank.</summary>
    private static void PpLex(string s, int from, ref bool inBlock, List<PpToken> into)
    {
        int i = from, n = s.Length;
        if (inBlock)
        {
            var e = s.IndexOf("*/", i, StringComparison.Ordinal);
            if (e < 0) return;
            inBlock = false;
            i = e + 2;
        }
        while (i < n)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\f' or '\v' or '\r' or '\n' || (c == '\\' && SplicedAt(s, i))) { i++; continue; }
            if (IsIdentStart(c))
            {
                var st = i++;
                while (i < n && IsIdentChar(s[i])) i++;
                into.Add(new PpToken(PpTok.Ident, st, i - st));
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
            {
                var st = i++;
                while (i < n)
                {
                    var d = s[i];
                    if ((d == '+' || d == '-') && s[i - 1] is 'e' or 'E' or 'p' or 'P') { i++; continue; }
                    if (IsIdentChar(d) || d == '.') { i++; continue; }
                    break;
                }
                into.Add(new PpToken(PpTok.Number, st, i - st));
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '/') return;
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) { inBlock = true; return; }
                i = e + 2;
                continue;
            }
            if (c is '"' or '\'')
            {
                var st = i++;
                while (i < n && s[i] != c) { if (s[i] == '\\' && i + 1 < n) i++; i++; }
                if (i < n) i++;
                into.Add(new PpToken(PpTok.Other, st, i - st));
                continue;
            }
            switch (c)
            {
                case '(': into.Add(new PpToken(PpTok.LParen, i, 1)); i++; break;
                case ')': into.Add(new PpToken(PpTok.RParen, i, 1)); i++; break;
                case ',': into.Add(new PpToken(PpTok.Comma, i, 1)); i++; break;
                case '#' when i + 1 < n && s[i + 1] == '#': into.Add(new PpToken(PpTok.HashHash, i, 2)); i += 2; break;
                default: into.Add(new PpToken(PpTok.Other, i, 1)); i++; break;
            }
        }
    }

    /// <summary>A backslash with only blanks after it before the line break the join kept.</summary>
    private static bool SplicedAt(string s, int i)
    {
        for (i++; i < s.Length; i++)
        {
            if (s[i] == '\n') return true;
            if (s[i] is not (' ' or '\t' or '\r')) return false;
        }
        return true;
    }

    private static PpLine ClassifyLine(string s, bool inBlock)
    {
        if (inBlock) return PpLine.Text;
        var i = 0;
        while (i < s.Length && s[i] is ' ' or '\t' or '\f' or '\v' or '\r') i++;
        if (i >= s.Length || s[i] != '#') return PpLine.Text;
        i++;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        var st = i;
        while (i < s.Length && IsIdentChar(s[i])) i++;
        var w = s.AsSpan(st, i - st);
        if (w is "define") return PpLine.Define;
        if (w is "if" or "ifdef" or "ifndef") return PpLine.Cond;
        if (w is "elif" or "elifdef" or "elifndef" or "else") return PpLine.Else;
        if (w is "endif") return PpLine.EndIf;
        return PpLine.OtherDirective;
    }

    /// <summary>A preprocessing number made only of identifier characters (<c>1</c>, <c>0x1F</c>; not <c>1.5</c>): a
    /// paste of one builds a name.</summary>
    private static bool IsIdNumber(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!IsIdentChar(c)) return false;
        return s.Length > 0;
    }

    // ---------------- a #define ----------------

    /// <summary>A parsed <c>#define</c>: name, parameters (<c>...</c> is <c>__VA_ARGS__</c>, GNU's <c>args...</c> is
    /// <c>args</c>) and body tokens. One instance is reused line after line.</summary>
    private sealed class PpDefine
    {
        public string Text = "";
        public int NameStart, NameLen, BodyStart;
        public bool IsFunc, Variadic, EndsInComment;
        public string? VariadicName;
        public readonly List<string> Params = new();
        public readonly List<PpToken> Body = new();

        public ReadOnlySpan<char> NameSpan => Text.AsSpan(NameStart, NameLen);
        public ReadOnlySpan<char> Tok(int k) => Text.AsSpan(Body[k].Start, Body[k].Len);

        public bool Parse(string s)
        {
            Text = s;
            Params.Clear();
            Body.Clear();
            IsFunc = Variadic = EndsInComment = false;
            VariadicName = null;
            var i = 0;
            while (i < s.Length && s[i] is ' ' or '\t' or '\f' or '\v' or '\r') i++;
            if (i >= s.Length || s[i] != '#') return false;
            i++;
            Blank(s, ref i);
            if (!s.AsSpan(i).StartsWith("define", StringComparison.Ordinal)) return false;
            i += 6;
            Blank(s, ref i);
            while (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')   // #define /*x*/NAME
            {
                var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) return false;
                i = e + 2;
                Blank(s, ref i);
            }
            if (i >= s.Length || !IsIdentStart(s[i])) return false;
            NameStart = i;
            while (i < s.Length && IsIdentChar(s[i])) i++;
            NameLen = i - NameStart;
            if (i < s.Length && s[i] == '(')
            {
                IsFunc = true;
                i++;
                while (i < s.Length)
                {
                    Blank(s, ref i);
                    if (i >= s.Length) break;
                    if (s[i] == ')') { i++; break; }
                    if (s.AsSpan(i).StartsWith("...", StringComparison.Ordinal)) { Variadic = true; VariadicName = "__VA_ARGS__"; i += 3; continue; }
                    if (IsIdentStart(s[i]))
                    {
                        var st = i;
                        while (i < s.Length && IsIdentChar(s[i])) i++;
                        var p = s[st..i];
                        Blank(s, ref i);
                        if (s.AsSpan(i).StartsWith("...", StringComparison.Ordinal)) { Variadic = true; VariadicName = p; i += 3; }
                        Params.Add(p);
                        continue;
                    }
                    i++;   // a comma, or malformed
                }
                if (Variadic && VariadicName is not null && !Params.Contains(VariadicName)) Params.Add(VariadicName);
            }
            BodyStart = i;
            var ib = false;
            PpLex(s, i, ref ib, Body);
            EndsInComment = ib;
            return true;
        }

        private static void Blank(string s, ref int i)
        {
            while (i < s.Length && (s[i] is ' ' or '\t' or '\f' or '\v' or '\r' or '\n' || (s[i] == '\\' && SplicedAt(s, i)))) i++;
        }

        public int ParamIndex(ReadOnlySpan<char> id)
        {
            for (var p = 0; p < Params.Count; p++) if (id.SequenceEqual(Params[p])) return p;
            return -1;
        }

        public bool IsParam(ReadOnlySpan<char> id) => IsFunc && ParamIndex(id) >= 0;

        /// <summary>Rule 1: some <c>##</c> has a parameter beside it, other than <c>, ## __VA_ARGS__</c>.</summary>
        public bool PastesParam()
        {
            if (!IsFunc) return false;
            for (var k = 0; k < Body.Count; k++)
            {
                if (Body[k].Kind != PpTok.HashHash) continue;
                var l = k > 0 && Body[k - 1].Kind == PpTok.Ident && IsParam(Tok(k - 1));
                var r = k + 1 < Body.Count && Body[k + 1].Kind == PpTok.Ident && IsParam(Tok(k + 1));
                if (r && k > 0 && Body[k - 1].Kind == PpTok.Comma && VariadicName is not null && Tok(k + 1).SequenceEqual(VariadicName)) r = false;
                if (l || r) return true;
            }
            return false;
        }

        public bool HasLParen()
        {
            foreach (var t in Body) if (t.Kind == PpTok.LParen) return true;
            return false;
        }
    }

    // ---------------- what every define says (rule 1, calls, emptiness) ----------------

    /// <summary>A growing list of 64-bit keys in chunks: tens of millions of them at work, with no doubling copies.</summary>
    private sealed class ULongs
    {
        private readonly List<ulong[]> full = new();
        private ulong[] cur = new ulong[1 << 12];
        private int n;
        public long Count { get; private set; }

        public void Add(ulong v)
        {
            if (n == cur.Length)
            {
                full.Add(cur);
                cur = new ulong[Math.Min(cur.Length * 2, 1 << 20)];
                n = 0;
            }
            cur[n++] = v;
            Count++;
        }

        public void AddRange(List<ulong> vs)
        {
            foreach (var v in vs) Add(v);
        }

        public ulong[] ToArray()
        {
            var a = new ulong[Count];
            long at = 0;
            foreach (var c in full) { c.CopyTo(a, at); at += c.Length; }
            Array.Copy(cur, 0, a, at, n);
            return a;
        }
    }

    /// <summary>One file's define facts, merged into <see cref="DefineFacts"/> in file order.</summary>
    private sealed class FileFacts
    {
        public readonly List<ulong> EdgeFrom = new(), EdgeTo = new(), Called = new(), Forwarders = new(), Direct = new(), Under = new();
        public readonly List<ulong> EmptyName = new(), EmptyItems = new();
        public readonly List<int> EmptyCount = new();
        public int Defines;
        private readonly List<bool> parenIsCall = new();

        /// <summary>Rule 1's edges (a function-like body with a call forwards to every name in it, an object-like body
        /// ending in a name is an alias of it), whether it pastes a parameter, the names it calls, and what it waits on
        /// to expand to nothing.</summary>
        public void Add(PpDefine d, bool big)
        {
            Defines++;
            var key = NameKey.Short(d.NameSpan);
            var pastes = d.PastesParam();
            if (pastes) Direct.Add(key);
            var b = d.Body;
            if (d.IsFunc && d.HasLParen())
            {
                for (var k = 0; k < b.Count; k++)
                    if (b[k].Kind == PpTok.Ident && !d.IsParam(d.Tok(k))) { EdgeFrom.Add(key); EdgeTo.Add(NameKey.Short(d.Tok(k))); }
                if (big && !pastes) Forwarders.Add(key);
            }
            // A body ending in a name forwards to it: an alias, or a function-like macro whose result is called
            // (`#define PICK(x) CAT`, then `PICK(1)(A, B)`).
            else if (b.Count > 0 && b[^1].Kind == PpTok.Ident && !d.IsParam(d.Tok(b.Count - 1))) { EdgeFrom.Add(key); EdgeTo.Add(NameKey.Short(d.Tok(b.Count - 1))); }
            if (d.NameSpan.StartsWith("__", StringComparison.Ordinal)) Under.Add(key);
            ScanCalls(d);
            EmptyCandidate(d, key);
        }

        // A name followed by (, one inside a call's parentheses, and an object-like body's last name are called.
        private void ScanCalls(PpDefine d)
        {
            var b = d.Body;
            var calls = 0;
            parenIsCall.Clear();
            for (var k = 0; k < b.Count; k++)
            {
                switch (b[k].Kind)
                {
                    case PpTok.LParen:
                        var call = k > 0 && b[k - 1].Kind == PpTok.Ident;
                        parenIsCall.Add(call);
                        if (call) calls++;
                        break;
                    case PpTok.RParen:
                        if (parenIsCall.Count > 0)
                        {
                            if (parenIsCall[^1]) calls--;
                            parenIsCall.RemoveAt(parenIsCall.Count - 1);
                        }
                        break;
                    case PpTok.Ident:
                        if (d.IsParam(d.Tok(k))) break;
                        if (calls > 0 || (k + 1 < b.Count && b[k + 1].Kind == PpTok.LParen) || (!d.IsFunc && k == b.Count - 1))
                            Called.Add(NameKey.Short(d.Tok(k)));
                        break;
                }
            }
        }

        // A body of only ##, parameters, names and calls of names expands to nothing when its names (callees) do.
        private void EmptyCandidate(PpDefine d, ulong key)
        {
            var b = d.Body;
            var start = EmptyItems.Count;
            for (var k = 0; k < b.Count; k++)
            {
                var kind = b[k].Kind;
                if (kind == PpTok.HashHash) continue;
                if (kind != PpTok.Ident) { EmptyItems.RemoveRange(start, EmptyItems.Count - start); return; }
                var param = d.IsParam(d.Tok(k));
                var call = k + 1 < b.Count && b[k + 1].Kind == PpTok.LParen;
                if (param && call) { EmptyItems.RemoveRange(start, EmptyItems.Count - start); return; }
                if (param) continue;
                EmptyItems.Add(NameKey.Short(d.Tok(k)));
                if (!call) continue;
                int depth = 0, j = k + 1;
                for (; j < b.Count; j++)
                {
                    if (b[j].Kind == PpTok.LParen) depth++;
                    else if (b[j].Kind == PpTok.RParen && --depth == 0) break;
                }
                k = j;
            }
            EmptyName.Add(key);
            EmptyCount.Add(EmptyItems.Count - start);
        }
    }

    /// <summary>
    /// What every define in reach says, held as 64-bit name keys (see <see cref="NameKey"/>): no define's text. Work's
    /// 1.0.338 max stage gave up at 18 GB learning paste macros from 18.9M big-header defines held as strings.
    /// <see cref="Resolve"/> turns them into the paste macros (rule 1 to a fixpoint), the paste macros something calls,
    /// and the names that can expand to nothing, and lets the rest go.
    /// </summary>
    private sealed class DefineFacts
    {
        private ULongs edgeFrom = new(), edgeTo = new(), called = new(), forwarders = new(), emptyName = new(), emptyItems = new();
        private List<int> emptyCount = new();
        private readonly HashSet<ulong> direct = new();
        public readonly HashSet<ulong> Under = new();
        public HashSet<ulong> Pasters { get; private set; } = new();
        public HashSet<ulong> CalledPasters { get; private set; } = new();
        public HashSet<ulong> Emptyable { get; private set; } = new();
        public long Defines, Edges;
        public int ForwardingMacros;
        private ulong[] fwdFrom = Array.Empty<ulong>(), fwdTo = Array.Empty<ulong>();

        /// <summary>The paste macros <paramref name="key"/> forwards to, directly or through others (not itself).</summary>
        public void ForwardsTo(ulong key, Func<ulong, bool> add)
        {
            var work = new Stack<ulong>();
            work.Push(key);
            while (work.Count > 0)
            {
                var x = work.Pop();
                for (var k = LowerBound(fwdFrom, x); k < fwdFrom.Length && fwdFrom[k] == x; k++)
                    if (fwdTo[k] != key && add(fwdTo[k])) work.Push(fwdTo[k]);
            }
        }

        public void Merge(FileFacts f)
        {
            Defines += f.Defines;
            edgeFrom.AddRange(f.EdgeFrom);
            edgeTo.AddRange(f.EdgeTo);
            called.AddRange(f.Called);
            forwarders.AddRange(f.Forwarders);
            direct.UnionWith(f.Direct);
            Under.UnionWith(f.Under);
            emptyName.AddRange(f.EmptyName);
            emptyItems.AddRange(f.EmptyItems);
            emptyCount.AddRange(f.EmptyCount);
        }

        public void Resolve()
        {
            Edges = edgeFrom.Count;
            // Rule 1: a name forwarding to a paste macro is one (a reverse closure from the direct ones).
            var from = edgeFrom.ToArray();
            var to = edgeTo.ToArray();
            edgeFrom = edgeTo = new ULongs();
            Array.Sort(to, from);
            var pasters = new HashSet<ulong>(direct);
            var work = new Stack<ulong>(direct);
            while (work.Count > 0)
            {
                var x = work.Pop();
                for (var k = LowerBound(to, x); k < to.Length && to[k] == x; k++)
                    if (pasters.Add(from[k])) work.Push(from[k]);
            }
            Pasters = pasters;
            // The edges into paste macros, by where they start: a use asks for a paste macro's defines and every paste
            // macro it forwards to at once (a chain of 40 forwarders took 40 reads of every big header, one a hop).
            var n = 0;
            for (var k = 0; k < to.Length; k++)
                if (pasters.Contains(to[k])) { from[n] = from[k]; to[n] = to[k]; n++; }
            Array.Resize(ref from, n);
            Array.Resize(ref to, n);
            Array.Sort(from, to);
            fwdFrom = from;
            fwdTo = to;
            from = to = Array.Empty<ulong>();

            var c = called.ToArray();
            called = new ULongs();
            foreach (var k in c) if (pasters.Contains(k)) CalledPasters.Add(k);
            var f = forwarders.ToArray();
            forwarders = new ULongs();
            ForwardingMacros = f.Distinct().Count();

            // Which names can expand to nothing: a least fixpoint, a name being so when any of its defines is.
            var names = emptyName.ToArray();
            var items = emptyItems.ToArray();
            var counts = emptyCount.ToArray();
            emptyName = emptyItems = new ULongs();
            emptyCount = new List<int>();
            var cand = new int[items.Length];
            var waiting = new int[names.Length];
            var ready = new Stack<int>();
            for (int i = 0, at = 0; i < names.Length; i++)
            {
                waiting[i] = counts[i];
                for (var j = 0; j < counts[i]; j++) cand[at++] = i;
                if (counts[i] == 0) ready.Push(i);
            }
            Array.Sort(items, cand);
            while (ready.Count > 0)
            {
                var name = names[ready.Pop()];
                if (!Emptyable.Add(name)) continue;
                for (var k = LowerBound(items, name); k < items.Length && items[k] == name; k++)
                    if (--waiting[cand[k]] == 0) ready.Push(cand[k]);
            }
        }

        private static int LowerBound(ulong[] a, ulong x)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (a[mid] < x) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    // ---------------- call streams ----------------

    /// <summary>A token of a call's argument or a define body as rule 3 reads it.</summary>
    private struct XTok
    {
        public const byte Ident = 0, Num = 1, LParen = 2, RParen = 3, Comma = 4, Other = 5, Param = 6, Wild = 7, Hash = 8;
        /// <summary>A name id (Ident, and Num of identifier characters), a parameter index (Param) or a wildcard kind (Wild).</summary>
        public int Id;
        public byte K;
        /// <summary>Stands for its expansion too: it came through a parameter used outside <c>##</c>.</summary>
        public bool Exp;

        public XTok(byte k, int id = -1, bool exp = false)
        {
            K = k;
            Id = id;
            Exp = exp;
        }
    }

    private const byte EvUse = 0, EvChain = 1, EvCalled = 2, EvNamed = 3;

    /// <summary>One event of a stream. A computed call (<c>CAT(F, 7)(x)</c>) has its <paramref name="Head"/>: the call whose
    /// result it calls. A call open across a directive is <paramref name="Crossed"/>: its arguments hold every branch's
    /// tokens.</summary>
    private readonly record struct UseEvent(byte Kind, int Id, List<XTok>[]? Args, bool Pool, bool Eof, int[]? Ops, UseHead? Head = null, bool Crossed = false);

    /// <summary>The call a computed call calls the result of: its callee (-1 if computed too, with its own head), its
    /// arguments, and whether they hold several branches' tokens.</summary>
    private sealed record UseHead(int Callee, List<XTok>[] Args, UseHead? Head, bool Crossed);

    /// <summary>What one file's streams found, in order, its names local to it: read on many threads, folded in in file
    /// order (<see cref="UseEngine.Fold"/>).</summary>
    private sealed class UseEvents(HashSet<ulong> pasters)
    {
        private readonly Dictionary<ulong, int> byKey = new();
        public readonly List<string> Names = new();
        private readonly List<bool> paster = new();
        private readonly HashSet<(byte, int)> marked = new();
        public readonly List<UseEvent> Events = new();

        public int Id(ReadOnlySpan<char> s)
        {
            var key = NameKey.Short(s);
            if (byKey.TryGetValue(key, out var id)) return id;
            id = Names.Count;
            byKey[key] = id;
            Names.Add(s.ToString());
            paster.Add(pasters.Contains(key));
            return id;
        }

        public bool IsPaster(int id) => id >= 0 && paster[id];

        /// <summary>A paste macro called or named (nothing else is asked about either).</summary>
        public void Mark(byte kind, int id)
        {
            if (IsPaster(id) && marked.Add((kind, id))) Events.Add(new UseEvent(kind, id, null, false, false, null));
        }
    }

    /// <summary>
    /// Rule 3's call stream: follows <c>##</c> chains and calls through a token stream that spans lines and conditional
    /// directives, and reports each paste use with its arguments, each literal chain, and the paste macros called and
    /// named. A define body is its own stream, where a call or chain holding one of its parameters is a template (built
    /// when the define is used). A call still open at the end is used, its missing arguments wildcards.
    /// </summary>
    private sealed class UseTracker(UseEvents ev)
    {
        private const int OpParam = -2, OpOther = -1;

        private sealed class Frame
        {
            public int Callee = -1;                   // the name called (-1: computed, or a parameter)
            public int Open;                          // its ( in the buffer
            public bool Marked, Computed, Crossed, Template;
            public int Depth;                         // parentheses open inside the current argument
            public UseHead? Head;                    // a computed call: the use whose result it calls
            public List<int>? Also;                   // paste macros left last in an earlier branch: called with these arguments too
            public Frame Clone() => (Frame)MemberwiseClone();
        }

        private sealed class State
        {
            public readonly List<Frame> Frames = new();
            public List<int>? Left;                   // paste macros left last in a branch that ended at an #elif or #else
            public int BufLen;
            public bool LastClosedUse;
            public UseHead? LastUse;
            public bool HasPending;
            public int Pending;
        }

        private PpDefine? prm;
        private List<Frame> frames = new();
        private readonly List<State> conds = new();
        private readonly List<XTok> buf = new();       // the tokens since the outermost open call
        private int pending = OpOther;                 // a name not yet known to be called (a name id or OpParam)
        private bool hasPending, lastClosedUse;
        private List<int>? left;                      // just after an #endif: paste macros earlier branches left last
        private UseHead? lastUse;                     // the use that just closed, when lastClosedUse
        private readonly List<int> chain = new();      // the ## chain being read: its operands
        private bool afterHash;
        private int last = OpOther;                    // the previous token as a ## operand
        private bool hasLast;

        public void Begin(PpDefine? define)
        {
            prm = define is { IsFunc: true } ? define : null;
            isObjectBody = define is { IsFunc: false };
            frames.Clear();
            conds.Clear();
            buf.Clear();
            hasPending = lastClosedUse = afterHash = hasLast = false;
            lastUse = null;
            left = null;
            chain.Clear();
        }

        private bool isObjectBody;
        private Frame? Top => frames.Count > 0 ? frames[^1] : null;

        private int Operand(PpTok kind, ReadOnlySpan<char> text)
        {
            if (kind == PpTok.Ident) return prm is not null && prm.IsParam(text) ? OpParam : ev.Id(text);
            if (kind == PpTok.Number && IsIdNumber(text)) return ev.Id(text);
            return OpOther;
        }

        private void Buf(XTok t)
        {
            if (frames.Count > 0) buf.Add(t);
        }

        private static XTok AsTok(PpTok kind, int op) => kind switch
        {
            PpTok.Ident => op == OpParam ? new XTok(XTok.Param) : new XTok(XTok.Ident, op),
            PpTok.Number => op >= 0 ? new XTok(XTok.Num, op) : new XTok(XTok.Other),
            PpTok.LParen => new XTok(XTok.LParen),
            PpTok.RParen => new XTok(XTok.RParen),
            PpTok.Comma => new XTok(XTok.Comma),
            _ => new XTok(XTok.Other),
        };

        public void Token(PpTok kind, ReadOnlySpan<char> text)
        {
            var leftHere = left;   // only a ( right after the #endif calls them
            left = null;
            if (kind == PpTok.HashHash)
            {
                if (!afterHash)
                {
                    if (chain.Count == 0) chain.Add(hasLast ? last : OpOther);
                    afterHash = true;
                }
                FinalizePending();
                Buf(new XTok(XTok.Other));
                lastClosedUse = false;
                return;
            }
            var op = Operand(kind, text);
            if (afterHash) { chain.Add(op); afterHash = false; }
            else FlushChain();
            last = op;
            hasLast = true;

            if (kind == PpTok.Ident)
            {
                FinalizePending();
                Buf(AsTok(kind, op));
                pending = op;
                hasPending = true;
                lastClosedUse = false;
                return;
            }
            switch (kind)
            {
                case PpTok.LParen:
                    if (hasPending)
                    {
                        var callee = pending;
                        hasPending = false;
                        if (callee >= 0) ev.Mark(EvCalled, callee);
                        if (leftHere is not null) foreach (var a in leftHere) ev.Mark(EvCalled, a);
                        buf.Add(new XTok(XTok.LParen));
                        frames.Add(new Frame { Callee = callee >= 0 ? callee : -1, Open = buf.Count - 1, Template = callee == OpParam, Also = leftHere });
                    }
                    else if (leftHere is not null)
                    {
                        foreach (var a in leftHere) ev.Mark(EvCalled, a);
                        buf.Add(new XTok(XTok.LParen));
                        frames.Add(new Frame { Open = buf.Count - 1, Also = leftHere });
                    }
                    else if (lastClosedUse)
                    {
                        buf.Add(new XTok(XTok.LParen));
                        frames.Add(new Frame { Computed = true, Open = buf.Count - 1, Head = lastUse });
                    }
                    else if (Top is { } t) { t.Depth++; buf.Add(new XTok(XTok.LParen)); }
                    lastClosedUse = false;
                    break;
                case PpTok.RParen:
                    FinalizePending();
                    lastClosedUse = false;
                    if (frames.Count == 0) break;
                    var top = frames[^1];
                    buf.Add(new XTok(XTok.RParen));
                    if (top.Depth > 0) { top.Depth--; break; }
                    lastClosedUse = Close(buf.Count - 1, eof: false);
                    break;
                default:
                    FinalizePending();
                    Buf(AsTok(kind, op));
                    lastClosedUse = false;
                    break;
            }
        }

        // A name that was not called: inside a call it is passed (a paste macro passed marks every open call: a call of
        // ID(CAT) passes CAT on to the call around it), outside one named.
        private void FinalizePending()
        {
            if (!hasPending) return;
            hasPending = false;
            if (pending < 0) return;
            if (frames.Count > 0)
            {
                ev.Mark(EvCalled, pending);
                if (ev.IsPaster(pending)) foreach (var f in frames) f.Marked = true;
            }
            else ev.Mark(EvNamed, pending);
        }

        // A chain with no parameter builds its literal name; one with a parameter is a template.
        private void FlushChain()
        {
            var n = chain.Count;
            if (n < 2) { chain.Clear(); return; }
            var lit = true;
            foreach (var o in chain) if (o < 0) { lit = false; break; }
            if (lit) ev.Events.Add(new UseEvent(EvChain, -1, null, false, false, chain.ToArray()));
            chain.Clear();
        }

        /// <summary>Closes the top call, its ) at <paramref name="close"/> (or the buffer's end): a paste use, a
        /// higher-order use, or a computed or crossed one with its own pool. True if it was a use.</summary>
        private bool Close(int close, bool eof)
        {
            var f = frames[^1];
            frames.RemoveAt(frames.Count - 1);
            var use = f.Computed || (f.Callee >= 0 && (f.Marked || ev.IsPaster(f.Callee)));
            lastUse = null;
            if ((use || f.Also is not null) && !f.Template)
            {
                var inner = buf.GetRange(f.Open + 1, close - f.Open - 1);
                var template = false;
                foreach (var t in inner) if (t.K == XTok.Param) { template = true; break; }
                if (!template)
                {
                    var args = Split(inner);
                    if (use)
                    {
                        var head = f.Computed ? f.Head : null;
                        ev.Events.Add(new UseEvent(EvUse, f.Callee, args, f.Computed || f.Crossed, eof, null, head, f.Crossed));
                        lastUse = new UseHead(f.Callee, args, head, f.Crossed);
                    }
                    AlsoUses(f, args, eof);
                }
            }
            if (frames.Count == 0) buf.RemoveRange(Floor, buf.Count - Floor);
            return use;
        }

        // The paste macros earlier branches left last, called by this ( too (CAT, #else, PLAIN, #endif, (HW_, 7)).
        private void AlsoUses(Frame f, List<XTok>[] args, bool eof)
        {
            if (f.Also is null) return;
            foreach (var a in f.Also) ev.Events.Add(new UseEvent(EvUse, a, args, f.Crossed, eof, null, null, f.Crossed));
        }

        private int Floor => conds.Count > 0 ? Math.Min(conds[^1].BufLen, buf.Count) : 0;

        /// <summary>A conditional directive: <c>#if</c>-family opens, <c>#elif</c>/<c>#else</c> restarts from the state
        /// at the <c>#if</c>, <c>#endif</c> goes on from the last branch. A call open across one has its own pool. A name
        /// not yet known to be called stays so: a <c>(</c> after the directive calls it (<c>CAT</c>, <c>#if A</c>,
        /// <c>(HW_, 7)</c>).</summary>
        public void Directive(PpLine kind)
        {
            FlushChain();
            afterHash = hasLast = false;
            foreach (var f in frames) f.Crossed = true;
            var leftHere = left;   // an inner #endif's, so left last in this branch too
            left = null;
            switch (kind)
            {
                case PpLine.Cond:
                    conds.Add(Snapshot());
                    break;
                case PpLine.Else:
                    if (conds.Count > 0)
                    {
                        // A paste macro last in the branch ending: a ( right after the #endif calls it, with the
                        // arguments that follow.
                        var s = conds[^1];
                        if (hasPending && pending >= 0 && ev.IsPaster(pending)) (s.Left ??= new List<int>()).Add(pending);
                        if (leftHere is not null) (s.Left ??= new List<int>()).AddRange(leftHere);
                        FinalizePending();
                        Restore(s);
                    }
                    break;
                case PpLine.EndIf:
                    if (conds.Count > 0)
                    {
                        // A call open since the #if was passed a paste macro in an earlier branch: it still was.
                        var s = conds[^1];
                        for (var k = 0; k < Math.Min(frames.Count, s.Frames.Count); k++)
                            if (frames[k].Open == s.Frames[k].Open) frames[k].Marked |= s.Frames[k].Marked;
                        conds.RemoveAt(conds.Count - 1);
                        // (the last branch's own name is still pending)
                        if (s.Left is not null) { left = s.Left; if (leftHere is not null) left.AddRange(leftHere); }
                        else left = leftHere;
                    }
                    break;
            }
        }

        private State Snapshot()
        {
            var s = new State { LastClosedUse = lastClosedUse, LastUse = lastUse, BufLen = buf.Count, HasPending = hasPending, Pending = pending };
            foreach (var f in frames) s.Frames.Add(f.Clone());
            return s;
        }

        /// <summary>Back to the state at the <c>#if</c>. A call open since then ends in a later branch or after the
        /// <c>#endif</c>: it is used now with its own pool, the rest of its arguments any text. A call open at the
        /// <c>#if</c> keeps this branch's tokens: it is pooled (crossed), and the branch may be the one compiled.</summary>
        private void Restore(State s)
        {
            for (var k = frames.Count - 1; k >= 0 && frames[k].Open >= s.BufLen; k--)
            {
                var f = frames[k];
                var use = f.Computed || (f.Callee >= 0 && (f.Marked || ev.IsPaster(f.Callee)));
                if (f.Template || !(use || f.Also is not null)) continue;
                var inner = buf.GetRange(f.Open + 1, buf.Count - f.Open - 1);
                if (inner.Exists(t => t.K == XTok.Param)) continue;
                var args = Split(inner);
                if (use) ev.Events.Add(new UseEvent(EvUse, f.Callee, args, true, true, null));
                if (f.Also is not null) foreach (var a in f.Also) ev.Events.Add(new UseEvent(EvUse, a, args, true, true, null));
            }
            // A call open at the #if passed a paste macro in this branch: it was passed one whatever branch is compiled.
            for (var k = 0; k < Math.Min(frames.Count, s.Frames.Count); k++)
                if (frames[k].Open == s.Frames[k].Open) s.Frames[k].Marked |= frames[k].Marked;
            frames = new List<Frame>(s.Frames.Count);
            foreach (var f in s.Frames) frames.Add(f.Clone());
            if (frames.Count == 0 && buf.Count > s.BufLen) buf.RemoveRange(s.BufLen, buf.Count - s.BufLen);
            lastClosedUse = s.LastClosedUse;
            lastUse = s.LastUse;
            hasPending = s.HasPending;
            pending = s.Pending;
        }

        /// <summary>End of the stream: open calls are closed top-down, used with what they have. An object-like body's
        /// last name is called (an alias).</summary>
        public void End()
        {
            if (hasPending && frames.Count == 0 && isObjectBody)
            {
                hasPending = false;
                if (pending >= 0) ev.Mark(EvCalled, pending);
            }
            FinalizePending();
            FlushChain();
            while (frames.Count > 0) Close(buf.Count, eof: true);
            frames.Clear();
            conds.Clear();
            buf.Clear();
            prm = null;
            isObjectBody = false;
        }

        public static List<XTok>[] Split(List<XTok> inner)
        {
            var args = new List<List<XTok>> { new() };
            var depth = 0;
            foreach (var t in inner)
            {
                if (t.K == XTok.LParen) depth++;
                else if (t.K == XTok.RParen) depth--;
                else if (t.K == XTok.Comma && depth == 0) { args.Add(new()); continue; }
                args[^1].Add(t);
            }
            return args.ToArray();
        }
    }

    /// <summary>A define's body as its own stream.</summary>
    private static void StreamBody(UseTracker t, PpDefine d)
    {
        t.Begin(d);
        for (var k = 0; k < d.Body.Count; k++) t.Token(d.Body[k].Kind, d.Tok(k));
        t.End();
    }

    /// <summary>One logical line of a file's stream: a conditional directive moves it, text is its tokens, another
    /// directive or a define is not in it. Every line carries the block comment it leaves open. An <c>#if</c> or
    /// <c>#elif</c> condition is a stream of its own (<paramref name="cond"/>): <c>#if XCAT(CFG_, 1)</c> builds CFG_1.</summary>
    private static void StreamLine(UseTracker t, UseTracker cond, string line, ref bool inBlock, List<PpToken> toks)
    {
        var kind = ClassifyLine(line, inBlock);
        toks.Clear();
        PpLex(line, 0, ref inBlock, toks);
        switch (kind)
        {
            case PpLine.Cond or PpLine.Else or PpLine.EndIf:
                t.Directive(kind);
                // # and the directive's name, then the condition (#ifdef, #else and #endif have no calls)
                if (kind is PpLine.Cond or PpLine.Else && toks.Count > 2 && line.AsSpan(toks[1].Start, toks[1].Len) is "if" or "elif")
                {
                    cond.Begin(null);
                    for (var k = 2; k < toks.Count; k++) cond.Token(toks[k].Kind, line.AsSpan(toks[k].Start, toks[k].Len));
                    cond.End();
                }
                return;
            case PpLine.Text:
                foreach (var k in toks) t.Token(k.Kind, line.AsSpan(k.Start, k.Len));
                return;
        }
    }

    /// <summary>Files whose text the C preprocessor reads: their calls are paste uses. A kept file of another kind
    /// (a script, a linker map, a data table) only names.</summary>
    private static bool StreamsCalls(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext == ".S" || ext == ".sx") return true;
        return ext.ToLowerInvariant() is ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".c++" or ".hh" or ".hpp" or ".hxx" or ".h++"
            or ".inc" or ".inl" or ".ipp" or ".tcc" or ".tpp" or ".def";
    }

    /// <summary>A kept file's streams: its non-define lines one, each define body its own.</summary>
    private static UseEvents? StreamKept(string path, HashSet<ulong> pasters)
    {
        var ev = new UseEvents(pasters);
        var main = new UseTracker(ev);
        var body = new UseTracker(ev);
        var def = new PpDefine();
        var toks = new List<PpToken>();
        var inBlock = false;
        main.Begin(null);
        foreach (var line in LogicalLines(path))
        {
            if (ClassifyLine(line, inBlock) == PpLine.Define && def.Parse(line))
            {
                StreamBody(body, def);
                inBlock = def.EndsInComment;
                continue;
            }
            StreamLine(main, body, line, ref inBlock, toks);
        }
        main.End();
        return ev.Events.Count > 0 ? ev : null;
    }

    // ---------------- uses ----------------

    /// <summary>What the uses build: exact names (by key), patterns (a prefix, one or more digits or anything, a
    /// suffix), and the pools of uses too wide to instantiate (a name starting with one of a pool's start pieces and
    /// ending, after it, with one of its end pieces). Only changed between batches, read on many threads during one.</summary>
    private sealed class UseResults
    {
        public readonly HashSet<ulong> Exact = new();
        private readonly HashSet<(string, byte, string)> patternSeen = new();
        private readonly Dictionary<string, SufSet> byPre = new(StringComparer.Ordinal);
        private readonly List<int> preLens = new();
        private readonly HashSet<int> preHashes = new();
        private readonly SufSet noPre = new();
        private bool anyAll;

        /// <summary>The end pieces of the patterns under one start piece, each with the wildcards before it. A few are
        /// scanned; past that they are looked up by the name's last characters, length by length: every big-header
        /// define was matched against every pattern in a list, and 16,000 patterns against 160,000 defines took 87 s.</summary>
        private sealed class SufSet
        {
            private const int Few = 8;
            private List<(string Suf, byte Wild)>? few = new();
            private Dictionary<string, byte>? wilds;   // end piece -> bit (1 << wild) for each wildcard before it
            private HashSet<int>? hashes;
            private List<int>? lens;

            public void Add(string suf, byte wild)
            {
                if (few is not null)
                {
                    few.Add((suf, wild));
                    if (few.Count <= Few) return;
                    wilds = new(StringComparer.Ordinal);
                    hashes = new();
                    lens = new();
                    foreach (var (q, w) in few) Index(q, w);
                    few = null;
                    return;
                }
                Index(suf, wild);
            }

            private void Index(string suf, byte wild)
            {
                wilds!.TryGetValue(suf, out var bits);
                wilds[suf] = (byte)(bits | (1 << wild));
                hashes!.Add(string.GetHashCode(suf.AsSpan()));
                InsertSorted(lens!, suf.Length);
            }

            /// <summary>One fits <paramref name="name"/> after its first <paramref name="p"/> characters.</summary>
            public bool Match(string name, int p)
            {
                if (few is not null)
                {
                    foreach (var (suf, wild) in few) if (Fits(name, p, suf, wild)) return true;
                    return false;
                }
                foreach (var l in lens!)
                {
                    if (p + l > name.Length) break;
                    var span = name.AsSpan(name.Length - l);
                    if (!hashes!.Contains(string.GetHashCode(span)) || !wilds!.TryGetValue(span.ToString(), out var bits)) continue;
                    if ((bits & ~(1 << UseEngine.WildDigits)) != 0) return true;
                    if (Fits(name, p, span.ToString(), UseEngine.WildDigits)) return true;
                }
                return false;
            }
        }

        private sealed class EndSet
        {
            public readonly HashSet<string> Ends = new(StringComparer.Ordinal);
            public readonly HashSet<int> Hashes = new();
            public readonly List<int> Lens = new();
            public bool AnyE, DigitsE;

            public bool Add(string e)
            {
                if (!Ends.Add(e)) return false;
                Hashes.Add(string.GetHashCode(e.AsSpan()));
                InsertSorted(Lens, e.Length);
                return true;
            }
        }

        // The pools, one per set of start pieces (two with the same starts are one with both's ends: the same names), each
        // start and end piece with the pools it is in. A name fits a pool when a start piece of it and an end piece in
        // the room left share one. Each start piece held every end piece of its pools: a call open across an #if passing
        // an alias of 1,000 names started and ended a pool with each, and every pool copied a million pieces.
        private sealed class Piece
        {
            public readonly HashSet<int> Pools = new();
            public bool AnyE, DigitsE;   // a pool it starts ends with anything, or with digits
        }

        private readonly Dictionary<string, int> poolByStarts = new(StringComparer.Ordinal);
        private readonly List<EndSet> pools = new();
        private readonly List<string[]> poolStarts = new();
        private readonly Dictionary<string, Piece> startPieces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Piece> endPieces = new(StringComparer.Ordinal);
        private readonly List<int> startLens = new(), endLens = new();
        private readonly HashSet<int> startHashes = new(), endHashes = new();
        private EndSet? anyStart;

        public int Patterns => patternSeen.Count;
        public int Pools => pools.Count + (anyStart is null ? 0 : 1);
        /// <summary>Grows with anything built.</summary>
        public long Version { get; private set; }

        public void AddExact(ulong key)
        {
            if (Exact.Add(key)) Version++;
        }

        public void AddPattern(string pre, byte wild, string suf)
        {
            if (!patternSeen.Add((pre, wild, suf))) return;
            Version++;
            if (pre.Length == 0 && suf.Length == 0 && wild == UseEngine.WildAny) { anyAll = true; return; }
            if (pre.Length == 0) { noPre.Add(suf, wild); return; }
            if (!byPre.TryGetValue(pre, out var l))
            {
                byPre[pre] = l = new();
                preHashes.Add(string.GetHashCode(pre.AsSpan()));
                InsertSorted(preLens, pre.Length);
            }
            l.Add(suf, wild);
        }

        public void AddPool(HashSet<string> starts, bool anyS, HashSet<string> ends, bool anyE, bool digitsE)
        {
            if (anyS)
            {
                anyStart ??= new EndSet();
                var before = anyStart.Ends.Count;
                var flags = (anyStart.AnyE, anyStart.DigitsE);
                foreach (var e in ends) anyStart.Add(e);
                anyStart.AnyE |= anyE;
                anyStart.DigitsE |= digitsE;
                if (anyStart.Ends.Count != before || flags != (anyStart.AnyE, anyStart.DigitsE)) Version++;
                return;
            }
            var sorted = starts.Order(StringComparer.Ordinal).ToArray();
            var sig = string.Join('\n', sorted);
            var grew = false;
            if (!poolByStarts.TryGetValue(sig, out var id))
            {
                poolByStarts[sig] = id = pools.Count;
                pools.Add(new EndSet());
                poolStarts.Add(sorted);
                foreach (var s in sorted) Of(startPieces, s, startHashes, startLens).Pools.Add(id);
                grew = true;
            }
            var set = pools[id];
            foreach (var e in ends)
                if (set.Add(e)) { Of(endPieces, e, endHashes, endLens).Pools.Add(id); grew = true; }
            if ((anyE && !set.AnyE) || (digitsE && !set.DigitsE))
            {
                set.AnyE |= anyE;
                set.DigitsE |= digitsE;
                foreach (var s in poolStarts[id])
                {
                    var p = startPieces[s];
                    p.AnyE |= anyE;
                    p.DigitsE |= digitsE;
                }
                grew = true;
            }
            if (grew) Version++;
        }

        private static Piece Of(Dictionary<string, Piece> pieces, string s, HashSet<int> hashes, List<int> lens)
        {
            if (pieces.TryGetValue(s, out var p)) return p;
            pieces[s] = p = new Piece();
            hashes.Add(string.GetHashCode(s.AsSpan()));
            InsertSorted(lens, s.Length);
            return p;
        }

        private static void InsertSorted(List<int> l, int v)
        {
            var i = l.BinarySearch(v);
            if (i < 0) l.Insert(~i, v);
        }

        public bool PatternMatch(string name)
        {
            if (anyAll) return true;
            foreach (var p in preLens)
            {
                if (p > name.Length) break;
                if (!preHashes.Contains(string.GetHashCode(name.AsSpan(0, p))) || !byPre.TryGetValue(name[..p], out var l)) continue;
                if (l.Match(name, p)) return true;
            }
            return noPre.Match(name, 0);
        }

        private static bool Fits(string name, int p, string q, byte wild)
        {
            if (name.Length < p + q.Length + (wild == UseEngine.WildDigits ? 1 : 0) || !name.EndsWith(q, StringComparison.Ordinal)) return false;
            if (wild == UseEngine.WildDigits)
                for (var i = p; i < name.Length - q.Length; i++) if (!char.IsAsciiDigit(name[i])) return false;
            return true;
        }

        public bool PoolMatch(string name)
        {
            if (anyStart is { } a && Ends(a, name, 0)) return true;
            foreach (var l in startLens)
            {
                if (l > name.Length) break;
                if (!startHashes.Contains(string.GetHashCode(name.AsSpan(0, l))) || !startPieces.TryGetValue(name[..l], out var sp)) continue;
                var room = name.Length - l;
                if (sp.AnyE || (sp.DigitsE && room >= 1 && char.IsAsciiDigit(name[^1]))) return true;
                // the end pieces in the room the start piece leaves (they never overlap)
                foreach (var el in endLens)
                {
                    if (el > room) break;
                    var span = name.AsSpan(name.Length - el);
                    if (!endHashes.Contains(string.GetHashCode(span)) || !endPieces.TryGetValue(span.ToString(), out var ep)) continue;
                    var (small, big) = sp.Pools.Count <= ep.Pools.Count ? (sp.Pools, ep.Pools) : (ep.Pools, sp.Pools);
                    foreach (var p in small) if (big.Contains(p)) return true;
                }
            }
            return false;
        }

        // Ends with one of the set's end pieces in the room the start piece leaves.
        private static bool Ends(EndSet e, string s, int startLen)
        {
            var room = s.Length - startLen;
            if (e.AnyE) return true;
            if (e.DigitsE && room >= 1 && char.IsAsciiDigit(s[^1])) return true;
            foreach (var l in e.Lens)
            {
                if (l > room) break;
                var span = s.AsSpan(s.Length - l);
                if (e.Hashes.Contains(string.GetHashCode(span)) && e.Ends.Contains(span.ToString())) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Rule 3's uses, instantiated: a port of CodeSpawner's firmware truth (FwUses), one run. Values are abstract: what
    /// can start (First) and end (Last) an argument or an expansion, and whether it can be empty.
    /// A define's body is its template. Those of kept files and the build are known from the start; a big header's are
    /// asked for (<see cref="Asked"/>) and taken from the next reads of the big headers (<see cref="Capture"/>): a use
    /// that met a name not yet read through every big header runs again when one of its defines arrives. What it built
    /// before stays: with fewer definitions it builds less.
    /// </summary>
    private sealed class UseEngine
    {
        public const int ProductCap = 4096;
        private const long WorkMax = 20_000_000;
        public const byte WildDigits = 1, WildAny = 2;

        /// <summary>One name a slot or a chain can give: exact (<see cref="Pre"/> is the name), the empty string, or a
        /// pattern: <see cref="Pre"/>, a wildcard (one or more digits, or anything), <see cref="Suf"/>. Parts are name
        /// ids, -1 for none.</summary>
        private readonly record struct El(int Pre, byte Wild, int Suf)
        {
            public static readonly El Eps = new(-1, 0, -1);
            public bool IsEps => Pre < 0 && Wild == 0;
            public bool IsExact => Pre >= 0 && Wild == 0;
        }

        private sealed class Val
        {
            public readonly HashSet<El> First = new(), Last = new();
            public bool Empty;

            public void Union(Val o)
            {
                // (the sets' own enumerators: HashSet.UnionWith takes any IEnumerable and boxes one a call)
                UnionAll(First, o.First);
                UnionAll(Last, o.Last);
                Empty |= o.Empty;
            }
        }

        /// <summary>One definition, its body as tokens (a parameter by index, <c>#param</c> as one Other).</summary>
        private sealed class Tpl
        {
            public bool IsFunc, Variadic;
            public int NParams;
            public XTok[] Body = Array.Empty<XTok>();
        }

        private sealed class Site
        {
            public int Close;
            public bool Computed;
            public readonly List<int> Callees = new();
            public List<XTok>[] Args = Array.Empty<List<XTok>>();
            public List<List<XTok>[]>? Forms;   // its arguments re-split at the commas a name expands to, if any
            public readonly Val V = new();
            public bool Inst;
        }

        private sealed class UseRec
        {
            public bool Wild;
            public int Callee;
            public List<XTok>[] Args = Array.Empty<List<XTok>>();
            public bool Pool, Eof, Crossed;
            public UseHead? Head;
        }

        private sealed class WorkLimit() : Exception("one paste use expands past the work limit");

        private readonly DefineFacts facts;
        private readonly Dictionary<ulong, List<string>> texts;
        public readonly UseResults Results = new();

        public UseEngine(DefineFacts facts, Dictionary<ulong, List<string>> keptTexts)
        {
            this.facts = facts;
            texts = keptTexts;
        }

        // ---- names ----

        private const byte BitPaster = 1, BitCalled = 2, BitNamed = 4, BitUnder = 8, BitEmpty = 16;
        private readonly Dictionary<ulong, int> idByKey = new();
        private readonly List<string> names = new();
        private readonly List<ulong> keys = new();
        private readonly List<byte> bits = new();

        private int Id(string name)
        {
            var key = NameKey.Short(name);
            if (idByKey.TryGetValue(key, out var id)) return id;
            id = names.Count;
            idByKey[key] = id;
            names.Add(name);
            keys.Add(key);
            byte b = 0;
            if (facts.Pasters.Contains(key)) b |= BitPaster;
            if (facts.CalledPasters.Contains(key)) b |= BitCalled;
            if (facts.Under.Contains(key)) b |= BitUnder;
            if (facts.Emptyable.Contains(key)) b |= BitEmpty;
            bits.Add(b);
            return id;
        }

        private bool Has(int id, byte bit) => id >= 0 && (bits[id] & bit) != 0;

        private bool SetBit(int id, byte bit)
        {
            if ((bits[id] & bit) != 0) return false;
            bits[id] |= bit;
            return true;
        }

        private bool IsPaster(int id) => Has(id, BitPaster);
        private bool Emptyable(int id) => Has(id, BitEmpty);
        public int Names => names.Count;

        // ---- templates, and those asked of the big headers ----

        private readonly Dictionary<int, List<Tpl>> tpls = new();
        private readonly PpDefine tplParse = new();
        private readonly Dictionary<ulong, int> asked = new();     // a big-header name asked for: the batch it was first read for
        private readonly HashSet<ulong> tried = new();             // read through every big header since asked
        private int nextBatch;
        private bool asking = true;
        private bool pastersRead;
        private HashSet<int>? misses;                              // the names the running use met that are not tried yet
        private long missCount;
        public long Captured { get; private set; }

        /// <summary>The names whose big-header defines the next batch's reads take (<see cref="Capture"/>).</summary>
        public Dictionary<ulong, int> Asked => asked;

        /// <summary>There are no big headers to ask: every template is known.</summary>
        public void NothingToAsk()
        {
            asking = false;
            ClearPartial();
        }

        private List<Tpl> Templates(int id)
        {
            var key = keys[id];
            if (asking && !tried.Contains(key) && !(pastersRead && IsPaster(id)))
            {
                if (asked.TryAdd(key, nextBatch) && IsPaster(id))
                    facts.ForwardsTo(key, k => !tried.Contains(k) && asked.TryAdd(k, nextBatch));
                misses?.Add(id);
                missCount++;
                missTop?.Own.Add(id);
            }
            if (tpls.TryGetValue(id, out var l)) return l;
            l = new();
            if (texts.TryGetValue(key, out var ts))
                foreach (var t in ts)
                    if (tplParse.Parse(t) && tplParse.NameSpan.SequenceEqual(names[id])) l.Add(MakeTpl(tplParse));
            tpls[id] = l;
            return l;
        }

        private const int StringifyMark = -7;

        private Tpl MakeTpl(PpDefine d)
        {
            var t = new Tpl { IsFunc = d.IsFunc, Variadic = d.Variadic, NParams = d.IsFunc ? d.Params.Count : 0 };
            var body = new List<XTok>(d.Body.Count);
            for (var k = 0; k < d.Body.Count; k++)
            {
                var s = d.Tok(k);
                switch (d.Body[k].Kind)
                {
                    case PpTok.Ident:
                        var p = d.IsFunc ? d.ParamIndex(s) : -1;
                        if (p < 0) { body.Add(new XTok(XTok.Ident, Id(s.ToString()))); break; }
                        if (body.Count > 0 && body[^1].K == XTok.Other && body[^1].Id == StringifyMark) { body[^1] = new XTok(XTok.Other); break; }
                        body.Add(new XTok(XTok.Param, p));
                        break;
                    case PpTok.Number:
                        body.Add(IsIdNumber(s) ? new XTok(XTok.Num, Id(s.ToString())) : new XTok(XTok.Other));
                        break;
                    case PpTok.LParen: body.Add(new XTok(XTok.LParen)); break;
                    case PpTok.RParen: body.Add(new XTok(XTok.RParen)); break;
                    case PpTok.Comma: body.Add(new XTok(XTok.Comma)); break;
                    case PpTok.HashHash: body.Add(new XTok(XTok.Hash)); break;
                    default:
                        body.Add(new XTok(XTok.Other, d.IsFunc && s is "#" ? StringifyMark : -1));
                        break;
                }
            }
            t.Body = body.ToArray();
            return t;
        }

        /// <summary>The first pass read every paste macro's big-header defines: none is asked for again (each level of a
        /// paste use built from another took a second pass, asking for the paste macros its new uses called).</summary>
        public void PastersRead()
        {
            pastersRead = true;
            List<ulong>? done = null;
            foreach (var key in asked.Keys)
                if (facts.Pasters.Contains(key)) (done ??= new()).Add(key);
            if (done is not null)
                foreach (var key in done) Tried(key);
        }

        /// <summary>A name every batch has been asked for: its defines are all read, so no use waits on it.</summary>
        private void Tried(ulong key)
        {
            asked.Remove(key);
            tried.Add(key);
            if (idByKey.TryGetValue(key, out var id)) waiting.Remove(id);
            if (expPartial.Count > 0) ClearPartial();
        }

        /// <summary>A batch of the big headers is about to be read: its number.</summary>
        public int BatchBegin() => nextBatch++;

        /// <summary>A big-header define of a name asked for: its uses run again at the batch's end.</summary>
        public void Capture(ulong key, string text)
        {
            if (!texts.TryGetValue(key, out var l)) texts[key] = l = new List<string>(1);
            // A define repeated word for word (the same alias in each of 250 headers) is one body: each copy was
            // instantiated again, 250 times a use.
            if (l.Contains(text)) return;
            l.Add(text);
            Captured++;
            if (!idByKey.TryGetValue(key, out var id)) return;
            tpls.Remove(id);
            ClearPartial();
            if (waiting.Remove(id, out var recs)) dirty.UnionWith(recs);
        }

        /// <summary>The batch numbered <paramref name="serial"/> is read and folded: a name asked before a whole round of
        /// <paramref name="batches"/> was read has all its defines; the uses whose defines arrived run again.</summary>
        public void BatchEnd(int serial, int batches)
        {
            List<ulong>? doneAsking = null;
            foreach (var (key, at) in asked)
                if (serial - at + 1 >= batches) (doneAsking ??= new()).Add(key);
            if (doneAsking is not null)
                foreach (var key in doneAsking) Tried(key);
            Drain();
        }

        // ---- uses held, run again when a define they met arrives ----

        private readonly List<UseRec> uses = new();
        private readonly HashSet<string> useSeen = new(StringComparer.Ordinal);
        private readonly HashSet<string> chainSeen = new(StringComparer.Ordinal);
        private readonly HashSet<int> wildDone = new();
        private readonly Dictionary<int, HashSet<UseRec>> waiting = new();
        private readonly HashSet<UseRec> dirty = new();
        private bool calledFinal;
        public long Uses => uses.Count;
        public long Chains => chainSeen.Count;
        public long Runs { get; private set; }
        public long OverWork { get; private set; }
        /// <summary>The uses left to run in this drain (the progress line's: a pass sat 12 minutes with nothing moving).</summary>
        public long Left { get; private set; }

        /// <summary>Uses waiting to run again, or names asked of the big headers: another pass has work.</summary>
        public bool Pending => asked.Count > 0 || dirty.Count > 0;

        public void Drain()
        {
            while (dirty.Count > 0)
            {
                var now = dirty.ToList();
                dirty.Clear();
                for (var i = 0; i < now.Count; i++)
                {
                    Left = now.Count - i + dirty.Count;
                    Run(now[i]);
                }
            }
            Left = 0;
        }

        /// <summary>Called now and then while uses run: the memory limit.</summary>
        public Action? Tick;

        private void Run(UseRec r)
        {
            Runs++;
            Tick?.Invoke();
            misses = new HashSet<int>();
            if (r.Wild) WildUseCore(r.Callee);
            else UseCore(r.Callee, r.Args, r.Pool, r.Eof, r.Crossed, r.Head);
            PartialMisses();
            foreach (var id in misses)
            {
                if (!waiting.TryGetValue(id, out var l)) waiting[id] = l = new();
                l.Add(r);
            }
            misses = null;
        }

        /// <summary>One file's events, its names made global.</summary>
        public void Fold(UseEvents e)
        {
            var map = new int[e.Names.Count];
            Array.Fill(map, -1);
            int G(int local) => map[local] >= 0 ? map[local] : map[local] = Id(e.Names[local]);
            foreach (var x in e.Events)
            {
                switch (x.Kind)
                {
                    case EvCalled:
                        SetBit(G(x.Id), BitCalled);
                        break;
                    case EvNamed:
                        Named(G(x.Id));
                        break;
                    case EvChain:
                        var ops = x.Ops!.Select(G).ToArray();
                        if (chainSeen.Add(string.Join(',', ops))) LiteralChain(ops);
                        break;
                    case EvUse:
                        var callee = x.Id >= 0 ? G(x.Id) : -1;
                        var args = MapArgs(x.Args!);
                        var head = MapHead(x.Head);
                        var sig = Signature(callee, args, x.Pool, x.Eof) + (x.Crossed ? "x" : "");
                        for (var hd = head; hd is not null; hd = hd.Head) sig += "@" + Signature(hd.Callee, hd.Args, hd.Crossed, false);
                        if (!useSeen.Add(sig)) break;
                        var rec = new UseRec { Callee = callee, Args = args, Pool = x.Pool, Eof = x.Eof, Crossed = x.Crossed, Head = head };
                        uses.Add(rec);
                        dirty.Add(rec);
                        break;
                }
            }

            UseHead? MapHead(UseHead? h) => h is null ? null : new UseHead(h.Callee >= 0 ? G(h.Callee) : -1, MapArgs(h.Args), MapHead(h.Head), h.Crossed);

            List<XTok>[] MapArgs(List<XTok>[] from)
            {
                var to = new List<XTok>[from.Length];
                for (var i = 0; i < to.Length; i++)
                {
                    to[i] = new List<XTok>(from[i].Count);
                    foreach (var t in from[i]) to[i].Add(t.K is XTok.Ident or XTok.Num ? new XTok(t.K, G(t.Id), t.Exp) : t);
                }
                return to;
            }
        }

        private static string Signature(int callee, List<XTok>[] args, bool pool, bool eof)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(callee).Append(pool ? 'p' : '-').Append(eof ? 'e' : '-');
            foreach (var a in args)
            {
                sb.Append('|');
                foreach (var t in a) sb.Append(t.K).Append(':').Append(t.Id).Append(t.Exp ? '+' : ' ');
            }
            return sb.ToString();
        }

        /// <summary>Every name has been streamed: a paste macro named and never called gets one use with every argument a
        /// wildcard, and so does one named from now on.</summary>
        public void CalledFinal()
        {
            calledFinal = true;
            for (var id = 0; id < names.Count; id++) if (Has(id, BitNamed)) WildUse(id);
        }

        private void Named(int id)
        {
            if (!SetBit(id, BitNamed)) return;
            if (calledFinal) WildUse(id);
        }

        private void WildUse(int id)
        {
            if (!IsPaster(id) || Has(id, BitCalled) || !wildDone.Add(id)) return;
            var rec = new UseRec { Wild = true, Callee = id };
            uses.Add(rec);
            dirty.Add(rec);
        }

        private void WildUseCore(int id)
        {
            var np = 1;
            foreach (var t in Templates(id)) if (t.IsFunc) np = Math.Max(np, t.NParams);
            var args = new List<XTok>[np];
            for (var i = 0; i < np; i++) args[i] = new List<XTok> { new(XTok.Wild, WildAny) };
            UseCore(id, args, pool: false, eof: false);
        }

        // ---- one use ----

        private long work;
        private HashSet<int> active = new();       // the macros being instantiated: not instantiated again inside
        private readonly HashSet<int> poolS = new(), poolE = new();
        private bool poolOn, poolAnyS, poolAnyE, poolDigitsE;
        private int noPool;                        // > 0 while expanding a name: its body's uses are its own stream's
        private List<XTok>[]? eofArgs;             // a call left open at the end: its missing arguments are wildcards

        private void UseCore(int callee, List<XTok>[] args, bool pool, bool eof, bool crossed = false, UseHead? head = null)
        {
            work = 0;
            depth = 0;
            expLow = int.MaxValue;   // a use cut short by the work limit can leave these set
            expGrew = false;
            ResetPool();
            try
            {
                if (pool)
                {
                    PoolArgs(args);
                    if (eof) poolAnyE = true;   // its later arguments are unknown: they can end a name
                    if (head is not null)
                    {
                        // A computed call calls the last token of its head's result: each name that can be is called too.
                        Val hv;
                        noPool++;
                        try { hv = HeadResult(head); }
                        finally { noPool--; }
                        foreach (var e in hv.Last.ToArray())
                            if (e.IsExact && Instantiable(e.Pre, args)) InstantiateEach(e.Pre, args, eof, crossed);
                    }
                    // Its callee's body too: the pieces of REGX(n) = CAT(REG_, n) are in no argument.
                    else if (callee >= 0) InstantiateEach(callee, args, eof, crossed);
                }
                else if (callee >= 0)
                {
                    eofArgs = eof ? args : null;
                    Instantiate(callee, args);
                }
            }
            catch (WorkLimit)
            {
                // Too wide to instantiate: the use's own pool (sound, wider). Its expansions get a budget of their own (the
                // spent one threw again from here); past that too, the pool takes any name.
                OverWork++;
                ResetPool();
                work = 0;
                try { PoolArgs(args); }
                catch (WorkLimit) { poolOn = poolAnyS = poolAnyE = true; }
            }
            finally { eofArgs = null; }
            FinishPool();
        }

        /// <summary>What a computed call's head gives: its callee instantiated, or for a computed head each name its own
        /// head's result can end in (<c>CAT(A, B)(x)(y)</c>).</summary>
        private Val HeadResult(UseHead h)
        {
            if (h.Callee >= 0) return InstantiateEach(h.Callee, h.Args, false, h.Crossed);
            var v = new Val();
            if (h.Head is null) return v;
            foreach (var e in HeadResult(h.Head).Last)
                if (e.IsExact && Instantiable(e.Pre, h.Args)) v.Union(InstantiateEach(e.Pre, h.Args, false, h.Crossed));
            return v;
        }

        private const int BranchFormCap = 64;

        /// <summary>Instantiates a call; a call open across an <c>#if</c> holds every branch's tokens, so it is also
        /// instantiated with each argument one of its names (<c>REGX(1 2)</c> from <c>1</c> and <c>2</c> in two branches:
        /// REGX(1), REGX(2)); past <see cref="BranchFormCap"/> forms, with wildcards.</summary>
        private Val InstantiateEach(int q, List<XTok>[] args, bool eof, bool crossed)
        {
            Val? v = null;
            foreach (var a in crossed ? BranchForms(args) : [args])
            {
                eofArgs = eof ? a : null;
                Join(ref v, Instantiate(q, a));
            }
            eofArgs = null;
            return v ?? new Val();
        }

        /// <summary>Adds a fresh result to <paramref name="acc"/>, taking it whole as the first: a name with one define
        /// copied each result into a new pair of sets.</summary>
        private static void Join(ref Val? acc, Val r)
        {
            if (acc is null) acc = r;
            else acc.Union(r);
        }

        private static List<List<XTok>[]> BranchForms(List<XTok>[] args)
        {
            var opts = new List<List<XTok>>[args.Length];
            long total = 1;
            for (var i = 0; i < args.Length && total <= BranchFormCap; i++)
            {
                var o = new List<List<XTok>> { args[i] };
                if (args[i].Count > 1)
                {
                    var seen = new HashSet<(byte, int)>();
                    foreach (var t in args[i])
                        if (t.K is XTok.Ident or XTok.Num or XTok.Wild && seen.Add((t.K, t.Id))) o.Add([t]);
                }
                opts[i] = o;
                total *= o.Count;
            }
            if (total > BranchFormCap)
            {
                var wild = new List<XTok>[args.Length];
                for (var i = 0; i < wild.Length; i++) wild[i] = [new XTok(XTok.Wild, WildAny)];
                return [args, wild];
            }
            var forms = new List<List<XTok>[]>();
            var idx = new int[args.Length];
            while (true)
            {
                var f = new List<XTok>[args.Length];
                for (var i = 0; i < f.Length; i++) f[i] = opts[i][idx[i]];
                forms.Add(f);
                var k = args.Length - 1;
                while (k >= 0 && ++idx[k] == opts[k].Count) idx[k--] = 0;
                if (k < 0) break;
            }
            return forms;
        }

        private void LiteralChain(int[] ops)
        {
            ResetPool();
            var slots = new List<El>[ops.Length];
            for (var i = 0; i < ops.Length; i++) slots[i] = IdEls(ops[i]);
            Product(slots);
            FinishPool();
        }

        private bool Instantiable(int q, List<XTok>[] args)
        {
            if (IsPaster(q)) return true;
            foreach (var a in args)
                foreach (var t in a)
                    if (t.K == XTok.Ident && IsPaster(t.Id)) return true;
            return false;
        }

        private Val Instantiate(int callee, List<XTok>[] args)
        {
            Val? v = null;
            // A deep instantiation (an argument's own uses reset what is active: R(x) = XCAT(R(x), _a) goes on) gives up
            // as too wide, before the stack does.
            if (depth >= DepthMax || !RuntimeHelpers.TryEnsureSufficientExecutionStack()) throw new WorkLimit();
            if (!active.Add(callee)) return new Val();
            depth++;
            try
            {
                foreach (var t in Templates(callee))
                {
                    if (t.IsFunc) { Join(ref v, Interp(t, Bind(t, args))); continue; }
                    // an object-like define called: its body, and its last name called with the arguments
                    Join(ref v, Interp(t, null));
                    if (t.Body.Length > 0 && t.Body[^1].K == XTok.Ident && Instantiable(t.Body[^1].Id, args))
                        Join(ref v, Instantiate(t.Body[^1].Id, args));
                }
            }
            finally { active.Remove(callee); depth--; }
            return v ?? new Val();
        }

        private static void UnionAll(HashSet<El> to, List<El> from)
        {
            foreach (var e in from) to.Add(e);
        }

        private static void UnionAll(HashSet<El> to, HashSet<El> from)
        {
            foreach (var e in from) to.Add(e);
        }

        private const int DepthMax = 200;
        private int depth;

        private List<XTok>[] Bind(Tpl t, List<XTok>[] args)
        {
            var pad = ReferenceEquals(args, eofArgs);
            var b = new List<XTok>[t.NParams];
            var fixedN = t.Variadic ? t.NParams - 1 : t.NParams;
            for (var i = 0; i < fixedN; i++) b[i] = i < args.Length ? args[i] : Missing(pad);
            if (t.Variadic)
            {
                if (args.Length <= fixedN) b[fixedN] = Missing(pad);
                else
                {
                    var va = new List<XTok>();
                    for (var i = fixedN; i < args.Length; i++)
                    {
                        if (i > fixedN) va.Add(new XTok(XTok.Comma));
                        va.AddRange(args[i]);
                    }
                    b[fixedN] = va;
                }
            }
            return b;
        }

        private static List<XTok> Missing(bool pad) => pad ? new List<XTok> { new(XTok.Wild, WildAny) } : new List<XTok>();

        /// <summary>Instantiates one body: builds each chain's names, instantiates each call in it, and returns the body's
        /// edges. With <paramref name="a"/> null (a name expanded, not called) parameters are unknown: chains and calls
        /// that hold one build nothing, and the walk passes them.</summary>
        private Val Interp(Tpl t, List<XTok>[]? a)
        {
            if (++work > WorkMax) throw new WorkLimit();
            if ((work & 0x3FF) == 0) Tick?.Invoke();
            var b = t.Body;
            var n = b.Length;
            var v = new Val();
            if (n == 0)
            {
                v.First.Add(El.Eps);
                v.Last.Add(El.Eps);
                v.Empty = true;
                return v;
            }

            // Scratch a body at a time, rented: pass 2 of a header carve instantiates each call again with its defines read,
            // and these arrays were most of what it allocated.
            var ints = ArrayPool<int>.Shared.Rent(4 * n);
            try
            {
                return InterpCore(t, a, b, n, v, ints);
            }
            finally { ArrayPool<int>.Shared.Return(ints); }
        }

        private Val InterpCore(Tpl t, List<XTok>[]? a, XTok[] b, int n, Val v, int[] ints)
        {
            // ## chains
            var chainEnd = ints.AsSpan(0, n);
            var chainStart = ints.AsSpan(n, n);
            chainEnd.Fill(-1);
            chainStart.Fill(-1);
            HashSet<El>?[]? chainSet = null;
            bool[]? chainEps = null;
            HashSet<El>?[]? chainFirst = null;   // a several-token argument at a chain's start: what the body starts with
            HashSet<El>?[]? chainLast = null;
            List<int>? ops = null;
            for (var k = 0; k + 2 < n;)
            {
                if (b[k + 1].K != XTok.Hash || b[k].K == XTok.Hash) { k++; continue; }
                ops ??= new List<int>();
                ops.Clear();
                var j = k;
                ops.Add(j);
                while (j + 2 < n && b[j + 1].K == XTok.Hash) { j += 2; ops.Add(j); }
                if (ops.Count == 2 && b[k].K == XTok.Comma && b[j].K == XTok.Param && t.Variadic && b[j].Id == t.NParams - 1) { k = j + 1; continue; }
                chainEnd[k] = j;
                chainStart[j] = k;
                chainSet ??= new HashSet<El>?[n];
                chainEps ??= new bool[n];
                chainFirst ??= new HashSet<El>?[n];
                chainLast ??= new HashSet<El>?[n];
                chainSet[k] = Chain(b, ops, a, out chainEps[k], out chainFirst[k], out chainLast[k]);
                k = j + 1;
            }

            // calls, nested ones included
            var open = ints.AsSpan(2 * n, n);
            open.Fill(-1);
            Stack<int>? stack = null;
            for (var k = 0; k < n; k++)
            {
                if (b[k].K == XTok.LParen) (stack ??= new Stack<int>()).Push(k);
                else if (b[k].K == XTok.RParen && stack is { Count: > 0 }) open[k] = stack.Pop();
            }
            var close = ints.AsSpan(3 * n, n);
            close.Fill(-1);
            for (var k = 0; k < n; k++) if (open[k] >= 0) close[open[k]] = k;
            Site?[]? sites = null;
            for (var k = 1; k < n; k++)
            {
                if (b[k].K != XTok.LParen || close[k] < 0) continue;
                var p = k - 1;
                var chainCallee = chainStart[p] >= 0;
                var computed = !chainCallee && b[p].K == XTok.RParen && open[p] >= 0 && sites?[open[p]] is { Inst: true };
                if (!chainCallee && !computed && b[p].K != XTok.Ident && b[p].K != XTok.Param) continue;
                var unknown = false;
                var inner = Subst(b, k + 1, close[k], a, ref unknown);
                var site = new Site { Close = close[k], Computed = computed, Args = UseTracker.Split(inner) };
                if (!unknown && !computed) site.Forms = CommaForms(inner, site.Args);
                (sites ??= new Site?[n])[k] = site;
                if (computed)
                {
                    if (!unknown) { PoolArgs(site.Args); CallResult(sites![open[p]]!.V, site.Args); site.Inst = true; }
                    continue;
                }
                if (chainCallee)
                {
                    if (unknown) continue;
                    foreach (var e in chainSet![chainStart[p]]!)
                    {
                        if (e.IsExact) Call(site, e.Pre);
                        else if (!e.IsEps) { PoolArgs(site.Args); site.Inst = true; }
                    }
                    continue;
                }
                if (b[p].K == XTok.Ident)
                {
                    if (unknown) site.Callees.Add(b[p].Id);
                    else Call(site, b[p].Id);
                    continue;
                }
                if (a is null || unknown) continue;
                var pv = a[b[p].Id];   // a parameter called: the macro it was given
                if (pv.Count == 1 && pv[0].K == XTok.Ident) Call(site, pv[0].Id);
                else if (pv.Count > 0) { PoolArgs(site.Args); site.Inst = true; }
            }

            // A call the body leaves open (OPENF(x) = CAT(x,): the rest of its arguments come after the body, unseen, so
            // they are wildcards.
            if (stack is not null && (a is not null || !t.IsFunc))
                foreach (var k in stack)
                {
                    if (k == 0) continue;
                    var p = k - 1;
                    var unknown = false;
                    var inner = Subst(b, k + 1, n, a, ref unknown);
                    if (unknown) continue;
                    var args = UseTracker.Split(inner);
                    args[^1].Add(new XTok(XTok.Wild, WildAny));   // the last argument goes on after the body
                    var callees = new List<int>();
                    if (chainStart[p] >= 0) { foreach (var e in chainSet![chainStart[p]]!) if (e.IsExact) callees.Add(e.Pre); }
                    else if (b[p].K == XTok.Ident) callees.Add(b[p].Id);
                    else if (b[p].K == XTok.Param && a is not null && a[b[p].Id] is [{ K: XTok.Ident } pv]) callees.Add(pv.Id);
                    var savedEof = eofArgs;
                    eofArgs = args;
                    try { foreach (var q in callees) if (Instantiable(q, args)) Instantiate(q, args); }
                    finally { eofArgs = savedEof; }
                }

            // the body's edges, walking in from each end up to the first token that can't be empty (the walk went on past
            // it: a use pasting onto an alias of 1,000 names built 1,000 names, each waiting for its header)
            var all = true;
            var x = 0;
            while (x < n && all)
            {
                if (chainEnd[x] >= 0)
                {
                    var e = chainEnd[x];
                    if (chainFirst![x] is { } cf) UnionAll(v.First, cf);
                    if (e + 1 < n && sites?[e + 1] is { } cs) { all &= SiteEdges(v.First, cs, false); x = cs.Close + 1; continue; }
                    var set = chainSet![x]!;
                    if (set.Count == 0) { all = false; break; }
                    foreach (var el in set) { v.First.Add(el); if (el.IsExact) AddExp(v.First, el.Pre, false); }
                    all &= chainEps![x];
                    x = e + 1;
                    continue;
                }
                var tk = b[x];
                if ((tk.K == XTok.Ident || tk.K == XTok.Param) && x + 1 < n && sites?[x + 1] is { } s)
                {
                    if (s.Callees.Count == 0 && !s.Inst) { all = false; break; }
                    all &= SiteEdges(v.First, s, false);
                    x = s.Close + 1;
                    continue;
                }
                if (!Walk(v.First, tk, a, false, ref all)) break;
                x++;
            }
            if (x >= n && all)
            {
                v.First.Add(El.Eps);
                v.Empty = true;
            }
            all = true;
            x = n - 1;
            while (x >= 0 && all)
            {
                if (chainStart[x] >= 0)
                {
                    var st = chainStart[x];
                    if (chainLast![st] is { } cl) UnionAll(v.Last, cl);
                    var set = chainSet![st]!;
                    if (set.Count == 0) { all = false; break; }
                    foreach (var el in set) { v.Last.Add(el); if (el.IsExact) AddExp(v.Last, el.Pre, true); }
                    all &= chainEps![st];
                    x = st - 1;
                    continue;
                }
                var tk = b[x];
                if (tk.K == XTok.RParen)
                {
                    var o = open[x];
                    if (o < 0 || sites?[o] is not { Computed: false } s || (s.Callees.Count == 0 && !s.Inst)) { all = false; break; }
                    all &= SiteEdges(v.Last, s, true);
                    var p = o - 1;
                    x = chainStart[p] >= 0 ? chainStart[p] - 1 : p - 1;
                    continue;
                }
                if (!Walk(v.Last, tk, a, true, ref all)) break;
                x--;
            }
            if (x < 0 && all) v.Last.Add(El.Eps);
            return v;
        }

        /// <summary>A computed call (<c>CAT(F, 7)(x)</c>): the head's result ends in the callee, so each name that can end
        /// it is instantiated with the call's arguments. The call's pool stays too (a wildcard end calls nothing known).</summary>
        private void CallResult(Val head, List<XTok>[] args)
        {
            foreach (var e in head.Last.ToArray())
                if (e.IsExact && Instantiable(e.Pre, args)) Instantiate(e.Pre, args);
        }

        private void Call(Site site, int q)
        {
            site.Callees.Add(q);
            foreach (var args in site.Forms ?? [site.Args])
            {
                if (!Instantiable(q, args)) continue;
                site.V.Union(Instantiate(q, args));
                site.Inst = true;
            }
        }

        private const int CommaFormMax = 64, CommaDepthMax = 8;

        /// <summary>A call's arguments as the preprocessor splits them once its names expand: a name whose define holds a
        /// comma (<c>PIN_LED = PORT, 5</c>) passed through a forwarder gives two arguments (<c>XCAT(PIN_LED)</c> is
        /// <c>CAT(PORT, 5)</c>). Null when no argument name expands to a comma; past <see cref="CommaFormMax"/> forms,
        /// null with the arguments pooled. Function-like names that expand to commas are not followed.</summary>
        private List<List<XTok>[]>? CommaForms(List<XTok> inner, List<XTok>[] args)
        {
            var some = false;
            var d0 = 0;
            for (var i = 0; i < inner.Count && !some; i++)
            {
                var t = inner[i];
                if (t.K == XTok.LParen) d0++;
                else if (t.K == XTok.RParen) d0--;
                else if (d0 == 0 && t.K == XTok.Ident && t.Exp && !(i + 1 < inner.Count && inner[i + 1].K == XTok.LParen))
                    some = ExpandsToComma(t.Id, 0);
            }
            if (!some) return null;
            var forms = new List<List<XTok>[]>();
            var over = false;
            var any = false;
            Forms(inner, 0, 0);
            if (over) { PoolArgs(args); return null; }
            return any ? forms : null;

            void Forms(List<XTok> toks, int from, int level)
            {
                if (over) return;
                var depth = 0;
                for (var i = 0; i < toks.Count; i++)
                {
                    var t = toks[i];
                    if (t.K == XTok.LParen) depth++;
                    else if (t.K == XTok.RParen) depth--;
                    if (i < from || depth != 0 || t.K != XTok.Ident || !t.Exp || level >= CommaDepthMax) continue;
                    if (i + 1 < toks.Count && toks[i + 1].K == XTok.LParen) continue;
                    var alts = CommaAlts(t.Id, out var plain, 0);
                    if (alts is null) continue;
                    any = true;
                    foreach (var alt in alts)
                    {
                        var next = new List<XTok>(toks.Count + alt.Count);
                        next.AddRange(toks.Take(i));
                        next.AddRange(alt);
                        next.AddRange(toks.Skip(i + 1));
                        Forms(next, i, level + 1);
                    }
                    if (plain) Forms(toks, i + 1, level);
                    return;
                }
                if (forms.Count >= CommaFormMax) { over = true; return; }
                forms.Add(UseTracker.Split(toks));
            }
        }

        private bool ExpandsToComma(int id, int level)
        {
            foreach (var t in Templates(id))
            {
                if (t.IsFunc) continue;
                var b = t.Body;
                if (TopComma(b)) return true;
                if (b.Length == 1 && b[0].K == XTok.Ident && level < CommaDepthMax && ExpandsToComma(b[0].Id, level + 1)) return true;
            }
            return false;
        }

        private static bool TopComma(XTok[] b)
        {
            var depth = 0;
            foreach (var x in b)
            {
                if (x.K == XTok.LParen) depth++;
                else if (x.K == XTok.RParen) depth--;
                else if (x.K == XTok.Comma && depth == 0) return true;
            }
            return false;
        }

        /// <summary>The bodies of a name that expand to a top-level comma, followed through aliases; null if none can.
        /// <paramref name="plain"/>: some define of it gives no comma.</summary>
        private List<List<XTok>>? CommaAlts(int id, out bool plain, int level)
        {
            plain = false;
            List<List<XTok>>? alts = null;
            var any = false;
            foreach (var t in Templates(id))
            {
                any = true;
                if (t.IsFunc) { plain = true; continue; }
                var b = t.Body;
                if (!TopComma(b) && !(b.Length == 1 && b[0].K == XTok.Ident && level < CommaDepthMax && ExpandsToComma(b[0].Id, level + 1)))
                {
                    plain = true;
                    continue;
                }
                var alt = new List<XTok>(b.Length);
                foreach (var x in b) alt.Add(x.K == XTok.Hash ? new XTok(XTok.Other) : new XTok(x.K, x.Id, true));
                (alts ??= new()).Add(alt);
            }
            if (!any) plain = true;
            return alts;
        }

        /// <summary>One token of a body's edge walk; false stops the walk.</summary>
        private bool Walk(HashSet<El> to, XTok tk, List<XTok>[]? a, bool last, ref bool all)
        {
            switch (tk.K)
            {
                case XTok.Ident:
                    AddId(to, tk.Id);
                    AddExp(to, tk.Id, last);
                    all &= Emptyable(tk.Id);
                    return true;
                case XTok.Param:
                    if (a is null) return true;
                    var s = EdgeSet(a[tk.Id], last, forceExp: true);
                    UnionAll(to, s);
                    all &= s.Contains(El.Eps);
                    return true;
                case XTok.Hash:
                    return true;
                case XTok.Num:
                    AddId(to, tk.Id);
                    all = false;
                    return false;
                default:
                    all = false;
                    return false;
            }
        }

        /// <summary>A call at a body's edge: its callee, every name and number among its arguments (each with its
        /// expansion), and what the instantiated call gives. True if it can be empty.</summary>
        private bool SiteEdges(HashSet<El> to, Site s, bool last)
        {
            var empty = s.V.Empty;
            foreach (var q in s.Callees)
            {
                AddId(to, q);
                AddExp(to, q, last);
                empty |= Emptyable(q);
            }
            foreach (var arg in s.Args)
                foreach (var t in arg)
                {
                    if (t.K == XTok.Ident) { AddId(to, t.Id); AddExp(to, t.Id, last); }
                    else if (t.K == XTok.Num) AddId(to, t.Id);
                }
            UnionAll(to, last ? s.V.Last : s.V.First);
            return empty;
        }

        /// <summary>A chain's names: a literal operand gives itself, a parameter its argument (the last token for a left
        /// slot, the first for a right one, both for an interior one); over <see cref="ProductCap"/> the use's pool takes
        /// the first and last slots. Unknown parameters: the literal operands, nothing built.</summary>
        private HashSet<El> Chain(XTok[] b, List<int> ops, List<XTok>[]? a, out bool eps, out HashSet<El>? outFirst,
            out HashSet<El>? outLast)
        {
            eps = false;
            outFirst = outLast = null;
            var n = ops.Count;
            var slots = new List<El>[n];
            var unbound = false;
            List<(int At, List<El> Ends, List<El> Starts, bool Sure)>? breaks = null;
            var sure = false;   // a split that is always there: no name runs across it
            for (var i = 0; i < n; i++)
            {
                var tk = b[ops[i]];
                if (tk.K == XTok.Ident || tk.K == XTok.Num) slots[i] = IdEls(tk.Id);
                else if (tk.K == XTok.Param)
                {
                    if (a is null) { unbound = true; slots[i] = new List<El>(); continue; }
                    var s = new HashSet<El>();
                    HashSet<El>? lastSide = null, firstSide = null;
                    if (i < n - 1) UnionAll(s, lastSide = EdgeSet(a[tk.Id], last: true, forceExp: false));
                    if (i > 0) UnionAll(s, firstSide = EdgeSet(a[tk.Id], last: false, forceExp: false));
                    slots[i] = s.ToList();
                    // An interior argument of several tokens splits the chain: its first token ends the name on its
                    // left, its last starts the one on its right (HWB_##blk##_r with blk = T x: HWB_T and x_r).
                    if (lastSide is not null && firstSide is not null && MaybeTokens(a[tk.Id]))
                    {
                        var always = SeveralTokens(a[tk.Id]);
                        sure |= always;
                        (breaks ??= new()).Add((i, firstSide.ToList(), lastSide.ToList(), always));
                    }
                    // At the chain's start or end, its far token is the body's edge there (x##_r with x = A B: A B_r).
                    else if (i == 0 && n > 1 && MaybeTokens(a[tk.Id])) outFirst = EdgeSet(a[tk.Id], last: false, forceExp: false);
                    else if (i == n - 1 && n > 1 && MaybeTokens(a[tk.Id])) outLast = EdgeSet(a[tk.Id], last: true, forceExp: false);
                }
                else return new HashSet<El>();   // a non-name operand builds nothing
            }
            if (unbound)
            {
                var lit = new HashSet<El>();
                foreach (var s in slots) UnionAll(lit, s);
                return lit;
            }
            // (ZSX_##b##_QZ with b = KA KB: ZSX_KA and KB_QZ, never ZSX_KA_QZ)
            var made = sure ? new HashSet<El>() : Product(slots);
            if (made is null)
            {
                made = new HashSet<El>(slots[0]);
                UnionAll(made, slots[^1]);
            }
            else eps = made.Contains(El.Eps);
            if (breaks is not null) Segments(slots, breaks, made);
            return made;
        }

        /// <summary>The names a chain builds between its splits: from its start or a split's last token, over the slots
        /// between, to its end or a later split's first token.</summary>
        private void Segments(List<El>[] slots, List<(int At, List<El> Ends, List<El> Starts, bool Sure)> breaks, HashSet<El> made)
        {
            var n = slots.Length;
            var starts = new List<(int At, List<El> Set)> { (0, slots[0]) };
            var ends = new List<(int At, List<El> Set)> { (n - 1, slots[n - 1]) };
            foreach (var br in breaks)
            {
                starts.Add((br.At, br.Starts));
                ends.Add((br.At, br.Ends));
            }
            foreach (var st in starts)
                foreach (var en in ends)
                {
                    if (en.At <= st.At || (st.At == 0 && en.At == n - 1)) continue;
                    if (breaks.Exists(br => br.Sure && br.At > st.At && br.At < en.At)) continue;
                    var seg = new List<El>[en.At - st.At + 1];
                    seg[0] = st.Set;
                    for (var k = st.At + 1; k < en.At; k++) seg[k - st.At] = slots[k];
                    seg[^1] = en.Set;
                    var part = Product(seg);
                    if (part is null) { UnionAll(made, seg[0]); UnionAll(made, seg[^1]); }
                    else UnionAll(made, part);
                }
        }

        /// <summary>Whether an argument is always more than one token: two of its tokens stand for themselves alone
        /// (one that stands for its expansion too can expand to nothing).</summary>
        private static bool SeveralTokens(List<XTok> arg)
        {
            var n = 0;
            foreach (var t in arg) if (!t.Exp && t.K != XTok.Wild && ++n == 2) return true;
            return false;
        }

        /// <summary>Whether an argument can be more than one token: it is, or it is a name whose expansion can be.</summary>
        private bool MaybeTokens(List<XTok> arg)
        {
            if (arg.Count > 1) return true;
            if (arg.Count == 0 || arg[0].K != XTok.Ident || !arg[0].Exp) return false;
            var seen = new HashSet<int>();
            var id = arg[0].Id;
            while (seen.Add(id))
            {
                var next = -1;
                foreach (var t in Templates(id))
                {
                    if (t.IsFunc) continue;
                    if (t.Body.Length > 1) return true;
                    if (t.Body.Length == 1 && t.Body[0].K == XTok.Ident) next = t.Body[0].Id;
                }
                if (next < 0) return false;
                id = next;
            }
            return false;
        }

        /// <summary>The cross product of <paramref name="slots"/>, each built; null (and the slots pooled) past the cap.</summary>
        private HashSet<El>? Product(List<El>[] slots)
        {
            long total = 1;
            foreach (var s in slots)
            {
                total *= s.Count;
                if (total > ProductCap) break;
            }
            if (total > ProductCap)
            {
                PoolStart(slots[0]);
                PoolEnd(slots[^1]);
                return null;
            }
            var made = new HashSet<El>();
            if (total == 0) return made;
            var idx = new int[slots.Length];
            var pre = new System.Text.StringBuilder();
            var suf = new System.Text.StringBuilder();
            while (true)
            {
                pre.Clear();
                suf.Clear();
                byte wild = 0;
                for (var i = 0; i < slots.Length; i++)
                {
                    var e = slots[i][idx[i]];
                    if (e.IsEps) continue;
                    if (e.Wild == 0) { AppendName(wild == 0 ? pre : suf, e.Pre); continue; }
                    if (wild == 0) { AppendName(pre, e.Pre); wild = e.Wild; }
                    else wild = WildAny;   // more than one wildcard: the outer prefix*suffix
                    suf.Clear();
                    AppendName(suf, e.Suf);
                }
                var el = wild == 0
                    ? (pre.Length == 0 ? El.Eps : new El(Id(pre.ToString()), 0, -1))
                    : new El(pre.Length == 0 ? -1 : Id(pre.ToString()), wild, suf.Length == 0 ? -1 : Id(suf.ToString()));
                if (made.Add(el)) Build(el);
                var k = slots.Length - 1;
                while (k >= 0 && ++idx[k] == slots[k].Count) idx[k--] = 0;
                if (k < 0) break;
            }
            return made;
        }

        private void AppendName(System.Text.StringBuilder to, int id)
        {
            if (id >= 0) to.Append(names[id]);
        }

        /// <summary>What an argument gives at one end: the token there, and for a token that stands for its expansion that
        /// expansion's edge too, the walk going on past names and calls; the empty string when every token can expand to
        /// nothing.</summary>
        private HashSet<El> EdgeSet(List<XTok> toks, bool last, bool forceExp)
        {
            var s = new HashSet<El>();
            var n = toks.Count;
            if (n == 0) { s.Add(El.Eps); return s; }
            var saved = active;
            active = new HashSet<int>();   // an argument is expanded on its own, before it is substituted
            try
            {
                var all = true;
                var k = last ? n - 1 : 0;
                while ((last ? k >= 0 : k < n) && all)
                {
                    var x = toks[k];
                    int callee = -1, o = -1, c = -1;
                    if (!last && x.K == XTok.Ident && k + 1 < n && toks[k + 1].K == XTok.LParen)
                    {
                        c = MatchClose(toks, k + 1);
                        if (c >= 0) { callee = k; o = k + 1; }
                    }
                    else if (last && x.K == XTok.RParen)
                    {
                        o = MatchOpen(toks, k);
                        if (o > 0 && toks[o - 1].K == XTok.Ident) { callee = o - 1; c = k; }
                    }
                    if (callee >= 0)
                    {
                        var ct = toks[callee];
                        if (!(forceExp || ct.Exp))
                        {
                            if (!last) AddId(s, ct.Id);
                            all = false;
                            break;
                        }
                        AddId(s, ct.Id);
                        AddExp(s, ct.Id, last);
                        var inner = new List<XTok>(c - o - 1);
                        for (var i = o + 1; i < c; i++) { var t = toks[i]; if (forceExp) t.Exp = true; inner.Add(t); }
                        var args = UseTracker.Split(inner);
                        foreach (var t in inner)
                        {
                            if (t.K == XTok.Ident) { AddId(s, t.Id); AddExp(s, t.Id, last); }
                            else if (t.K == XTok.Num) AddId(s, t.Id);
                        }
                        var empty = Emptyable(ct.Id);
                        foreach (var form in CommaForms(inner, args) ?? [args])
                        {
                            if (!Instantiable(ct.Id, form)) continue;
                            var cv = Instantiate(ct.Id, form);
                            UnionAll(s, last ? cv.Last : cv.First);
                            empty |= cv.Empty;
                        }
                        all &= empty;
                        k = last ? callee - 1 : c + 1;
                        continue;
                    }
                    if (x.K == XTok.Ident)
                    {
                        AddId(s, x.Id);
                        if (!(forceExp || x.Exp)) { all = false; break; }
                        AddExp(s, x.Id, last);
                        all &= Emptyable(x.Id);
                        k += last ? -1 : 1;
                        continue;
                    }
                    if (x.K == XTok.Num) AddId(s, x.Id);
                    else if (x.K == XTok.Wild) s.Add(new El(-1, (byte)x.Id, -1));
                    all = false;
                    break;
                }
                if (all && (last ? k < 0 : k >= n)) s.Add(El.Eps);
            }
            finally { active = saved; }
            return s;
        }

        private static int MatchClose(List<XTok> t, int open)
        {
            var depth = 0;
            for (var i = open; i < t.Count; i++)
            {
                if (t[i].K == XTok.LParen) depth++;
                else if (t[i].K == XTok.RParen && --depth == 0) return i;
            }
            return -1;
        }

        private static int MatchOpen(List<XTok> t, int close)
        {
            var depth = 0;
            for (var i = close; i >= 0; i--)
            {
                if (t[i].K == XTok.RParen) depth++;
                else if (t[i].K == XTok.LParen && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>A body range with its parameters replaced by their arguments: one used outside <c>##</c> stands for its
        /// expansion too. With no arguments a parameter is dropped and <paramref name="unknown"/> set.</summary>
        private static List<XTok> Subst(XTok[] b, int from, int to, List<XTok>[]? a, ref bool unknown)
        {
            var r = new List<XTok>();
            for (var k = from; k < to; k++)
            {
                var x = b[k];
                if (x.K == XTok.Param)
                {
                    if (a is null) { unknown = true; continue; }
                    var raw = (k > 0 && b[k - 1].K == XTok.Hash) || (k + 1 < b.Length && b[k + 1].K == XTok.Hash);
                    foreach (var t in a[x.Id]) r.Add(new XTok(t.K, t.Id, raw ? t.Exp : true));
                }
                else if (x.K == XTok.Hash) r.Add(new XTok(XTok.Other));
                else r.Add(new XTok(x.K, x.Id));
            }
            return r;
        }

        private List<El> IdEls(int id)
        {
            var l = new List<El> { new(id, 0, -1) };
            var w = WildOf(id);
            if (w != 0) l.Add(new El(-1, w, -1));
            return l;
        }

        private void AddId(HashSet<El> to, int id)
        {
            to.Add(new El(id, 0, -1));
            var w = WildOf(id);
            if (w != 0) to.Add(new El(-1, w, -1));
        }

        /// <summary><c>__LINE__</c> and <c>__COUNTER__</c> stand for digits too, an undefined <c>__x</c> for anything.</summary>
        private byte WildOf(int id)
        {
            var s = names[id];
            if (!s.StartsWith("__", StringComparison.Ordinal)) return 0;
            if (s is "__LINE__" or "__COUNTER__") return WildDigits;
            return Has(id, BitUnder) ? (byte)0 : WildAny;
        }

        private void AddExp(HashSet<El> to, int id, bool last)
        {
            var e = Expand(id);
            UnionAll(to, last ? e.Last : e.First);
        }

        private readonly Dictionary<int, Val> expMemo = new();
        private readonly Dictionary<int, int> expAt = new();   // names being expanded: their depth
        private int expLow = int.MaxValue;
        // Expansions that met a define not read yet, until a big-header define arrives: a chain of 40 forwarders whose end
        // was unread expanded each link twice (its first and last edges), 2^40 times. They were dropped after each use,
        // and every use expanded its names' whole alias tree again: work's pass 1 sat 12 minutes at 241K uses. Each keeps
        // the names it met unread, directly and through the partial expansions inside it: a use that takes one waits on
        // them too.
        private readonly Dictionary<int, Val> expPartial = new();
        private readonly Dictionary<int, MissRec> partialMiss = new();
        private MissRec? missTop;                                   // the expansion running: the names it meets unread
        private readonly List<int> partialsTaken = new();           // the partial expansions the running use took

        private sealed class MissRec
        {
            public readonly HashSet<int> Own = new(), Kids = new();
        }

        private void ClearPartial()
        {
            if (misses is not null) PartialMisses();
            expPartial.Clear();
            partialMiss.Clear();
        }

        /// <summary>A partial expansion is taken: what the use and the expansion around it wait on.</summary>
        private void TakePartial(int id)
        {
            missTop?.Kids.Add(id);
            if (misses is not null) partialsTaken.Add(id);
        }

        /// <summary>The running use waits on every name the partial expansions it took met unread.</summary>
        private void PartialMisses()
        {
            if (partialsTaken.Count == 0) return;
            var seen = new HashSet<int>();
            var todo = new Stack<int>(partialsTaken);
            partialsTaken.Clear();
            while (todo.Count > 0)
            {
                var k0 = todo.Pop();
                if (!seen.Add(k0) || !partialMiss.TryGetValue(k0, out var rec)) continue;
                misses!.UnionWith(rec.Own);
                foreach (var k in rec.Kids) if (!seen.Contains(k)) todo.Push(k);
            }
        }
        // The names of a define cycle (A = B x in one #if branch, B = A y in the other) while its first name is expanded:
        // each one's value so far, the stack depth its cycle reaches, and whether this round has computed it. Without
        // them no name in a cycle was kept, and 16 names in one cycle took a minute: each was expanded once per path.
        private readonly Dictionary<int, (Val V, int Low, bool Done)> expCyc = new();
        private readonly List<int> expCycOrder = new();
        private bool expGrew;

        /// <summary>A name's expansion edges (not the name itself): every body of it, with parameters unknown,
        /// transitively. Kept once complete: every define it met was read. A cycle is solved at its first name, whose
        /// rounds compute each name in it once until none grows; a name met again inside its own expansion gives its
        /// value so far (the least fixpoint: every path through the cycle, a superset of what the preprocessor does).</summary>
        private Val Expand(int id)
        {
            if (expMemo.TryGetValue(id, out var m)) return m;
            if (expPartial.TryGetValue(id, out m)) { missCount++; TakePartial(id); return m; }
            var inCyc = expCyc.TryGetValue(id, out var cyc);
            if (expAt.TryGetValue(id, out var at)) { expLow = Math.Min(expLow, at); return inCyc ? cyc.V : new Val(); }
            if (inCyc && cyc.Done) { expLow = Math.Min(expLow, cyc.Low); return cyc.V; }
            var depth = expAt.Count;
            expAt[id] = depth;
            var savedLow = expLow;
            var savedActive = active;
            var savedGrew = expGrew;
            var missesAt = missCount;
            var mark = expCycOrder.Count;
            var up = missTop;
            var rec = missTop = new MissRec();
            noPool++;
            try
            {
                while (true)
                {
                    expLow = int.MaxValue;
                    expGrew = false;
                    active = new HashSet<int>();
                    Val? jv = null;
                    foreach (var t in Templates(id)) Join(ref jv, Interp(t, null));
                    var v = jv ?? new Val();
                    if (expLow < depth)
                    {
                        // inside a cycle whose first name is further up the stack: that one's rounds finish it
                        var grew = CycNote(id, v, expLow);
                        expGrew = savedGrew || grew;
                        expLow = Math.Min(savedLow, expLow);
                        missTop = up;
                        if (up is not null) { up.Own.UnionWith(rec.Own); up.Kids.UnionWith(rec.Kids); }
                        return v;
                    }
                    if (expLow > depth && expCycOrder.Count == mark)
                    {
                        missTop = up;
                        if (missCount == missesAt) expMemo[id] = v;
                        else { expPartial[id] = v; partialMiss[id] = rec; TakePartial(id); }
                        expGrew = savedGrew;
                        expLow = savedLow;
                        return v;
                    }
                    var rootGrew = CycNote(id, v, depth);
                    if (expGrew || rootGrew)
                    {
                        // another round: each name of the cycle again, from the values so far
                        for (var i = mark; i < expCycOrder.Count; i++)
                        {
                            var x = expCycOrder[i];
                            expCyc[x] = expCyc[x] with { Done = false };
                        }
                        continue;
                    }
                    missTop = up;
                    var part = missCount != missesAt;
                    var to = part ? expPartial : expMemo;
                    for (var i = mark; i < expCycOrder.Count; i++)
                    {
                        var x = expCycOrder[i];
                        to[x] = expCyc[x].V;
                        if (part) partialMiss[x] = rec;
                        expCyc.Remove(x);
                    }
                    expCycOrder.RemoveRange(mark, expCycOrder.Count - mark);
                    expGrew = savedGrew;
                    expLow = savedLow;
                    if (part) TakePartial(id);
                    return to[id];
                }
            }
            catch
            {
                missTop = up;
                for (var i = mark; i < expCycOrder.Count; i++) expCyc.Remove(expCycOrder[i]);
                expCycOrder.RemoveRange(mark, expCycOrder.Count - mark);
                throw;
            }
            finally
            {
                noPool--;
                active = savedActive;
                expAt.Remove(id);
            }
        }

        /// <summary>Records a cycle name's value this round (never smaller than its last); true if it grew.</summary>
        private bool CycNote(int id, Val v, int low)
        {
            if (expCyc.TryGetValue(id, out var o))
            {
                var (f, l, e) = (o.V.First.Count, o.V.Last.Count, o.V.Empty);
                v.Union(o.V);
                expCyc[id] = (v, Math.Min(low, o.Low), true);
                return v.First.Count > f || v.Last.Count > l || v.Empty != e;
            }
            expCyc[id] = (v, low, true);
            expCycOrder.Add(id);
            return v.First.Count > 0 || v.Last.Count > 0 || v.Empty;
        }

        // ---- building ----

        private void Build(El e)
        {
            if (e.IsEps || noPool > 0) return;   // an expansion's own builds are its body stream's
            if (e.Wild == 0) { Results.AddExact(keys[e.Pre]); return; }
            Results.AddPattern(e.Pre >= 0 ? names[e.Pre] : "", e.Wild, e.Suf >= 0 ? names[e.Suf] : "");
        }

        // ---- a use's own pool ----

        private void ResetPool()
        {
            poolS.Clear();
            poolE.Clear();
            poolOn = poolAnyS = poolAnyE = poolDigitsE = false;
        }

        private void PoolStart(IEnumerable<El> els)
        {
            if (noPool > 0) return;
            poolOn = true;
            foreach (var e in els)
            {
                if (e.IsEps) continue;
                if (e.Pre >= 0) poolS.Add(e.Pre);
                else poolAnyS = true;
            }
        }

        private void PoolEnd(IEnumerable<El> els)
        {
            if (noPool > 0) return;
            poolOn = true;
            foreach (var e in els)
            {
                if (e.IsEps) continue;
                if (e.Wild == 0) poolE.Add(e.Pre);
                else if (e.Suf >= 0) poolE.Add(e.Suf);
                else if (e.Wild == WildDigits) poolDigitsE = true;
                else poolAnyE = true;
            }
        }

        /// <summary>A computed call or one open across a directive: every name and number among its arguments starts and
        /// ends names, with its expansion's last and first edges.</summary>
        private void PoolArgs(List<XTok>[] args)
        {
            if (noPool > 0) return;
            var s = new HashSet<El>();
            var e = new HashSet<El>();
            foreach (var arg in args)
                foreach (var t in arg)
                {
                    if (t.K == XTok.Wild) { s.Add(new El(-1, (byte)t.Id, -1)); e.Add(new El(-1, (byte)t.Id, -1)); continue; }
                    if (t.K != XTok.Ident && t.K != XTok.Num) continue;
                    s.Add(new El(t.Id, 0, -1));
                    AddId(e, t.Id);
                    if (t.K != XTok.Ident) continue;
                    AddExp(s, t.Id, true);
                    AddExp(e, t.Id, false);
                }
            s.RemoveWhere(x => x.Wild == WildDigits && x.Pre < 0 && x.Suf < 0);   // digits end names only
            PoolStart(s);
            PoolEnd(e);
        }

        /// <summary>The pool's names: one starting with a start piece and ending with an end piece, never overlapping.</summary>
        private void FinishPool()
        {
            if (!poolOn) return;
            poolOn = false;
            var anyEnd = poolAnyE || poolDigitsE || poolE.Count > 0;
            if ((!poolAnyS && poolS.Count == 0) || !anyEnd) return;
            Results.AddPool(poolS.Select(i => names[i]).ToHashSet(StringComparer.Ordinal), poolAnyS,
                            poolE.Select(i => names[i]).ToHashSet(StringComparer.Ordinal), poolAnyE, poolDigitsE);
        }
    }
}
