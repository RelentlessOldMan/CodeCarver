namespace CodeCarver.Core.Preprocess;

/// <summary>How a preprocessor condition evaluated: definitively live, definitively dead, or unknown.</summary>
public enum Tri { False, True, Unknown }

/// <summary>
/// Resolves which source lines are DEAD under a given macro configuration — the code inside
/// <c>#if</c>/<c>#ifdef</c> branches the config does not take. The front-end skips definitions and calls
/// on dead lines, so the carve reflects the build's real configuration instead of over-keeping every
/// branch.
///
/// It is a conditional-only scanner (no macro body expansion, no #include inlining — a full
/// preprocessor isn't needed to know which branch is live) and it is CONSERVATIVE by default. The
/// crucial soundness point is the world assumption:
///
///  • <b>Open world (default)</b>: a macro that is neither a supplied define nor defined earlier in the
///    file is treated as UNKNOWN — it might be defined in a header we didn't process — so its branch is
///    KEPT. Resolution only ever tightens where it is certain (a supplied define is present, a definite
///    <c>#if 0</c>, arithmetic over known macros, or the <c>#else</c>/later branches after a definitely
///    taken branch). It never drops live code.
///
///  • <b>Closed world (opt-in)</b>: the supplied defines are trusted as the COMPLETE macro set, so an
///    absent macro is treated as undefined and its branch dropped. Powerful, but only correct when the
///    define set really is complete (e.g. taken from a preprocessed build) — otherwise it can drop code
///    a header would have enabled.
///
/// No compiler required either way.
/// </summary>
public static class PreprocessorScanner
{
    // Certain: this branch is live in EVERY configuration that reaches its parent (no earlier branch of the
    // chain was unknown, and the parent itself is certain). A #define in a live but uncertain branch only
    // POSSIBLY happens, so it makes the name unknown rather than defined (review PP3).
    private readonly record struct Frame(bool ParentActive, bool ParentCertain, bool Taken, bool BranchActive,
                                         bool SawUnknown, bool Certain)
    {
        public bool Active => ParentActive && BranchActive;
    }

    private static readonly System.Text.RegularExpressions.Regex ConstantIf = new(
        @"^[ \t]*#[ \t]*(?:el)?if[ \t(]*[01]\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The text with every line that is dead in EVERY configuration (<c>#if 0</c>, the <c>#else</c> of
    /// <c>#if 1</c>) blanked to spaces, line breaks kept. Such a block is often not C at all (unbalanced braces, notes),
    /// and a parser or tokenizer that reads it loses the real code after it. Directive lines stay.</summary>
    public static string BlankAlwaysDead(string text)
    {
        text = SourceText.Trigraphs(text);
        if (!text.Contains("if", StringComparison.Ordinal) || !ConstantIf.IsMatch(text)) return text;
        var (dead, _, balanced) = LineMaps(text, new MacroTable(), closedWorld: false);
        if (!balanced) return text;   // an #if that never closes (inside a raw string, a cut-off file): blank nothing
        var lines = text.Split('\n');
        var any = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (i + 1 >= dead.Length || !dead[i + 1] || lines[i].TrimStart().StartsWith('#')) continue;
            var l = lines[i];
            var cr = l.EndsWith('\r');
            lines[i] = new string(' ', cr ? l.Length - 1 : l.Length) + (cr ? "\r" : "");
            any = true;
        }
        return any ? string.Join('\n', lines) : text;
    }

    /// <summary>Returns a 1-based map (index 0 unused) where true = the line is in a dead branch.</summary>
    public static bool[] DeadLineMap(string text, MacroTable defines, bool closedWorld = false)
        => LineMaps(text, defines, closedWorld).Dead;

    /// <summary>A 1-based map where true = the line is live but only in SOME configurations: it sits in a branch whose
    /// condition (or an enclosing one) the model can't decide.</summary>
    public static bool[] UncertainLineMap(string text, MacroTable defines, bool closedWorld = false)
        => LineMaps(text, defines, closedWorld).Uncertain;

    /// <summary>Balanced: every #if closed by the end of the text.</summary>
    static (bool[] Dead, bool[] Uncertain, bool Balanced) LineMaps(string text, MacroTable defines, bool closedWorld)
    {
        text = SourceText.Trigraphs(text);   // ??=if is #if
        var lines = text.Split('\n');
        var dead = new bool[lines.Length + 1];
        var uncertain = new bool[lines.Length + 1];
        var table = defines.Clone();
        var stack = new Stack<Frame>();
        var guardLine = IncludeGuardLine(lines);
        var inComment = false;
        string? rawEnd = null;   // inside a C++ raw string R"d( ... )d": its lines are text, not directives

        for (var idx = 0; idx < lines.Length; idx++)
        {
            var active = stack.Count == 0 || stack.Peek().Active;
            var trimmed = lines[idx].TrimStart();
            var startsInComment = inComment;
            var startsInRaw = rawEnd is not null;
            if (rawEnd is not null) { if (lines[idx].Contains(rawEnd, StringComparison.Ordinal)) rawEnd = null; }
            else
            {
                inComment = EndsInComment(lines[idx], inComment);
                if (!inComment && lines[idx].Contains("R\"", StringComparison.Ordinal)) rawEnd = OpenRawString(lines[idx]);
            }

            if (!startsInComment && !startsInRaw && trimmed.Length > 0 && trimmed[0] == '#')
            {
                var last = idx;
                var joined = lines[idx].TrimEnd('\r');
                while (joined.TrimEnd().EndsWith('\\') && last + 1 < lines.Length)
                {
                    joined = joined.TrimEnd();
                    joined = joined[..^1] + " " + lines[++last].TrimEnd('\r');
                }
                for (var k = idx; k <= last; k++) dead[k + 1] = !active;
                for (var k = idx + 1; k <= last; k++) inComment = EndsInComment(lines[k], inComment);
                var body = StripComments(joined.TrimStart().TrimStart('#'));
                if (idx == guardLine) Push(stack, active, true, Tri.True);   // include guard: true on first inclusion
                else HandleDirective(body, active, table, stack, closedWorld);
                idx = last;
                continue;
            }

            dead[idx + 1] = !active;
            uncertain[idx + 1] = active && stack.Count > 0 && !stack.Peek().Certain;
        }

        return (dead, uncertain, stack.Count == 0 && rawEnd is null);
    }

    private static readonly System.Text.RegularExpressions.Regex RawStart = new(
        @"(?<![A-Za-z0-9_])(?:u8|u|U|L)?R""([^()\\\s]{0,16})\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The closing <c>)d"</c> of a C++ raw string <paramref name="line"/> opens and doesn't close, or null.</summary>
    private static string? OpenRawString(string line)
    {
        foreach (System.Text.RegularExpressions.Match m in RawStart.Matches(line))
        {
            var end = ")" + m.Groups[1].Value + "\"";
            if (line.IndexOf(end, m.Index + m.Length, StringComparison.Ordinal) < 0) return end;
        }
        return null;
    }

    private static void HandleDirective(string body, bool active, MacroTable table, Stack<Frame> stack, bool closedWorld)
    {
        var (keyword, rest) = SplitKeyword(body);
        var certain = stack.Count == 0 || stack.Peek().Certain;
        switch (keyword)
        {
            case "ifdef":
                // Known-defined => True; explicitly-unknown (e.g. varies per TU) => Unknown even under closed
                // world; otherwise absent => False only under closed world, else Unknown (a header may define it).
                Push(stack, active, certain, DefinedTri(table, FirstToken(rest), closedWorld, whenDefined: Tri.True, whenAbsent: Tri.False));
                break;
            case "ifndef":
                Push(stack, active, certain, DefinedTri(table, FirstToken(rest), closedWorld, whenDefined: Tri.False, whenAbsent: Tri.True));
                break;
            case "if":
                Push(stack, active, certain, EvaluateCondition(rest, table, closedWorld));
                break;
            case "elif":
                Elif(stack, EvaluateCondition(rest, table, closedWorld));
                break;
            case "else":
                Else(stack);
                break;
            case "endif":
                if (stack.Count > 0) stack.Pop();
                break;
            case "define" when active:
            {
                var name = FirstToken(rest);
                var after = rest[name.Length..];
                if (after.StartsWith('(')) { var close = after.IndexOf(')'); after = close < 0 ? "" : after[(close + 1)..]; }
                if (certain) table.Set(name, after.Trim());
                else table.ForceUnknown(name);
                break;
            }
            case "undef" when active:
                if (certain) table.UndefCertain(FirstToken(rest));
                else table.ForceUnknown(FirstToken(rest));
                break;
        }
    }

    /// <summary>Tri-state for an ifdef/ifndef on <paramref name="name"/>: defined → <paramref name="whenDefined"/>;
    /// explicitly unknown → Unknown (kept, even closed-world); absent → <paramref name="whenAbsent"/> only under
    /// closed-world, else Unknown.</summary>
    private static Tri DefinedTri(MacroTable table, string name, bool closedWorld, Tri whenDefined, Tri whenAbsent)
    {
        if (table.IsDefined(name)) return whenDefined;
        if (table.IsUnknown(name)) return Tri.Unknown;
        return closedWorld ? whenAbsent : Tri.Unknown;
    }

    private static void Push(Stack<Frame> stack, bool parentActive, bool parentCertain, Tri cond)
    {
        bool branchActive, taken, sawUnknown = false;
        if (!parentActive) { branchActive = false; taken = true; }
        else
            switch (cond)
            {
                case Tri.True: branchActive = true; taken = true; break;
                case Tri.False: branchActive = false; taken = false; break;
                default: branchActive = true; taken = false; sawUnknown = true; break; // Unknown: keep, allow other branches too
            }
        stack.Push(new Frame(parentActive, parentCertain, taken, branchActive, sawUnknown,
                             parentCertain && branchActive && !sawUnknown));
    }

    private static void Elif(Stack<Frame> stack, Tri cond)
    {
        if (stack.Count == 0) return;
        var top = stack.Pop();
        bool branchActive, taken = top.Taken, sawUnknown = top.SawUnknown;
        if (!top.ParentActive || top.Taken) branchActive = false;
        else
            switch (cond)
            {
                case Tri.True: branchActive = true; taken = true; break;
                case Tri.False: branchActive = false; break;
                default: branchActive = true; sawUnknown = true; break;
            }
        stack.Push(new Frame(top.ParentActive, top.ParentCertain, taken, branchActive, sawUnknown,
                             top.ParentCertain && branchActive && !sawUnknown));
    }

    private static void Else(Stack<Frame> stack)
    {
        if (stack.Count == 0) return;
        var top = stack.Pop();
        var branchActive = top.ParentActive && !top.Taken;
        stack.Push(new Frame(top.ParentActive, top.ParentCertain, true, branchActive, top.SawUnknown,
                             top.ParentCertain && branchActive && !top.SawUnknown));
    }

    /// <summary>0-based index of an include-guard <c>#ifndef X</c> (or <c>#if !defined(X)</c>) that is the
    /// file's first directive and is followed directly by <c>#define X</c>; -1 if none. On first inclusion the
    /// guard is certainly true — without this, a guard macro #defined in the tree would read as unknown and make
    /// every #define in the header uncertain.</summary>
    private static int IncludeGuardLine(string[] lines)
    {
        int first = -1, second = -1;
        var inComment = false;
        for (var i = 0; i < lines.Length && second < 0; i++)
        {
            var starts = inComment;
            inComment = EndsInComment(lines[i], inComment);
            var t = lines[i].TrimStart();
            if (starts || t.Length == 0 || t[0] != '#') continue;
            if (first < 0) first = i; else second = i;
        }
        if (first < 0 || second < 0) return -1;
        var (k1, r1) = SplitKeyword(StripComments(lines[first].TrimStart().TrimStart('#')));
        var (k2, r2) = SplitKeyword(StripComments(lines[second].TrimStart().TrimStart('#')));
        if (k2 != "define") return -1;
        string? guard = null;
        if (k1 == "ifndef") guard = FirstToken(r1);
        else if (k1 == "if")
        {
            var m = System.Text.RegularExpressions.Regex.Match(r1, @"^!\s*defined\s*\(?\s*(\w+)\s*\)?$");
            if (m.Success) guard = m.Groups[1].Value;
        }
        if (guard is not { Length: > 0 } || FirstToken(r2) != guard) return -1;
        // A guard spans the file: no #else/#elif of its own, and its #endif is the last directive. `#ifndef X /
        // #define X 0 / #else ...` is a default, and its #else branch is live whenever the build defines X.
        var depth = 0;
        var closedAt = -1;
        inComment = false;
        for (var i = first; i < lines.Length; i++)
        {
            var starts = inComment;
            inComment = EndsInComment(lines[i], inComment);
            var t = lines[i].TrimStart();
            if (starts || t.Length == 0 || t[0] != '#') continue;
            if (closedAt >= 0) return -1;   // a directive after the guard closed
            var (k, _) = SplitKeyword(StripComments(t.TrimStart('#')));
            if (k is "if" or "ifdef" or "ifndef") depth++;
            else if (k is "else" or "elif" or "elifdef" or "elifndef" && depth == 1) return -1;
            else if (k == "endif" && --depth == 0) closedAt = i;
        }
        return closedAt >= 0 ? first : -1;
    }

    /// <summary>Is a block comment open at the end of this line? (Strings are skipped so "/*" in a literal
    /// does not count.)</summary>
    private static bool EndsInComment(string line, bool inComment)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inComment) { if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { inComment = false; i++; } continue; }
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return false;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inComment = true; i++; }
        }
        return inComment;
    }

    /// <summary>Remove /* */ and // comments from a joined directive line.</summary>
    private static string StripComments(string s)
    {
        if (!s.Contains('/')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        var quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { sb.Append(c); if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]); else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') break;
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                sb.Append(' ');
                if (end < 0) break;
                i = end + 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- #if expression evaluation ------------------------------------------------------------

    public static Tri EvaluateCondition(string expr, MacroTable table, bool closedWorld = false)
    {
        try
        {
            var value = new ExprParser(Tokenize(expr), table, closedWorld).ParseFull();
            return value is null ? Tri.Unknown : (value != 0 ? Tri.True : Tri.False);
        }
        catch
        {
            return Tri.Unknown;
        }
    }

    private sealed class ExprParser
    {
        private readonly List<string> _t;
        private readonly MacroTable _table;
        private readonly bool _closedWorld;
        private readonly int _depth;
        private int _i;

        public ExprParser(List<string> tokens, MacroTable table, bool closedWorld, int depth = 0)
        {
            _t = tokens;
            _table = table;
            _closedWorld = closedWorld;
            _depth = depth;
        }

        public long? ParseFull()
        {
            var v = ParseOr();
            return _i == _t.Count ? v : throw new FormatException("trailing tokens");
        }

        private string? Peek => _i < _t.Count ? _t[_i] : null;
        private string Next() => _t[_i++];
        private bool Eat(string s) { if (Peek == s) { _i++; return true; } return false; }

        private long? ParseOr()
        {
            var l = ParseAnd();
            while (Eat("||")) l = OrOp(l, ParseAnd());
            return l;
        }

        private long? ParseAnd()
        {
            var l = ParseEq();
            while (Eat("&&")) l = AndOp(l, ParseEq());
            return l;
        }

        // Short-circuit-aware logic so a known-true `||` or known-false `&&` resolves even with an unknown side.
        private static long? OrOp(long? a, long? b)
        {
            if ((a is { } av && av != 0) || (b is { } bv && bv != 0)) return 1;
            return (a is null || b is null) ? null : 0;
        }

        private static long? AndOp(long? a, long? b)
        {
            if ((a is { } av && av == 0) || (b is { } bv && bv == 0)) return 0;
            return (a is null || b is null) ? null : 1;
        }

        private long? ParseEq()
        {
            var l = ParseRel();
            while (Peek is "==" or "!=")
            {
                var op = Next(); var r = ParseRel();
                l = (l is null || r is null) ? null : (op == "==" ? (l == r ? 1 : 0) : (l != r ? 1 : 0));
            }
            return l;
        }

        private long? ParseRel()
        {
            var l = ParseAdd();
            while (Peek is "<" or ">" or "<=" or ">=")
            {
                var op = Next(); var r = ParseAdd();
                if (l is null || r is null) { l = null; continue; }
                l = op switch { "<" => l < r ? 1 : 0, ">" => l > r ? 1 : 0, "<=" => l <= r ? 1 : 0, _ => l >= r ? 1 : 0 };
            }
            return l;
        }

        private long? ParseAdd()
        {
            var l = ParseMul();
            while (Peek is "+" or "-")
            {
                var op = Next(); var r = ParseMul();
                l = (l is null || r is null) ? null : (op == "+" ? l + r : l - r);
            }
            return l;
        }

        private long? ParseMul()
        {
            var l = ParseUnary();
            while (Peek is "*" or "/" or "%")
            {
                var op = Next(); var r = ParseUnary();
                if (l is null || r is null || (op != "*" && r == 0)) { l = null; continue; }
                l = op switch { "*" => l * r, "/" => l / r, _ => l % r };
            }
            return l;
        }

        private long? ParseUnary()
        {
            if (Eat("!")) { var v = ParseUnary(); return v is null ? null : (v == 0 ? 1 : 0); }
            if (Eat("-")) { var v = ParseUnary(); return v is null ? null : -v; }
            if (Eat("+")) return ParseUnary();
            return ParsePrimary();
        }

        private long? ParsePrimary()
        {
            var tok = Peek ?? throw new FormatException("unexpected end");

            if (tok == "(")
            {
                Next();
                var v = ParseOr();
                if (!Eat(")")) throw new FormatException("missing )");
                return v;
            }

            if (tok == "defined")
            {
                Next();
                var paren = Eat("(");
                var name = Next();
                if (paren && !Eat(")")) throw new FormatException("missing ) after defined");
                if (_table.IsDefined(name)) return 1;
                if (_table.IsUnknown(name)) return null;        // varies per TU -> unknown, even closed-world
                return _closedWorld ? 0 : null; // absent: definitely-0 only if the define set is complete
            }

            Next();
            if (TryParseNumber(tok, out var num)) return num;
            if (IsIdentifier(tok)) return ResolveIdentifier(tok);
            throw new FormatException($"unexpected token '{tok}'");
        }

        private long? ResolveIdentifier(string name)
        {
            var value = _table.Value(name);
            if (value is null) return _table.IsUnknown(name) ? null : (_closedWorld ? 0 : null); // unknown wins over closed-world
            if (TryParseNumber(value, out var num)) return num;
            if (_depth > 16) return null;
            return new ExprParser(Tokenize(value), _table, _closedWorld, _depth + 1).ParseFull();
        }
    }

    // ---- tokenizing / helpers -----------------------------------------------------------------

    private static bool TryParseNumber(string tok, out long value)
    {
        value = 0;
        // An unsigned literal changes comparison semantics (-1 < 0u is false in C); don't evaluate it.
        if (tok.Length > 0 && char.IsDigit(tok[0]) && (tok.Contains('u') || tok.Contains('U'))) return false;
        var s = tok.TrimEnd('l', 'L');
        if (s.Length == 0) return false;
        try
        {
            // Hex beyond long.MaxValue is an unsigned value in C; Convert.ToInt64 would wrap it negative.
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && Convert.ToUInt64(s[2..], 16) > long.MaxValue) return false;
            value = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(s[2..], 16)
                : (s.Length > 1 && s[0] == '0' && s.All(char.IsDigit) ? Convert.ToInt64(s, 8) : long.Parse(s));
            return true;
        }
        catch { return false; }
    }

    private static bool IsIdentifier(string tok) =>
        tok.Length > 0 && (char.IsLetter(tok[0]) || tok[0] == '_') && tok.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static List<string> Tokenize(string expr)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < expr.Length)
        {
            var c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                tokens.Add(expr[start..i]);
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '.')) i++;
                tokens.Add(expr[start..i]);
            }
            else
            {
                var two = i + 1 < expr.Length ? expr.Substring(i, 2) : "";
                if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=") { tokens.Add(two); i += 2; }
                else { tokens.Add(c.ToString()); i++; }
            }
        }
        return tokens;
    }

    // The keyword is the identifier run after '#' (and optional whitespace); everything after it is the
    // argument. "#if(X)", "#elif(X)", "#if!defined(X)", "#endif/*x*/" are directives too (review PP5).
    private static (string Keyword, string Remainder) SplitKeyword(string s)
    {
        s = s.TrimStart();
        var i = 0;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return (s[..i], s[i..].Trim());
    }

    private static string FirstToken(string s)
    {
        var t = s.TrimStart();
        var i = 0;
        while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] == '_')) i++;
        return t[..i];
    }

}
