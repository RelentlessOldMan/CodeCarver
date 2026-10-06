namespace CodeCarver.Core.Reachability;

/// <summary>One unresolved reference found by <see cref="EmittedLinkCheck"/>: emitted code names a function
/// that only a DROPPED file defines.</summary>
/// <param name="Name">The function name.</param>
/// <param name="DefinedIn">A dropped file that defines it (first in ordinal order).</param>
/// <param name="ReferencedIn">An emitted file that references it.</param>
/// <param name="Line">1-based line of that reference.</param>
/// <param name="DeadOnly">True when every reference sits on a line the #ifdef model calls dead — the carve
/// relied on that model to drop the definition. Reported, but not a hard failure.</param>
/// <param name="DefinedLine">1-based line of that definition in <paramref name="DefinedIn"/>.</param>
public sealed record LinkViolation(string Name, string DefinedIn, string ReferencedIn, int Line, bool DeadOnly, int DefinedLine = 0);

/// <summary>Outcome of <see cref="EmittedLinkCheck.Run"/>.</summary>
public sealed record LinkCheckResult(IReadOnlyList<LinkViolation> Violations, int FilesChecked, int FilesSkipped)
{
    /// <summary>References on live lines: the emitted tree will not link (or not compile).</summary>
    public IReadOnlyList<LinkViolation> Hard => Violations.Where(v => !v.DeadOnly).ToList();
    /// <summary>References only on #ifdef-dead lines: correct exactly when the #ifdef model is.</summary>
    public IReadOnlyList<LinkViolation> DeadOnly => Violations.Where(v => v.DeadOnly).ToList();
}

/// <summary>
/// The independent soundness check over the EMITTED tree (review V1). It deliberately shares nothing with
/// the graph: no tree-sitter, no edges, no name tables. A plain C/C++ tokenizer collects
/// <list type="number">
/// <item>every function DEFINITION in the dropped code files (identifier, balanced <c>( )</c>, then <c>{</c>
///   at file/namespace scope — <c>static</c> ones in non-header files are skipped, since no other file can
///   link against them);</item>
/// <item>every function definition in the emitted files; and</item>
/// <item>every identifier USE in the emitted files (outside comments, strings, <c>#if</c>/<c>#include</c>
///   lines, member access, and names declared in the same scope).</item>
/// </list>
/// A name defined in a dropped file, defined in no emitted file, and used by emitted code is a violation:
/// the carve dropped a definition the emitted code needs. Because it never consults the graph, it catches
/// exactly the bugs the graph-based check cannot — a wrong #ifdef resolution, a wrong build-log define, a
/// name-table collision, or an emitter that writes more code than reachability accounted for.
///
/// The one input it does share is the #ifdef model (<c>deadLines</c>), and only to CLASSIFY: a use that sits
/// only on lines the model calls dead is reported as <see cref="LinkViolation.DeadOnly"/> — the drop is right
/// if and only if the model is — instead of failing the run, so a correct closed-world carve stays green.
/// </summary>
public static class EmittedLinkCheck
{
    /// <param name="emitted">Emitted code files: (relative path, path on disk to read).</param>
    /// <param name="dropped">Dropped code files: (relative path, path on disk to read).</param>
    /// <param name="deadLines">Optional: (relative path, emitted text) → 1-based dead-line map, or null.</param>
    /// <param name="maxBytes">Files larger than this are not read (counted in FilesSkipped).</param>
    public static LinkCheckResult Run(
        IEnumerable<(string Rel, string Path)> emitted,
        IEnumerable<(string Rel, string Path)> dropped,
        Func<string, string, bool[]?>? deadLines = null,
        long maxBytes = 20_000_000)
    {
        var skipped = 0;
        var checkedFiles = 0;

        // 1. Candidate definitions from dropped files (name -> first defining file).
        var droppedDefs = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        // A file-scope `MACRO(a, b, c)` with no ';' reads as a definition when the next function's '{' follows.
        // A name with only such bare "definitions" that the tree #defines as a function-like macro is a macro use,
        // not a link symbol (work eval, 1.0.162: a registration macro invoked in two modules was reported as
        // "used" in one and "defined only" in the other).
        var typedDef = new HashSet<string>(StringComparer.Ordinal);
        var functionMacros = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, path) in dropped.OrderBy(d => d.Rel, StringComparer.Ordinal))
        {
            var text = TryRead(path, maxBytes);
            if (text is null) { skipped++; continue; }
            checkedFiles++;
            var header = IsHeader(rel);
            var dscan = Scan(text, header);
            functionMacros.UnionWith(dscan.FunctionMacros);
            foreach (var d in dscan.Definitions)
                if (header || !d.Static)
                {
                    droppedDefs.TryAdd(d.Name, (rel, d.Line));
                    if (!d.Bare) typedDef.Add(d.Name);
                }
        }
        if (droppedDefs.Count == 0)
            return new LinkCheckResult(Array.Empty<LinkViolation>(), checkedFiles, skipped);

        // 2+3. Definitions and uses in the emitted files.
        var emittedDefs = new HashSet<string>(StringComparer.Ordinal);
        var live = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        var dead = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        foreach (var (rel, path) in emitted.OrderBy(e => e.Rel, StringComparer.Ordinal))
        {
            var text = TryRead(path, maxBytes);
            if (text is null) { skipped++; continue; }
            checkedFiles++;
            var map = deadLines?.Invoke(rel, text);
            bool IsDead(int line) => map is not null && line < map.Length && map[line];
            var scan = Scan(text, IsHeader(rel));
            functionMacros.UnionWith(scan.FunctionMacros);
            foreach (var d in scan.Definitions)
                if (!IsDead(d.Line)) emittedDefs.Add(d.Name);
            foreach (var (name, line) in scan.Uses)
            {
                if (!droppedDefs.ContainsKey(name)) continue;
                var into = IsDead(line) ? dead : live;
                into.TryAdd(name, (rel, line));
            }
        }

        var violations = new List<LinkViolation>();
        foreach (var name in live.Keys.Concat(dead.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (emittedDefs.Contains(name)) continue;
            if (functionMacros.Contains(name) && !typedDef.Contains(name)) continue;
            var isLive = live.TryGetValue(name, out var at);
            if (!isLive) at = dead[name];
            var def = droppedDefs[name];
            violations.Add(new LinkViolation(name, def.Rel, at.Rel, at.Line, DeadOnly: !isLive, DefinedLine: def.Line));
        }
        return new LinkCheckResult(violations, checkedFiles, skipped);
    }

    static string? TryRead(string path, long maxBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > maxBytes) return null;
            // Latin-1 is byte-transparent: identifiers are ASCII, and no byte sequence can fail to decode.
            return File.ReadAllText(path, System.Text.Encoding.Latin1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsHeader(string rel) =>
        Path.GetExtension(rel).ToLowerInvariant() is ".h" or ".hpp" or ".hxx" or ".hh" or ".h++" or ".inl"
            or ".ipp" or ".tcc" or ".tpp" or ".inc" or ".def";

    // ---- tokenizer + scope tracking ------------------------------------------------------------------

    /// <param name="Bare">Nothing but #define lines precedes the name in its statement (no return type).</param>
    public readonly record struct Definition(string Name, int Line, bool Static, bool Inline, bool Bare = false);

    public sealed class ScanResult
    {
        public List<Definition> Definitions { get; } = new();
        public List<(string Name, int Line)> Uses { get; } = new();
        /// <summary>Names this file #defines as function-like macros.</summary>
        public HashSet<string> FunctionMacros { get; } = new(StringComparer.Ordinal);
    }

    static readonly System.Text.RegularExpressions.Regex FuncLikeDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal readonly record struct Tok(string Text, int Line, bool Ident, bool InDefine);

    // Words that can precede a USE of a name (so "keyword name" is not a declaration of name).
    static readonly HashSet<string> ExprKeywords = new(StringComparer.Ordinal)
    {
        "return", "case", "goto", "sizeof", "else", "do", "typeof", "__typeof__", "alignof", "_Alignof",
        "__alignof__", "throw", "new", "delete", "co_return", "co_await", "co_yield", "and", "or", "not",
        "decltype", "noexcept", "if", "while", "for", "switch",
    };

    static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "switch", "return", "sizeof", "do", "else", "case", "default", "break",
        "continue", "goto", "typedef", "struct", "union", "enum", "class", "namespace", "template",
        "typename", "static", "extern", "const", "volatile", "inline", "void", "int", "char", "short", "long",
        "float", "double", "signed", "unsigned", "auto", "register", "operator", "public", "private",
        "protected", "virtual", "using", "this", "true", "false", "nullptr", "defined", "__attribute__",
        "__declspec", "decltype", "alignof", "static_assert", "_Static_assert", "asm", "__asm__", "__asm",
    };

    /// <summary>Tokenize and collect definitions and uses. In a <paramref name="header"/>, uses inside the body
    /// of a static/inline/template definition, or inside a C++ class body, are not collected: a compiler emits
    /// such a definition only where it is used, and then reachability followed it from that use.</summary>
    public static ScanResult Scan(string text, bool header = false)
    {
        var toks = Tokenize(text);
        var result = new ScanResult();
        if (text.Contains("define", StringComparison.Ordinal))
            foreach (System.Text.RegularExpressions.Match m in FuncLikeDefine.Matches(text))
                result.FunctionMacros.Add(m.Groups[1].Value);

        // Brace stack: 'T' transparent (namespace / extern "C"), 'F' function body, 'Q' function body whose uses
        // don't count (header inline), 'C' class body in a header, 'O' other.
        var braces = new Stack<char>();
        bool AtFileScope() => braces.All(b => b == 'T');
        var fnLocals = new HashSet<string>(StringComparer.Ordinal);   // declared in the current function
        var stmtDeclared = new HashSet<string>(StringComparer.Ordinal); // declared since the last file-scope stmt
        var stmtStart = 0;
        var pendingBody = false;   // the next '{' at file scope opens a function body
        var pendingQuiet = false;  // ... and that body is a header static/inline definition

        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.InDefine) { if (t.Ident) result.Uses.Add((t.Text, t.Line)); continue; }
            // NB: macro bodies count even in headers — a macro is expanded at its (counted) use site.

            if (!t.Ident)
            {
                switch (t.Text)
                {
                    case "{":
                    {
                        char kind;
                        if (AtFileScope() && pendingBody) kind = pendingQuiet ? 'Q' : 'F';
                        else if (AtFileScope() && IsTransparentOpen(toks, i)) kind = 'T';
                        else if (header && IsClassOpen(toks, i)) kind = 'C';
                        else kind = 'O';
                        if (kind is 'F' or 'Q') { fnLocals.Clear(); fnLocals.UnionWith(stmtDeclared); }
                        braces.Push(kind);
                        pendingBody = false;
                        if (kind == 'T') { stmtDeclared.Clear(); stmtStart = i + 1; }
                        break;
                    }
                    case "}":
                        if (braces.Count > 0)
                        {
                            var k = braces.Pop();
                            if (k is 'F' or 'Q') fnLocals.Clear();
                        }
                        if (AtFileScope()) { stmtDeclared.Clear(); stmtStart = i + 1; pendingBody = false; }
                        break;
                    case ";":
                        if (AtFileScope()) { stmtDeclared.Clear(); stmtStart = i + 1; pendingBody = false; }
                        break;
                }
                continue;
            }

            var name = t.Text;
            var prev = i > 0 && !toks[i - 1].InDefine ? toks[i - 1] : default;
            var next = i + 1 < toks.Count && !toks[i + 1].InDefine ? toks[i + 1] : default;
            var inFunction = braces.Contains('F') || braces.Contains('Q');
            var quiet = braces.Contains('Q') || braces.Contains('C');

            // Member access: obj.name / p->name are fields/methods, not free names.
            if (prev.Text is "." or "->") continue;

            // File-scope function definition: name ( ... ) [attrs/qualifiers/init-list] {
            if (AtFileScope() && next.Text == "(" && !NotNames.Contains(name))
            {
                var close = MatchParen(toks, i + 1);
                if (close > 0 && OpensBody(toks, close + 1))
                {
                    bool isStatic = false, isInline = false, bare = true;
                    for (var k = stmtStart; k < i; k++)
                    {
                        if (!toks[k].InDefine) bare = false;
                        var w = toks[k].Text;
                        if (w == "static") isStatic = true;
                        else if (toks[k].Ident && (w.Contains("inline", StringComparison.OrdinalIgnoreCase)
                                 || w is "template" or "constexpr" or "consteval")) isInline = true;
                    }
                    result.Definitions.Add(new Definition(name, t.Line, isStatic, isInline, bare));
                    stmtDeclared.Add(name);
                    pendingBody = true;
                    pendingQuiet = header && (isStatic || isInline);
                    continue;
                }
            }

            if (NotNames.Contains(name) && !ExprKeywords.Contains(name)) continue;

            // "Type name" / "Type *name" / "Type &name": a declaration of name, not a use. Inside a function a
            // declared-looking name directly followed by "(" is still treated as a call (x * f(y)).
            var declared = prev.Ident && !ExprKeywords.Contains(prev.Text)
                || (prev.Text is "*" or "&" or "&&" && i >= 2 && toks[i - 2].Ident && !toks[i - 2].InDefine
                    && !ExprKeywords.Contains(toks[i - 2].Text) && next.Text is "=" or ";" or "," or ")" or "[" or "(");
            if (declared && !(inFunction && next.Text == "("))
            {
                if (inFunction) fnLocals.Add(name); else stmtDeclared.Add(name);
                continue;
            }

            if (quiet) continue;
            if (inFunction && fnLocals.Contains(name)) continue;
            if (!inFunction && AtFileScope() && stmtDeclared.Contains(name)) continue;
            if (ExprKeywords.Contains(name)) continue;
            result.Uses.Add((name, t.Line));
        }
        return result;
    }

    static bool IsTransparentOpen(List<Tok> toks, int brace)
    {
        // namespace {   namespace a::b {   extern "" {
        var k = brace - 1;
        while (k >= 0 && (toks[k].Ident || toks[k].Text == "::") && toks[k].Text != "namespace") k--;
        if (k >= 0 && toks[k].Text == "namespace") return true;
        return brace >= 2 && toks[brace - 1].Text == "\"\"" && toks[brace - 2].Text == "extern";
    }

    static bool IsClassOpen(List<Tok> toks, int brace)
    {
        // class X {   struct X : Base {   union {   — scan back to the statement start for the keyword.
        for (var k = brace - 1; k >= 0 && k >= brace - 64; k--)
        {
            var s = toks[k].Text;
            if (s is ";" or "{" or "}" or ")" or "=") return false;
            if (s is "class" or "struct" or "union") return true;
        }
        return false;
    }

    static int MatchParen(List<Tok> toks, int open)
    {
        var depth = 0;
        for (var k = open; k < toks.Count; k++)
        {
            if (toks[k].InDefine) return -1;
            if (toks[k].Text == "(") depth++;
            else if (toks[k].Text == ")" && --depth == 0) return k;
            else if (toks[k].Text is "{" or "}" or ";") return -1;
        }
        return -1;
    }

    // After a parameter list: does a function body follow (before any ';' or '=' at paren depth 0)?
    static bool OpensBody(List<Tok> toks, int from)
    {
        var depth = 0;
        for (var k = from; k < toks.Count && k < from + 512; k++)
        {
            if (toks[k].InDefine) return false;
            var s = toks[k].Text;
            if (s == "(") depth++;
            else if (s == ")") depth--;
            else if (depth == 0 && s == "{") return true;
            else if (depth == 0 && s is ";" or "=" or "}") return false;
        }
        return false;
    }

    /// <summary>C/C++ tokens with comments, string/char literals (as <c>""</c>) and preprocessor lines handled.
    /// #define bodies are kept (as InDefine tokens, parameters and ## / # operands removed); every other
    /// directive line is skipped entirely.</summary>
    internal static List<Tok> Tokenize(string s, bool directives = true)
    {
        var toks = new List<Tok>();
        var line = 1;
        var i = 0;
        var atLineStart = true;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n') { line++; i++; atLineStart = true; continue; }
            if (c is ' ' or '\t' or '\r' or '\f' or '\v') { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                i += 2;
                continue;
            }
            if (c == '#' && atLineStart && directives)
            {
                // Collect the logical directive line (with continuations), stripped of comments.
                var start = line;
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < s.Length && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '\n' || (s[i + 1] == '\r' && i + 2 < s.Length && s[i + 2] == '\n')))
                    { i += s[i + 1] == '\r' ? 3 : 2; line++; sb.Append(' '); continue; }
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; break; }
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
                    {
                        i += 2;
                        while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                        i += 2; sb.Append(' ');
                        continue;
                    }
                    sb.Append(s[i]); i++;
                }
                DefineBody(sb.ToString(), start, toks);
                continue;
            }
            atLineStart = false;
            if (c is '"' or '\'')
            {
                // Raw strings R"x(...)x" are rare in firmware; a plain scan to the closing quote is enough.
                var q = c; i++;
                while (i < s.Length && s[i] != q && s[i] != '\n') { if (s[i] == '\\') i++; i++; }
                i++;
                toks.Add(new Tok(q == '"' ? "\"\"" : "''", line, false, false));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$')) i++;
                // A string-literal prefix (L"", u8"", R"") is part of the literal, not a name.
                if (i < s.Length && s[i] is '"' or '\'' && i - st <= 2) continue;
                toks.Add(new Tok(s[st..i], line, true, false));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '\'')) i++;
                toks.Add(new Tok("0", line, false, false));
                continue;
            }
            if (i + 1 < s.Length)
            {
                var two = s.Substring(i, 2);
                if (two is "->" or "::" or "&&" or "||" or "==" or "!=" or "<=" or ">=" or "##")
                { toks.Add(new Tok(two, line, false, false)); i += 2; continue; }
            }
            toks.Add(new Tok(c.ToString(), line, false, false));
            i++;
        }
        return toks;
    }

    // #define NAME(params) body  -> body identifiers as InDefine uses; other directives contribute nothing.
    static void DefineBody(string directive, int line, List<Tok> toks)
    {
        var d = directive.TrimStart();
        var k = 0;
        while (k < d.Length && (char.IsLetter(d[k]) || d[k] == '_')) k++;
        if (d[..k] != "define") return;
        var rest = d[k..].TrimStart();
        var n = 0;
        while (n < rest.Length && (char.IsLetterOrDigit(rest[n]) || rest[n] == '_')) n++;
        var body = rest[n..];
        var parms = new HashSet<string>(StringComparer.Ordinal);
        if (body.StartsWith('('))
        {
            var close = body.IndexOf(')');
            if (close < 0) return;
            foreach (var p in body[1..close].Split(',')) parms.Add(p.Trim().TrimEnd('.').Trim());
            body = body[(close + 1)..];
        }
        var inner = Tokenize(body, directives: false);
        for (var j = 0; j < inner.Count; j++)
        {
            var t = inner[j];
            if (!t.Ident || parms.Contains(t.Text) || NotNames.Contains(t.Text)) continue;
            var before = j > 0 ? inner[j - 1].Text : "";
            var after = j + 1 < inner.Count ? inner[j + 1].Text : "";
            if (before is "." or "->" or "#" or "##" || after == "##") continue;
            toks.Add(new Tok(t.Text, line, true, true));
        }
    }
}
