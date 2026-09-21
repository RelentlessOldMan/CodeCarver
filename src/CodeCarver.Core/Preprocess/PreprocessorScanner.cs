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
    private readonly record struct Frame(bool ParentActive, bool Taken, bool BranchActive)
    {
        public bool Active => ParentActive && BranchActive;
    }

    /// <summary>Returns a 1-based map (index 0 unused) where true = the line is in a dead branch.</summary>
    public static bool[] DeadLineMap(string text, MacroTable defines, bool closedWorld = false)
    {
        var lines = text.Split('\n');
        var dead = new bool[lines.Length + 1];
        var table = defines.Clone();
        var stack = new Stack<Frame>();

        for (var idx = 0; idx < lines.Length; idx++)
        {
            var active = stack.Count == 0 || stack.Peek().Active;
            var trimmed = lines[idx].TrimStart();

            if (trimmed.Length > 0 && trimmed[0] == '#')
            {
                var last = idx;
                var joined = lines[idx].TrimEnd('\r');
                while (joined.TrimEnd().EndsWith('\\') && last + 1 < lines.Length)
                {
                    joined = joined.TrimEnd();
                    joined = joined[..^1] + " " + lines[++last].TrimEnd('\r');
                }
                for (var k = idx; k <= last; k++) dead[k + 1] = !active;
                HandleDirective(joined.TrimStart().TrimStart('#').TrimStart(), active, table, stack, closedWorld);
                idx = last;
                continue;
            }

            dead[idx + 1] = !active;
        }

        return dead;
    }

    private static void HandleDirective(string body, bool active, MacroTable table, Stack<Frame> stack, bool closedWorld)
    {
        var (keyword, rest) = SplitKeyword(body);
        switch (keyword)
        {
            case "ifdef":
                // Known-defined => True; absent => False only under closed world, else Unknown (a header may define it).
                Push(stack, active, table.IsDefined(FirstToken(rest)) ? Tri.True : (closedWorld ? Tri.False : Tri.Unknown));
                break;
            case "ifndef":
                Push(stack, active, table.IsDefined(FirstToken(rest)) ? Tri.False : (closedWorld ? Tri.True : Tri.Unknown));
                break;
            case "if":
                Push(stack, active, EvaluateCondition(rest, table, closedWorld));
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
                var (name, value) = SplitKeyword(rest);
                table.Set(ObjectMacroName(name), value.Trim());
                break;
            case "undef" when active:
                table.Undef(FirstToken(rest));
                break;
        }
    }

    private static void Push(Stack<Frame> stack, bool parentActive, Tri cond)
    {
        bool branchActive, taken;
        if (!parentActive) { branchActive = false; taken = true; }
        else
            switch (cond)
            {
                case Tri.True: branchActive = true; taken = true; break;
                case Tri.False: branchActive = false; taken = false; break;
                default: branchActive = true; taken = false; break; // Unknown: keep, allow other branches too
            }
        stack.Push(new Frame(parentActive, taken, branchActive));
    }

    private static void Elif(Stack<Frame> stack, Tri cond)
    {
        if (stack.Count == 0) return;
        var top = stack.Pop();
        bool branchActive, taken = top.Taken;
        if (!top.ParentActive || top.Taken) branchActive = false;
        else
            switch (cond)
            {
                case Tri.True: branchActive = true; taken = true; break;
                case Tri.False: branchActive = false; break;
                default: branchActive = true; break;
            }
        stack.Push(new Frame(top.ParentActive, taken, branchActive));
    }

    private static void Else(Stack<Frame> stack)
    {
        if (stack.Count == 0) return;
        var top = stack.Pop();
        stack.Push(new Frame(top.ParentActive, true, top.ParentActive && !top.Taken));
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
            if (value is null) return _closedWorld ? 0 : null; // undefined: 0 only under closed world
            if (TryParseNumber(value, out var num)) return num;
            if (_depth > 16) return null;
            return new ExprParser(Tokenize(value), _table, _closedWorld, _depth + 1).ParseFull();
        }
    }

    // ---- tokenizing / helpers -----------------------------------------------------------------

    private static bool TryParseNumber(string tok, out long value)
    {
        value = 0;
        var s = tok.TrimEnd('u', 'U', 'l', 'L');
        if (s.Length == 0) return false;
        try
        {
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

    private static (string Keyword, string Remainder) SplitKeyword(string s)
    {
        var i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return (s[..i], i < s.Length ? s[(i + 1)..].Trim() : "");
    }

    private static string FirstToken(string s)
    {
        var t = s.TrimStart();
        var i = 0;
        while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] == '_')) i++;
        return t[..i];
    }

    private static string ObjectMacroName(string lhs)
    {
        var paren = lhs.IndexOf('(');
        return (paren < 0 ? lhs : lhs[..paren]).Trim();
    }
}
