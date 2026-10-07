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
public sealed record LinkViolation(string Name, string DefinedIn, string ReferencedIn, int Line, bool DeadOnly, int DefinedLine = 0)
{
    /// <summary>Every dropped file that defines the name (<see cref="DefinedIn"/> is the first).</summary>
    public IReadOnlyList<string> DefinedInAll { get; init; } = Array.Empty<string>();
    /// <summary>The emitted code declares the name weak: an unresolved reference links (as null).</summary>
    public bool Weak { get; init; }
}

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
    /// <param name="original">For a stage that carves INSIDE kept files: emitted relative path → the original file on
    /// disk. A function the original defines and the emitted copy doesn't was pruned, and counts as dropped.</param>
    public static LinkCheckResult Run(
        IEnumerable<(string Rel, string Path)> emitted,
        IEnumerable<(string Rel, string Path)> dropped,
        Func<string, string, bool[]?>? deadLines = null,
        long maxBytes = 20_000_000,
        Func<string, string?>? original = null,
        CodeCarver.Core.Preprocess.DefinerMacros.Set? definerMacros = null,
        IEnumerable<(string Name, string Rel, int Line)>? linkUses = null,
        IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? commandLineMacros = null,
        IReadOnlySet<string>? c99InlineFns = null,
        IReadOnlySet<string>? wrapped = null)
    {
        emitted = emitted.ToList();
        dropped = dropped.ToList();
        var skipped = 0;
        var checkedFiles = 0;

        // Template headers (see TemplateInstances): the names a translation unit defines by including one.
        var byBase = emitted.Concat(dropped).GroupBy(f => Path.GetFileName(f.Rel), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList(), StringComparer.OrdinalIgnoreCase);
        var templateText = new Dictionary<string, string?>(StringComparer.Ordinal);
        var templates = new HashSet<string>(StringComparer.Ordinal);
        // A header's text as TemplateInstances reads it: whole for a template, its directive lines otherwise (a config
        // header still carries macros a template after it uses). The same string every time.
        string? ReadHeader(string p)
        {
            if (!templateText.TryGetValue(p, out var t))
            {
                t = TryRead(p, maxBytes);
                if (t is not null) t = CodeCarver.Core.Preprocess.SourceText.Normalize(t);
                if (t is not null && CodeCarver.Core.Preprocess.TemplateInstances.HasMacroNamedHead(t)) templates.Add(p);
                else if (t is not null) t = CodeCarver.Core.Preprocess.TemplateInstances.DirectivesOnly(t);
                templateText[p] = t;
            }
            return t;
        }
        string? ReadTemplate(string raw)
        {
            if (!byBase.TryGetValue(Path.GetFileName(raw), out var ps)) return null;
            foreach (var p in ps) ReadHeader(p);
            return ReadHeader(ps.FirstOrDefault(templates.Contains) ?? ps[0]);
        }
        bool? anyTemplate = null;
        bool AnyTemplate()
        {
            if (anyTemplate is null)
            {
                foreach (var p in byBase.Values.SelectMany(ps => ps).Where(p => IsHeader(p))) ReadHeader(p);
                anyTemplate = templates.Count > 0;
            }
            return anyTemplate.Value;
        }
        var cmdFnMacros = CodeCarver.Core.Preprocess.CommandLineMacros.AnyFunctionLike(commandLineMacros);
        // Names a translation unit defines beyond its own function heads: template-header instances (with the build's
        // function-like -D macros, `int HIDE(den)(void) {`), and the C99 inline functions it emits by declaring them.
        IEnumerable<Definition> Instances(string rel, string text)
        {
            if (IsHeader(rel) || IsAssembly(rel)) return Array.Empty<Definition>();
            var r = new List<Definition>();
            if ((text.Contains("include", StringComparison.Ordinal) && AnyTemplate())
                || (CodeCarver.Core.Preprocess.TemplateInstances.HasMacroNamedHead(text)
                    && (cmdFnMacros || text.Contains("define", StringComparison.Ordinal))))
                r.AddRange(CodeCarver.Core.Preprocess.TemplateInstances.Find(text, ReadTemplate, commandLineMacros)
                               .Select(i => new Definition(i.Name, i.Line, false, false)));
            if (c99InlineFns is { Count: > 0 })
            {
                var starts = LineStarts(text);
                r.AddRange(CodeCarver.Core.Preprocess.C99Inline.Emitters(text, c99InlineFns)
                               .Select(e => new Definition(e.Name, LineOf(starts, e.Index), false, false)));
            }
            return r;
        }
        // A command-line rename (-Dsecret_rite=true_rite): a function defined as secret_rite is the symbol true_rite.
        void Renamed(ScanResult s)
        {
            if (commandLineMacros is not { Count: > 0 }) return;
            foreach (var d in s.Definitions.ToList())
                foreach (var to in CodeCarver.Core.Preprocess.CommandLineMacros.RenamesOf(commandLineMacros, d.Name)) s.Definitions.Add(d with { Name = to });
        }

        // 1. Candidate definitions from dropped files (name -> first defining file).
        var droppedDefs = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        // A file-scope `MACRO(a, b, c)` with no ';' reads as a definition when the next function's '{' follows.
        // A name with only such bare "definitions" that the tree #defines as a function-like macro is a macro use,
        // not a link symbol (work eval, 1.0.162: a registration macro invoked in two modules was reported as
        // "used" in one and "defined only" in the other).
        var typedDef = new HashSet<string>(StringComparer.Ordinal);
        var definers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var functionMacros = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, path) in dropped.OrderBy(d => d.Rel, StringComparer.Ordinal))
        {
            var text = TryRead(path, maxBytes);
            if (text is null) { skipped++; continue; }
            checkedFiles++;
            var header = IsHeader(rel);
            var dscan = Scan(text, header, declarators: false, definerMacros);
            dscan.Definitions.AddRange(Instances(rel, text));
            Renamed(dscan);
            functionMacros.UnionWith(dscan.FunctionMacros);
            foreach (var d in dscan.Definitions)
                if (header || !d.Static)
                {
                    droppedDefs.TryAdd(d.Name, (rel, d.Line));
                    if (!definers.TryGetValue(d.Name, out var dl)) definers[d.Name] = dl = new List<string>();
                    if (!dl.Contains(rel)) dl.Add(rel);
                    if (!d.Bare) typedDef.Add(d.Name);
                }
        }
        // Functions pruned out of kept files. A pruned static is only visible in its own file.
        var prunedStatics = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        if (original is not null)
            foreach (var (rel, path) in emitted)
            {
                if (IsAssembly(rel) || original(rel) is not { } op) continue;
                var otext = TryRead(op, maxBytes);
                var etext = otext is null ? null : TryRead(path, maxBytes);
                if (otext is null || etext is null || otext == etext) continue;
                var header = IsHeader(rel);
                var kept = Scan(etext, header, declarators: false, definerMacros).Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var d in Scan(otext, header, declarators: false, definerMacros).Definitions)
                {
                    if (kept.Contains(d.Name)) continue;
                    if (d.Static && !header)
                    {
                        if (!prunedStatics.TryGetValue(rel, out var ps)) prunedStatics[rel] = ps = new(StringComparer.Ordinal);
                        ps.TryAdd(d.Name, d.Line);
                        continue;
                    }
                    droppedDefs.TryAdd(d.Name, (rel, d.Line));
                    if (!definers.TryGetValue(d.Name, out var dl)) definers[d.Name] = dl = new List<string>();
                    if (!dl.Contains(rel)) dl.Add(rel);
                    if (!d.Bare) typedDef.Add(d.Name);
                }
            }
        var staticViolations = new List<LinkViolation>();
        if (droppedDefs.Count == 0 && prunedStatics.Count == 0)
            return new LinkCheckResult(Array.Empty<LinkViolation>(), checkedFiles, skipped);

        // 2+3. Definitions and uses in the emitted files.
        var emittedDefs = new HashSet<string>(StringComparer.Ordinal);
        var inlineHeaderDefs = new HashSet<string>(StringComparer.Ordinal);
        var live = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        var dead = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        // Per file: the function-like macros it #defines on live lines, and what it #includes. A call `name(...)` where
        // a function-like macro `name` is visible (this file or a header it includes) is expanded by the preprocessor:
        // it never reaches the linker, whatever a same-named function elsewhere does.
        var fileMacros = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fileIncludes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var headersByBase = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var macroCalls = new List<(string Name, string Rel, int Line, bool Dead)>();
        var aliasMacros = new HashSet<string>(StringComparer.Ordinal);
        var macroDeclarators = new List<(string Name, string Macro, string Target, string Rel, int Line)>();
        // Weak: declared weak in a header (it may be included anywhere), or every use sits in a file that declares it weak
        // itself. One file's weak declaration does not make another file's reference weak: ELF fails the link on that one.
        var weakNames = new HashSet<string>(StringComparer.Ordinal);
        var strongUse = new HashSet<string>(StringComparer.Ordinal);
        var weakUse = new HashSet<string>(StringComparer.Ordinal);
        // Only a symbol the build wraps (--wrap=X): a __wrap_X elsewhere is a unit-test mock, not part of this link.
        var anyWrap = wrapped is { Count: > 0 } && droppedDefs.Keys.Any(k => k.StartsWith("__wrap_", StringComparison.Ordinal));
        foreach (var (rel, path) in emitted.OrderBy(e => e.Rel, StringComparer.Ordinal))
        {
            var text = TryRead(path, maxBytes);
            if (text is null) { skipped++; continue; }
            checkedFiles++;
            var map = deadLines?.Invoke(rel, text);
            bool IsDead(int line) => map is not null && line < map.Length && map[line];
            var scan = IsAssembly(rel) ? ScanAssembly(text) : Scan(text, IsHeader(rel), definerMacros: definerMacros);
            scan.Definitions.AddRange(Instances(rel, text));
            Renamed(scan);
            functionMacros.UnionWith(scan.FunctionMacros);
            fileMacros[rel] = scan.FunctionMacroLines.Where(m => !IsDead(m.Line)).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            fileIncludes[rel] = scan.Includes;
            var b = Path.GetFileName(rel);
            if (!headersByBase.TryGetValue(b, out var hl)) headersByBase[b] = hl = new List<string>();
            hl.Add(rel);
            foreach (var d in scan.Definitions)
                if (IsDead(d.Line)) continue;
                else if (d.Inline && !d.Static && IsHeader(rel)) inlineHeaderDefs.Add(d.Name);
                else emittedDefs.Add(d.Name);
            aliasMacros.UnionWith(scan.AliasMacros);
            foreach (var md in scan.MacroDeclarators) if (!IsDead(md.Line)) macroDeclarators.Add((md.Name, md.Macro, md.Target, rel, md.Line));
            if (IsHeader(rel)) weakNames.UnionWith(scan.WeakDeclarations);
            foreach (var (name, _) in scan.Uses) (scan.WeakDeclarations.Contains(name) ? weakUse : strongUse).Add(name);
            if (prunedStatics.TryGetValue(rel, out var pruned))
            {
                var definedHere = scan.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (name, line) in scan.Uses)
                    if (pruned.TryGetValue(name, out var defLine) && !definedHere.Contains(name) && reported.Add(name)
                        && !(scan.CallUses.Contains((name, line)) && scan.FunctionMacros.Contains(name)))
                        staticViolations.Add(new LinkViolation(name, rel, rel, line, DeadOnly: IsDead(line), DefinedLine: defLine)
                                             { DefinedInAll = new[] { rel } });
            }
            foreach (var (name, line) in scan.Uses)
            {
                // Linked with --wrap=X, every X call goes to __wrap_X: a dropped wrapper breaks the link.
                if (anyWrap && wrapped!.Contains(name) && droppedDefs.ContainsKey("__wrap_" + name)) (IsDead(line) ? dead : live).TryAdd("__wrap_" + name, (rel, line));
                if (!droppedDefs.ContainsKey(name)) continue;
                if (scan.CallUses.Contains((name, line))) { macroCalls.Add((name, rel, line, IsDead(line))); continue; }
                var into = IsDead(line) ? dead : live;
                into.TryAdd(name, (rel, line));
            }
        }
        // A non-static inline body in a header provides the symbol only in C++. In C it emits nothing: a translation unit
        // emits it (C99, by declaring it extern) or defines it out of line (GNU extern inline). When such a translation
        // unit was dropped, the header's body links nothing.
        foreach (var name in inlineHeaderDefs)
            if (!(definers.TryGetValue(name, out var dl) && dl.Any(r => !IsHeader(r)))) emittedDefs.Add(name);
        // Symbols the link itself names (--defsym's right side, --undefined, a linker script's EXTERN...): ld needs
        // them whatever the C code calls.
        if (linkUses is not null)
            foreach (var (name, rel, line) in linkUses)
                if (droppedDefs.ContainsKey(name)) { live.TryAdd(name, (rel, line)); strongUse.Add(name); }
        // Function-like macros visible in a file: its own, plus those of every emitted header it includes (by basename,
        // transitively). Memoised; a cycle just stops.
        var visible = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Visible(string rel)
        {
            if (visible.TryGetValue(rel, out var v)) return v;
            v = new HashSet<string>(StringComparer.Ordinal);
            visible[rel] = v;   // cycle guard
            var stack = new Stack<string>(); stack.Push(rel);
            var seen = new HashSet<string>(StringComparer.Ordinal) { rel };
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (fileMacros.TryGetValue(f, out var ms)) v.UnionWith(ms);
                if (!fileIncludes.TryGetValue(f, out var incs)) continue;
                foreach (var inc in incs)
                    if (headersByBase.TryGetValue(Path.GetFileName(inc), out var hs))
                        foreach (var h in hs) if (seen.Add(h)) stack.Push(h);
            }
            return v;
        }
        foreach (var (name, macro, target, mrel, mline) in macroDeclarators)
            if (aliasMacros.Contains(macro))
            {
                emittedDefs.Add(name);
                if (droppedDefs.ContainsKey(target)) live.TryAdd(target, (mrel, mline));   // the alias needs its target
            }
        foreach (var (name, rel, line, isDead) in macroCalls)
        {
            // Only a name some file #defines as a function-like macro can be one (the include closure is the cost).
            if (functionMacros.Contains(name) && Visible(rel).Contains(name)) continue;   // a macro invocation, not a reference
            (isDead ? dead : live).TryAdd(name, (rel, line));
        }

        var violations = new List<LinkViolation>();
        foreach (var name in live.Keys.Concat(dead.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (emittedDefs.Contains(name)) continue;

            if (functionMacros.Contains(name) && !typedDef.Contains(name)) continue;
            var isLive = live.TryGetValue(name, out var at);
            if (!isLive) at = dead[name];
            var def = droppedDefs[name];
            violations.Add(new LinkViolation(name, def.Rel, at.Rel, at.Line, DeadOnly: !isLive, DefinedLine: def.Line) { DefinedInAll = definers[name], Weak = weakNames.Contains(name) || (weakUse.Contains(name) && !strongUse.Contains(name)) });
        }
        violations.AddRange(staticViolations);
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
        /// <summary>Each function-like #define, with its line.</summary>
        public List<(string Name, int Line)> FunctionMacroLines { get; } = new();
        /// <summary>What each #include names ("x.h" or &lt;x.h&gt;, without the delimiters).</summary>
        public List<string> Includes { get; } = new();
        /// <summary>Uses written as a call, <c>name(</c>: a function-like macro of that name would expand them.</summary>
        public HashSet<(string Name, int Line)> CallUses { get; } = new();
        /// <summary>Function-like macros this file defines whose body is <c>alias(#arg)</c>.</summary>
        public HashSet<string> AliasMacros { get; } = new(StringComparer.Ordinal);
        /// <summary><c>decl(...) MACRO(target);</c>: a definition of decl when MACRO is an alias macro (which may be
        /// #defined in another file).</summary>
        public List<(string Name, int Line, string Macro, string Target)> MacroDeclarators { get; } = new();
        /// <summary>Names declared weak without a body: a reference to one may stay unresolved.</summary>
        public HashSet<string> WeakDeclarations { get; } = new(StringComparer.Ordinal);
        /// <summary>Asm labels: the C name, its symbol, and the line.</summary>
        public List<(string Name, string Symbol, int Line)> AsmLabels { get; } = new();
    }

    static readonly System.Text.RegularExpressions.Regex FuncLikeDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex IncludeLine = new(
        @"^[ \t]*#[ \t]*include[ \t]*[""<]([^"">\r\n]+)["">]",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Names a file defines without a body: aliases and assembler symbols. A reference to one links.
    //   void hook(void) __attribute__((weak, alias("impl")));      __attribute__((alias("impl"))) void hook(void);
    //   void hook(void) WEAK_ALIAS(impl);   (a macro whose body is alias(#arg))      #pragma weak hook = impl
    //   __asm__(".set hook, impl")   .equ / .equiv      .globl hook ... hook:
    const System.Text.RegularExpressions.RegexOptions Rx = System.Text.RegularExpressions.RegexOptions.Multiline
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
    // Group 1 = the alias (a definition), group 2 = its target (a use: the alias needs it).
    static readonly System.Text.RegularExpressions.Regex AliasAfter = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*__attribute__\s*\(\([^;{}]*?\b(?:alias|ifunc)\s*\(\s*""(\w+)""", Rx);
    static readonly System.Text.RegularExpressions.Regex AliasBefore = new(
        @"__attribute__\s*\(\([^;{}]*?\b(?:alias|ifunc)\s*\(\s*""(?<t>\w+)""\s*\)[^;{}]*?\)\)[\w\s\*]*?\b(?<n>[A-Za-z_]\w*)\s*\([^(){};]*\)\s*;", Rx);
    static readonly System.Text.RegularExpressions.Regex AliasMacroDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(\s*(\w+)\s*\)[^\n]*\balias\s*\(\s*#\s*\2\b", Rx);
    static readonly System.Text.RegularExpressions.Regex MacroDeclarator = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*([A-Za-z_]\w*)\s*\(\s*(\w+)\s*\)\s*;", Rx);
    // #pragma weak a = b  and  _Pragma("weak a = b"); without "= b" only a is declared weak.
    static readonly System.Text.RegularExpressions.Regex PragmaWeak = new(
        @"(?:^[ \t]*#[ \t]*pragma[ \t]+weak|\b_Pragma\s*\(\s*""\s*weak)[ \t]+([A-Za-z_]\w*)(?:[ \t]*=[ \t]*([A-Za-z_]\w*))?", Rx);
    static readonly System.Text.RegularExpressions.Regex AsmSet = new(
        @"\.(?:set|equ|equiv)\s+([A-Za-z_.$][\w.$]*)\s*,\s*([A-Za-z_.$][\w.$]*)?", Rx);
    // A weak declaration (no body): the reference may stay unresolved, so it needs no definition.
    static readonly System.Text.RegularExpressions.Regex WeakDeclAfter = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*__attribute__\s*\(\([^;{}()]*\bweak\b[^;{}()]*\)\)\s*;", Rx);
    static readonly System.Text.RegularExpressions.Regex WeakDeclBefore = new(
        @"__attribute__\s*\(\([^;{}()]*\bweak\b[^;{}()]*\)\)[\w\s\*]*?\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*;", Rx);
    // `int hidden(int) __asm__("sym");` - the declarator's symbol is sym.
    static readonly System.Text.RegularExpressions.Regex AsmLabel = new(
        @"\b([A-Za-z_]\w*)\s*\([^(){};]*\)\s*(?:__asm__|__asm|asm)\s*\(\s*""([A-Za-z_.$][\w.$]*)""\s*\)", Rx);
    static readonly System.Text.RegularExpressions.Regex RedefineExtname = new(
        @"^[ \t]*#[ \t]*pragma[ \t]+redefine_extname[ \t]+([A-Za-z_]\w*)[ \t]+([A-Za-z_.$][\w.$]*)", Rx);
    static readonly System.Text.RegularExpressions.Regex AsmGlobal = new(
        @"(?:^|[\s""])\.(?:globl|global|weak|weakref)\s+([A-Za-z_.$][\w.$]*(?:\s*,\s*[A-Za-z_.$][\w.$]*)*)", Rx);
    static readonly System.Text.RegularExpressions.Regex AsmPublic = new(
        @"^[ \t]*(?:PUBLIC|EXPORT|GLOBAL|XDEF|\.public)[ \t]+([A-Za-z_]\w*(?:[ \t]*,[ \t]*[A-Za-z_]\w*)*)|^[ \t]*([A-Za-z_]\w*)[ \t]+PROC\b",
        Rx | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Assembly source: a GNU/ARM/MASM-style file, not C.</summary>
    public static bool IsAssembly(string rel) =>
        Path.GetExtension(rel).ToLowerInvariant() is ".s" or ".asm" or ".sx" or ".a51" or ".s43" or ".s90";

    static int[] LineStarts(string text)
    {
        var l = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') l.Add(i + 1);
        return l.ToArray();
    }
    static int LineOf(int[] starts, int index)
    {
        var k = Array.BinarySearch(starts, index);
        return (k >= 0 ? k : ~k - 1) + 1;
    }

    /// <summary>Aliases, weak pragmas and assembler symbols defined in C text (see the regexes above). A declarator
    /// followed by a macro goes to <see cref="ScanResult.MacroDeclarators"/>: whether that macro is an alias macro is
    /// decided across files.</summary>
    static void BodilessDefinitions(string text, ScanResult result, bool declarators)
    {
        var starts = LineStarts(text);
        void Add(string name, int index) => result.Definitions.Add(new Definition(name, LineOf(starts, index), false, false));
        // The alias needs its target: a use (it sits in a string or a directive, which the tokenizer skips).
        void Use(System.Text.RegularExpressions.Group g) { if (g.Success) result.Uses.Add((g.Value, LineOf(starts, g.Index))); }
        if (text.Contains("alias", StringComparison.Ordinal) || text.Contains("ifunc", StringComparison.Ordinal))   // an ifunc: defined by its resolver
        {
            foreach (System.Text.RegularExpressions.Match m in AliasAfter.Matches(text)) { Add(m.Groups[1].Value, m.Groups[1].Index); Use(m.Groups[2]); }
            foreach (System.Text.RegularExpressions.Match m in AliasBefore.Matches(text)) { Add(m.Groups["n"].Value, m.Groups["n"].Index); Use(m.Groups["t"]); }
            foreach (System.Text.RegularExpressions.Match m in AliasMacroDefine.Matches(text)) result.AliasMacros.Add(m.Groups[1].Value);
        }
        if (declarators) foreach (System.Text.RegularExpressions.Match m in MacroDeclarator.Matches(text))
            result.MacroDeclarators.Add((m.Groups[1].Value, LineOf(starts, m.Groups[1].Index), m.Groups[2].Value, m.Groups[3].Value));
        if (text.Contains("weak", StringComparison.Ordinal))
        {
            foreach (System.Text.RegularExpressions.Match m in PragmaWeak.Matches(text))
            {
                if (m.Groups[2].Success) { Add(m.Groups[1].Value, m.Groups[1].Index); Use(m.Groups[2]); }
                else result.WeakDeclarations.Add(m.Groups[1].Value);
            }
            foreach (System.Text.RegularExpressions.Match m in WeakDeclAfter.Matches(text)) result.WeakDeclarations.Add(m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in WeakDeclBefore.Matches(text)) result.WeakDeclarations.Add(m.Groups[1].Value);
        }
        // `#pragma redefine_extname cname sym` renames like an asm label.
        if (text.Contains("redefine_extname", StringComparison.Ordinal))
            foreach (System.Text.RegularExpressions.Match m in RedefineExtname.Matches(text))
            {
                Add(m.Groups[1].Value, m.Groups[1].Index);
                result.AsmLabels.Add((m.Groups[1].Value, m.Groups[2].Value, LineOf(starts, m.Groups[2].Index)));
            }
        if (text.Contains("asm", StringComparison.Ordinal))
        {
            foreach (System.Text.RegularExpressions.Match m in AsmSet.Matches(text)) { Add(m.Groups[1].Value, m.Groups[1].Index); Use(m.Groups[2]); }
            // An asm label renames the symbol: the C name is declared here (calls to it never reach a same-named
            // function elsewhere); the symbol is defined here when the C name has a body here, else it is used.
            foreach (System.Text.RegularExpressions.Match m in AsmLabel.Matches(text))
            {
                Add(m.Groups[1].Value, m.Groups[1].Index);
                result.AsmLabels.Add((m.Groups[1].Value, m.Groups[2].Value, LineOf(starts, m.Groups[2].Index)));
            }
            // .globl name together with a `name:` label in the same text.
            foreach (System.Text.RegularExpressions.Match m in AsmGlobal.Matches(text))
                foreach (var n in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?:^|[\s""])" + System.Text.RegularExpressions.Regex.Escape(n) + @"\s*:"))
                        Add(n, m.Groups[1].Index);
        }
    }

    /// <summary>An assembly file: its exported and aliased symbols are definitions (<c>.globl</c>/<c>.weak</c>,
    /// <c>PUBLIC</c>/<c>EXPORT</c>, <c>name PROC</c>, <c>.set</c>/<c>.equ</c>). Uses are scanned as for C, so a vector
    /// table's <c>.word handler</c> still counts.</summary>
    public static ScanResult ScanAssembly(string text)
    {
        var result = Scan(text);
        result.Definitions.Clear();
        var starts = LineStarts(text);
        void Add(string name, int index) => result.Definitions.Add(new Definition(name, LineOf(starts, index), false, false));
        foreach (System.Text.RegularExpressions.Match m in AsmGlobal.Matches(text))
            foreach (var n in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) Add(n, m.Groups[1].Index);
        foreach (System.Text.RegularExpressions.Match m in AsmPublic.Matches(text))
        {
            if (m.Groups[1].Success)
                foreach (var n in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) Add(n, m.Groups[1].Index);
            else Add(m.Groups[2].Value, m.Groups[2].Index);
        }
        foreach (System.Text.RegularExpressions.Match m in AsmSet.Matches(text)) Add(m.Groups[1].Value, m.Groups[1].Index);
        return result;
    }

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
    /// <param name="declarators">Collect <see cref="ScanResult.MacroDeclarators"/> (only emitted files need them).</param>
    /// <param name="definerMacros">The tree's definer macros (the file's own are always added).</param>
    public static ScanResult Scan(string text, bool header = false, bool declarators = true,
                                  CodeCarver.Core.Preprocess.DefinerMacros.Set? definerMacros = null,
        IEnumerable<(string Name, string Rel, int Line)>? linkUses = null,
        IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? commandLineMacros = null,
        IReadOnlySet<string>? c99InlineFns = null)
    {
        // Trigraphs and names split by a backslash-newline, as the compiler reads them.
        text = CodeCarver.Core.Preprocess.SourceText.Normalize(text);
        // `#if 0` blocks are dead in every configuration and often not C (unbalanced braces): never read them.
        text = CodeCarver.Core.Preprocess.PreprocessorScanner.BlankAlwaysDead(text);
        var toks = Tokenize(text);
        var result = new ScanResult();
        if (text.Contains('#'))
        {
            var starts = LineStarts(text);
            if (text.Contains("define", StringComparison.Ordinal))
                foreach (System.Text.RegularExpressions.Match m in FuncLikeDefine.Matches(text))
                {
                    result.FunctionMacros.Add(m.Groups[1].Value);
                    result.FunctionMacroLines.Add((m.Groups[1].Value, LineOf(starts, m.Index)));
                }
            if (text.Contains("include", StringComparison.Ordinal))
                foreach (System.Text.RegularExpressions.Match m in IncludeLine.Matches(text)) result.Includes.Add(m.Groups[1].Value);
        }
        BodilessDefinitions(text, result, declarators);

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

            // `int (name)(params) {`: a parenthesised name (it dodges a function-like macro of the same name).
            if (AtFileScope() && prev.Text == "(" && next.Text == ")" && i + 2 < toks.Count && toks[i + 2].Text == "("
                && !NotNames.Contains(name) && MatchParen(toks, i + 2) is var pclose && pclose > 0 && OpensBody(toks, pclose + 1))
            {
                result.Definitions.Add(new Definition(name, t.Line, StaticBefore(toks, stmtStart, i), false));
                stmtDeclared.Add(name);
                pendingBody = true;
                pendingQuiet = header && StaticBefore(toks, stmtStart, i);
                i = pclose;
                continue;
            }
            // `int WRAP(name)(params) {`: the name wrapped in a macro (`#define WRAP(n) n`). A real parameter list is
            // never a lone identifier followed by another parameter list, so the definition is of the inner name.
            if (AtFileScope() && next.Text == "(" && i + 4 < toks.Count && toks[i + 2].Ident && !toks[i + 2].InDefine
                && toks[i + 3].Text == ")" && toks[i + 4].Text == "(" && !NotNames.Contains(name)
                && MatchParen(toks, i + 4) is var wclose && wclose > 0 && OpensBody(toks, wclose + 1))
            {
                var inner = toks[i + 2].Text;
                var st = StaticBefore(toks, stmtStart, i);
                result.Definitions.Add(new Definition(inner, t.Line, st, false));
                stmtDeclared.Add(inner);
                pendingBody = true;
                pendingQuiet = header && st;
                i = wclose;
                continue;
            }

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
            if (next.Text == "(") result.CallUses.Add((name, t.Line));   // `(name)(x)` has ")" next: never a macro call
        }
        DefinerDefinitions(text, result, definerMacros);
        foreach (var (cname, sym, line) in result.AsmLabels)
            if (result.Definitions.Any(d => d.Name == cname && !d.Bare && d.Line != line)) result.Definitions.Add(new Definition(sym, line, false, false));
            else result.Uses.Add((sym, line));
        // A by-name lookup (dlsym(h, "name")): the string is a use. The link doesn't need it, the run does.
        if (text.Contains("\"", StringComparison.Ordinal))
        {
            int[]? starts = null;
            foreach (var (name, index) in CodeCarver.Core.Preprocess.DynamicLookup.Names(text))
                result.Uses.Add((name, LineOf(starts ??= LineStarts(text), index)));
        }
        // Linked with --wrap=X, `__real_X` is the original X.
        for (var u = result.Uses.Count - 1; u >= 0; u--)
            if (result.Uses[u].Name is { Length: > 7 } un && un.StartsWith("__real_", StringComparison.Ordinal))
                result.Uses.Add((un[7..], result.Uses[u].Line));
        return result;
    }

    /// <summary>Names a definer macro defines at its file-scope uses (<c>BEGIN_FN(name)</c>, <c>DEFINE_GETTER(speed)</c>,
    /// <c>FW_DECLARE(uart, ...)</c> defining <c>uart_desc</c>): the tree's definers plus the ones this file #defines.</summary>
    static void DefinerDefinitions(string text, ScanResult result, CodeCarver.Core.Preprocess.DefinerMacros.Set? tree)
    {
        var set = tree;
        if (result.FunctionMacros.Count > 0)
        {
            var own = CodeCarver.Core.Preprocess.DefinerMacros.FromText(text);
            if (own.Count > 0)
            {
                if (tree is not null)
                    foreach (var kv in tree.Definers) own.TryAdd(kv.Key, kv.Value);
                set = CodeCarver.Core.Preprocess.DefinerMacros.MakeSet(own);
            }
        }
        if (set is null) return;
        var have = result.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var use in CodeCarver.Core.Preprocess.DefinerMacros.Uses(text, set.Use, set.Definers))
            foreach (var (name, _) in use.Defines)
                if (have.Add(name)) result.Definitions.Add(new Definition(name, use.StartLine, Static: false, Inline: false));
    }

    static bool StaticBefore(List<Tok> toks, int from, int to)
    {
        for (var k = from; k < to; k++) if (toks[k].Text == "static") return true;
        return false;
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
            // A backslash-newline outside a directive is a line splice: whitespace between tokens (SourceText.SplitNames
            // already joined any name it split). The line still counts; a directive can't start on the spliced line.
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '\n' || (s[i + 1] == '\r' && i + 2 < s.Length && s[i + 2] == '\n')))
            { i += s[i + 1] == '\r' ? 3 : 2; line++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                i += 2;
                continue;
            }
            if ((c == '#' || (c == '%' && i + 1 < s.Length && s[i + 1] == ':')) && atLineStart && directives)
            {
                // Collect the logical directive line (with continuations), stripped of comments. `%:` is the # digraph.
                var start = line;
                var sb = new System.Text.StringBuilder();
                i += c == '%' ? 2 : 1;
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
                // Digraphs: <% %> <: :> are { } [ ] (but C++ `<::` before anything but : or > is < ::).
                var di = two switch { "<%" => "{", "%>" => "}", "<:" => "[", ":>" => "]", _ => null };
                if (di is not null && !(two == "<:" && i + 2 < s.Length && s[i + 2] == ':' && (i + 3 >= s.Length || s[i + 3] is not (':' or '>'))))
                { toks.Add(new Tok(di, line, false, false)); i += 2; continue; }
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
