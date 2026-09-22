using System.Text.RegularExpressions;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;
using TreeSitter;
using TsNode = TreeSitter.Node;

namespace CodeCarver.Frontend;

/// <summary>
/// Shared tree-sitter extraction for the C-family languages. Subclasses supply only the grammar and
/// the definition/call queries; everything else — file/#include structure, DefinedIn edges, direct
/// calls, conservative address-taken edges, macro expansion, name-based resolution — lives here.
///
/// A note on soundness by name resolution: call targets are resolved by unqualified name across the
/// whole input. That over-approximates (a call to <c>area()</c> keeps every <c>area</c> definition),
/// which is exactly what makes C++ virtual dispatch sound without building the class hierarchy —
/// calling a method keeps all its overrides. Over-keeping is the safe side of "must build".
/// </summary>
public abstract class TreeSitterFrontEnd : ICarveFrontEnd
{
    private const string IdentQuery = "(identifier) @id";

    /// <summary><c>#include "x"</c> or <c>#include &lt;x&gt;</c>, scanned from text (line-oriented) so it
    /// catches includes tree-sitter misses — e.g. inside an array initializer (the data-fragment case).</summary>
    private static readonly Regex IncludeLine = new(
        """^\s*#\s*include\s+(?:"([^"]+)"|<([^>]+)>)""", RegexOptions.Compiled);

    /// <summary><c>void h(void) __attribute__((weak, alias("target")))</c> — a GCC symbol alias. The
    /// aliasing name IS the target function, so without an edge to the target, dropping the target breaks
    /// the link. Ubiquitous in embedded startup, where every unused handler aliases a Default_Handler.</summary>
    private static readonly Regex AliasAttr = new(
        @"(?<name>[A-Za-z_]\w*)\s*\([^()]*\)\s*__attribute__\s*\(\(\s*[^()]*?\balias\s*\(\s*""(?<target>[A-Za-z_]\w*)""",
        RegexOptions.Compiled);

    // Keep-attributes: symbols the runtime/linker keep regardless of any call — implicit roots a
    // from-main closure would silently drop (a self-registering `constructor`, an initcall-section entry).
    private static readonly Regex AttrBlock = new(
        @"__attribute__\s*\(\((?<body>(?:[^()]|\([^()]*\))*)\)\)", RegexOptions.Compiled);
    private static readonly Regex KeepKeyword = new(
        @"\b(?:constructor|destructor|used|retain)\b|section\s*\(\s*""\.(?:init_array|preinit_array|fini_array)",
        RegexOptions.Compiled);
    private static readonly Regex NameBeforeAttr = new( // trailing:  name / name(...) / name[...]  __attribute__
        @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\]|\([^()]*\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex NameAfterAttr = new(  // leading:   __attribute__ ... name( / name[ / name =
        @"^\s*(?:[A-Za-z_][\w*]*[\s*]+)*?([A-Za-z_]\w*)\s*(?:[\(\[=;,]|$)", RegexOptions.Compiled);

    private static readonly Regex AsmKeyword = new(@"\b(?:__asm__|__asm|asm)\b", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    // Function names used as DATA — global/array/struct initializers (vector tables, dispatch tables,
    // hooks structs, function-pointer registries). Structural on purpose (only initializer contexts) so
    // it never mistakes a prototype/extern declaration for a reference. For an @il list we scan its span
    // for identifiers, which also catches entries split across #if branches without an (illegal) nested
    // preproc query pattern.
    private const string InitRefQuery = """
        (initializer_list) @il
        (init_declarator value: (identifier) @ref)
        (initializer_pair value: (identifier) @ref)
        """;

    // File-scope INITIALIZED ARRAY globals — lookup tables, S-boxes, string/dispatch tables. That is
    // where the size win lives, and restricting to arrays (not scalars) is a safety measure: it avoids
    // mistaking a scalar LOCAL of a mis-parsed function (e.g. `int exclusive = 0;` inside a function
    // whose macro-prefixed signature tree-sitter didn't recognise) for a prunable global. Only
    // initialized ones are captured; uninitialized/extern/tentative declarations are left untouched.
    // Control-flow / type keywords are never real definition names. In #ifdef-heavy code tree-sitter
    // can mis-parse `if (...)` / `while (...)` as a function_declarator named "if"/"while"; capturing
    // those creates phantom functions that steal call attribution and get wrongly pruned.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "switch", "do", "return", "break", "continue", "goto", "case",
        "default", "sizeof", "typedef", "static", "const", "struct", "union", "enum", "void", "int",
        "char", "short", "long", "float", "double", "signed", "unsigned", "register", "volatile",
        "extern", "auto", "inline", "restrict", "_Static_assert", "static_assert",
    };

    private const string GlobalQuery = """
        (declaration declarator: (init_declarator declarator: (array_declarator declarator: (identifier) @global)))
        """;

    private readonly Language _lang;
    private readonly Query _defs;
    private readonly Query _calls;
    private readonly Query _idents;
    private readonly Query _initRefs;
    private readonly Query _globals;

    private readonly List<string> _warnings = new();
    /// <inheritdoc/>
    public IReadOnlyList<string> Warnings => _warnings;

    private IReadOnlyList<(NodeId From, string Name)> _callSites = Array.Empty<(NodeId, string)>();
    /// <summary>Every call site found in the last build as (enclosing node, callee name), INCLUDING calls
    /// that didn't resolve to a defined function. The post-carve soundness gate uses these to check that
    /// no kept function calls an in-scope function that was carved out (which wouldn't link).</summary>
    public IReadOnlyList<(NodeId From, string Name)> CallSites => _callSites;

    /// <summary>Per-file parse budget (ms). A file whose parse exceeds it is kept whole (see
    /// <see cref="LooksLikeIncludeFragment"/>) — a backstop against tree-sitter's super-linear error
    /// recovery on invalid #include fragments stalling a whole run. Only applied to files big enough to
    /// plausibly stall (below that they parse near-instantly even when invalid). 0 disables the budget.</summary>
    public int ParseBudgetMs { get; set; } = 20_000;
    private const int BudgetMinBytes = 256 * 1024;

    protected TreeSitterFrontEnd(string grammarLib, string grammarFn, string defsQuery, string callsQuery)
    {
        _lang = new Language(grammarLib, grammarFn);
        _defs = new Query(_lang, defsQuery);
        _calls = new Query(_lang, callsQuery);
        _idents = new Query(_lang, IdentQuery);
        _initRefs = new Query(_lang, InitRefQuery);
        _globals = new Query(_lang, GlobalQuery);
    }

    /// <summary>
    /// Build the dependency graph for the given files. When <paramref name="defines"/> is supplied, the
    /// preprocessor conditionals are resolved against it and code in dead <c>#ifdef</c> branches is
    /// ignored — a config-specific (tighter) carve. With no defines, every branch is kept (safe).
    /// </summary>
    public CodeGraph BuildGraph(IEnumerable<(string Path, string Text)> files, MacroTable? defines = null,
                                bool closedWorldDefines = false)
    {
        _warnings.Clear();
        var graph = new CodeGraph();
        var inputs = files.ToList();

        var fileNodeByPath = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var pathsByBasename = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in inputs)
        {
            fileNodeByPath[path] = graph.GetOrAddNode(NodeKind.File, path);
            var bas = BaseName(path);
            if (!pathsByBasename.TryGetValue(bas, out var list))
                pathsByBasename[bas] = list = new List<string>();
            list.Add(path);
        }

        var functionsByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var macrosByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var globalsByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var pendingCalls = new List<(NodeId From, string Name)>();
        var pendingRefs = new List<(NodeId From, string Name)>();
        var pendingMacroRefs = new List<(NodeId From, string Name)>();
        var pendingPastes = new List<(NodeId Macro, PasteKind Kind, string Frag)>();

        var keepNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in inputs)
        {
            if (text.Length == 0) continue; // oversized/empty file: File node already registered; nothing to parse
            ProcessFile(graph, path, text, fileNodeByPath, pathsByBasename,
                        functionsByName, macrosByName, globalsByName, pendingCalls, pendingRefs,
                        pendingMacroRefs, pendingPastes, defines, closedWorldDefines);
            foreach (var n in ScanKeepAttributes(text)) keepNames.Add(n);
        }

        foreach (var (from, name) in pendingCalls)
            ResolveUse(graph, from, name, functionsByName, macrosByName, globalsByName, EdgeKind.Calls);
        foreach (var (from, name) in pendingRefs)
            ResolveUse(graph, from, name, functionsByName, macrosByName, globalsByName, EdgeKind.AddressTaken);
        // A macro that expands to a call/another macro reaches those — so a function or global used ONLY
        // through a macro body (e.g. `#define getSBox(n) sbox[n]`) is not lost when its callers are pruned.
        foreach (var (from, name) in pendingMacroRefs)
            ResolveUse(graph, from, name, functionsByName, macrosByName, globalsByName, EdgeKind.Calls);

        // Token-paste (##): a macro that builds a name like `arg ## Suffix` generates functions we can't
        // see statically. Conservatively link the macro to every function whose name matches the literal
        // fragment, so a name-generating macro (dispatch/handler tables) keeps its generated targets.
        foreach (var (macro, kind, frag) in pendingPastes)
        {
            if (frag.Length == 0) continue;
            LinkPaste(graph, macro, kind, frag, functionsByName, EdgeKind.Calls);
            LinkPaste(graph, macro, kind, frag, globalsByName, EdgeKind.References);
        }

        _callSites = pendingCalls; // retained for the post-carve soundness self-check (--verify)

        // Keep-attributes → implicit roots. Mark every function/global whose declaration carries a
        // constructor/destructor/used/retain or init-array-section attribute, so AttributeRootProvider
        // seeds them: they run/are-kept by the runtime or linker, not by any call we could trace.
        foreach (var name in keepNames)
        {
            if (functionsByName.TryGetValue(name, out var fns)) foreach (var id in fns) graph.AddFlag(id, NodeFlags.Keep);
            if (globalsByName.TryGetValue(name, out var gs)) foreach (var id in gs) graph.AddFlag(id, NodeFlags.Keep);
        }

        return graph;
    }

    /// <summary>
    /// Identifiers appearing inside inline-asm blocks (<c>asm("bl helper")</c>, <c>asm(".word my_isr")</c>).
    /// A symbol reached ONLY from inline asm has no C-level edge, so a from-main closure drops it and the
    /// link/behaviour breaks. Over-approximates (every identifier in the block, incl. mnemonics/registers);
    /// non-matching ones resolve to nothing and are harmless. Bounded: scans each asm parenthesis group.
    /// </summary>
    private static IEnumerable<string> ScanAsmIdentifiers(string text)
    {
        foreach (Match m in AsmKeyword.Matches(text))
        {
            var i = m.Index + m.Length;
            while (i < text.Length && text[i] != '(' && text[i] != ';' && text[i] != '{' && text[i] != '}') i++;
            if (i >= text.Length || text[i] != '(') continue;
            var start = i;
            var depth = 0;
            for (; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) { i++; break; }
            }
            foreach (Match id in Identifier.Matches(text[start..i]))
                yield return id.Value;
        }
    }

    /// <summary>Names decorated with a keep-attribute (constructor/destructor/used/retain/init-array
    /// section), resolving the decorated symbol whether the attribute leads or trails the declaration.</summary>
    private static IEnumerable<string> ScanKeepAttributes(string text)
    {
        foreach (Match m in AttrBlock.Matches(text))
        {
            if (!KeepKeyword.IsMatch(m.Groups["body"].Value)) continue;
            var before = text.AsSpan(0, m.Index);
            var mb = NameBeforeAttr.Match(before.Length > 200 ? before[^200..].ToString() : before.ToString());
            if (mb.Success) { yield return mb.Groups[1].Value; continue; }
            var afterStart = m.Index + m.Length;
            var after = text.AsSpan(afterStart);
            var ma = NameAfterAttr.Match(after.Length > 200 ? after[..200].ToString() : after.ToString());
            if (ma.Success) yield return ma.Groups[1].Value;
        }
    }

    /// <summary>
    /// Parse <paramref name="text"/>, but for a file big enough to plausibly trigger tree-sitter's
    /// super-linear error recovery, do the parse on a throwaway background thread and abandon it if it
    /// blows <see cref="ParseBudgetMs"/>. Only the parse runs on the worker (no shared state), so an
    /// abandoned thread — stuck in native error recovery until the process exits — can never corrupt the
    /// graph. Small files (the vast majority) parse inline; they finish near-instantly even when invalid.
    /// </summary>
    private Tree? ParseWithBudget(string text, out bool timedOut)
    {
        timedOut = false;
        if (ParseBudgetMs <= 0 || text.Length < BudgetMinBytes)
        {
            using var parser = new Parser(_lang);
            return parser.Parse(text);
        }

        Tree? result = null;
        var worker = new Thread(() =>
        {
            var parser = new Parser(_lang); // not `using`: if abandoned, let the process teardown reclaim it
            result = parser.Parse(text);
            parser.Dispose();
        }) { IsBackground = true, Name = "ts-parse-budget" };
        worker.Start();
        if (worker.Join(ParseBudgetMs)) return result; // Join true => happens-before on `result`
        timedOut = true;
        return null; // abandon the worker; it touches no shared state
    }

    /// <summary>
    /// Cheap, streaming heuristic for an #include DATA fragment: a file dominated by lines of bare
    /// numeric/char literals and separators (a byte table, an X-macro-free constant list) with no
    /// declaration or preprocessor structure. Such a file is not valid stand-alone C, so parsing it is
    /// both pointless (nothing to carve) and dangerous (error-recovery blowup). Conservative: needs a
    /// meaningful line count and a high data-line ratio, so ordinary code never trips it.
    /// </summary>
    internal static bool LooksLikeIncludeFragment(string text)
    {
        int total = 0, data = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n') continue;
            var line = text.AsSpan(start, i - start).Trim();
            start = i + 1;
            if (line.Length == 0) continue;
            if (line[0] == '#') return false;                    // any preprocessor line => real header
            if (line.StartsWith("//") || line.StartsWith("/*") || line[0] == '*') continue; // comment
            total++;
            if (IsDataLine(line)) data++;
        }
        return total >= 50 && data >= total * 0.9;
    }

    /// <summary>A .c/.cc/.cpp/.cxx/.c++ source file — a translation unit we must always parse.</summary>
    private static bool IsTranslationUnit(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0) return false;
        var ext = path[dot..];
        return ext.Equals(".c", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".c++", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A line of only numeric/char literals + separators (no identifier, no <c>;</c>).</summary>
    private static bool IsDataLine(ReadOnlySpan<char> line)
    {
        var sawDigit = false;
        foreach (var c in line)
        {
            if (char.IsAsciiDigit(c)) { sawDigit = true; continue; }
            if (char.IsAsciiLetter(c))
            {
                if ("abcdefABCDEFxXuUlL".IndexOf(c) < 0) return false; // a real identifier char => not data
                continue;
            }
            if (c is ' ' or '\t' or ',' or '.' or '+' or '-' or '|' or '&' or '(' or ')'
                  or '<' or '>' or '{' or '}' or '\'' or '"' or '\\' or '~' or '^' or '*') continue;
            return false; // a `;`, `=`, `:` etc. — declaration/statement structure, not a bare data list
        }
        return sawDigit;
    }

    private static void ResolveUse(CodeGraph graph, NodeId from, string name,
                                   Dictionary<string, List<NodeId>> functionsByName,
                                   Dictionary<string, List<NodeId>> macrosByName,
                                   Dictionary<string, List<NodeId>> globalsByName,
                                   EdgeKind functionEdge)
    {
        if (functionsByName.TryGetValue(name, out var fns))
        {
            foreach (var target in fns)
            {
                graph.AddEdge(from, target, functionEdge);
                if (functionEdge == EdgeKind.AddressTaken)
                    graph.AddFlag(target, NodeFlags.AddressTaken);
            }
        }
        else if (macrosByName.TryGetValue(name, out var macros))
        {
            foreach (var target in macros)
                graph.AddEdge(from, target, EdgeKind.Expands);
        }
        else if (globalsByName.TryGetValue(name, out var globals))
        {
            foreach (var target in globals)
                graph.AddEdge(from, target, EdgeKind.References);
        }
    }

    private void ProcessFile(CodeGraph graph, string path, string text,
                             Dictionary<string, NodeId> fileNodeByPath,
                             Dictionary<string, List<string>> pathsByBasename,
                             Dictionary<string, List<NodeId>> functionsByName,
                             Dictionary<string, List<NodeId>> macrosByName,
                             Dictionary<string, List<NodeId>> globalsByName,
                             List<(NodeId, string)> pendingCalls,
                             List<(NodeId, string)> pendingRefs,
                             List<(NodeId, string)> pendingMacroRefs,
                             List<(NodeId, PasteKind, string)> pendingPastes,
                             MacroTable? defines,
                             bool closedWorldDefines)
    {
        // Finding A: some headers are #include fragments (e.g. a bare byte list pasted inside an array
        // initializer) — valid in context, invalid alone. tree-sitter's error recovery on them is
        // super-linear (a 2.4 MB blob can stall for minutes). Detect the obvious data-fragment shape and
        // keep it whole without parsing; a per-file budget backstops anything the heuristic misses. Either
        // way the file is still kept via #include-closure (its File node is already registered).
        // Gate the fragment heuristic to non-.c files: a header kept-whole is always sound (#include-
        // closure keeps it), but skipping a translation unit would lose its symbols. TUs always parse
        // (the parse budget still backstops a pathological one).
        if (!IsTranslationUnit(path) && LooksLikeIncludeFragment(text))
        {
            _warnings.Add($"{path}: looks like an #include data fragment (not valid stand-alone C) — kept whole, not carved");
            return;
        }

        using var tree = ParseWithBudget(text, out var timedOut);
        if (timedOut)
        {
            _warnings.Add($"{path}: parse exceeded the {ParseBudgetMs} ms budget — kept whole, not carved");
            return;
        }
        if (tree is null) return;
        var root = tree.RootNode;
        var fileNode = fileNodeByPath[path];
        var srcLines = text.Split('\n');

        // When configured, code in dead #ifdef branches is skipped (config-specific carve).
        var dead = defines is null ? null : PreprocessorScanner.DeadLineMap(text, defines, closedWorldDefines);
        bool IsDead(TsNode n) => dead is not null && n.StartPosition.Row + 1 is var ln && ln < dead.Length && dead[ln];

        var funcSpans = new List<(int Start, int End, NodeId Id)>();
        var defNamePositions = new HashSet<(int, int)>();

        // Pass 1: definitions. A "function" capture is any callable (free function or method); anything
        // else that isn't a macro is treated as a type (class/struct/enum/namespace/typedef).
        foreach (var cap in _defs.Execute(root).Captures)
        {
            var node = cap.Node;
            if (IsDead(node)) continue;
            var name = node.Text;
            var row = node.StartPosition.Row + 1;

            if (cap.Name == "function")
            {
                if (Keywords.Contains(name)) continue; // phantom `if`/`while` from mis-parsed #ifdef code
                // Only real definitions (with a body) become function nodes. DefinitionSpan returns
                // null for prototypes and function-pointer declarators, which we skip — capturing
                // those would create bodiless nodes and (worse) leave uncaptured real definitions.
                var span = DefinitionSpan(node);
                if (span is null) continue;
                defNamePositions.Add((node.StartPosition.Row, node.StartPosition.Column));
                var fid = graph.GetOrAddNode(NodeKind.Function, name, path, new SourceSpan(span.Value.Start, span.Value.End));
                graph.AddEdge(fid, fileNode, EdgeKind.DefinedIn);
                Add(functionsByName, name, fid);
                funcSpans.Add((span.Value.Start, span.Value.End, fid));
            }
            else if (cap.Name == "macro")
            {
                var mid = graph.GetOrAddNode(NodeKind.Macro, name, path, new SourceSpan(row, row));
                graph.AddEdge(mid, fileNode, EdgeKind.DefinedIn);
                Add(macrosByName, name, mid);
                var defineText = node.Parent?.Text ?? "";
                foreach (var id in MacroBodyIdentifiers(defineText))
                    pendingMacroRefs.Add((mid, id));
                foreach (var paste in ExtractMacroPastes(defineText))
                    pendingPastes.Add((mid, paste.Kind, paste.Frag));
            }
            else // class / struct / enum / namespace / type
            {
                var tid = graph.GetOrAddNode(NodeKind.Type, name, path, new SourceSpan(row, row));
                graph.AddEdge(tid, fileNode, EdgeKind.DefinedIn);
            }
        }

        // Pass 1b: file-scope INITIALIZED globals (data tables). Span = the whole declaration, so a big
        // lookup table can be removed wholesale when nothing reachable references it.
        foreach (var cap in _globals.Execute(root).Captures)
        {
            var node = cap.Node;
            if (IsDead(node)) continue;
            if (InsideFunctionBody(node)) continue; // a LOCAL variable, not a file-scope global
            var span = GlobalDeclarationSpan(node);
            if (span is null) continue;
            var gid = graph.GetOrAddNode(NodeKind.Global, node.Text, path, new SourceSpan(span.Value.Start, span.Value.End));
            graph.AddEdge(gid, fileNode, EdgeKind.DefinedIn);
            Add(globalsByName, node.Text, gid);
        }

        // Pass 2: #includes → file/header edges. Scanned from TEXT, not the parse tree: #include is a
        // line-oriented preprocessor directive, and tree-sitter misses ones in odd positions — notably
        // `#include "blob.h"` INSIDE an array initializer (the data-fragment pattern). A text scan catches
        // them all, so a needed fragment header is never silently dropped (would break the emitted build).
        for (var li = 0; li < srcLines.Length; li++)
        {
            if (dead is not null && li + 1 < dead.Length && dead[li + 1]) continue;
            var m = IncludeLine.Match(srcLines[li]);
            if (!m.Success) continue;
            var target = BaseName(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (!pathsByBasename.TryGetValue(target, out var targets)) continue;
            foreach (var tp in targets)
                if (tp != path)
                    graph.AddEdge(fileNode, fileNodeByPath[tp], EdgeKind.Includes);
        }

        // Pass 2b: symbol aliases. `void NMI_Handler(void) __attribute__((alias("Default_Handler")))` is a
        // bodyless declaration tree-sitter treats as a prototype (uncaptured), yet it IS a real symbol the
        // vector table points at — and it REQUIRES its target. Register the alias name as a function and
        // link it to the target, so a reference to the alias (e.g. from the vector table) keeps the target.
        foreach (Match m in AliasAttr.Matches(text))
        {
            var name = m.Groups["name"].Value;
            if (Keywords.Contains(name)) continue;
            var ln = 1;
            for (var k = 0; k < m.Index && k < text.Length; k++) if (text[k] == '\n') ln++;
            var aid = graph.GetOrAddNode(NodeKind.Function, name, path, new SourceSpan(ln, ln));
            graph.AddEdge(aid, fileNode, EdgeKind.DefinedIn);
            Add(functionsByName, name, aid);
            pendingCalls.Add((aid, m.Groups["target"].Value)); // alias -> target: keeping the alias keeps it
        }

        // Pass 2c: inline-asm symbol references. A function/global named only inside asm("...") is kept
        // via the file (attributed to fileNode, like a file-scope address-take), so it survives if this
        // translation unit is kept. Sound over-approximation for the inline-asm blind spot.
        foreach (var id in ScanAsmIdentifiers(text))
            pendingRefs.Add((fileNode, id));

        // Pass 3: direct calls, attributed to the enclosing function by span.
        var calleePositions = new HashSet<(int, int)>();
        foreach (var cap in _calls.Execute(root).Captures)
        {
            if (IsDead(cap.Node)) continue;
            calleePositions.Add((cap.Node.StartPosition.Row, cap.Node.StartPosition.Column));
            // A call not inside any captured function is inside a function we failed to parse (e.g. an
            // unusual macro-prefixed / bare-typedef-return declaration like zlib's `local gzFile gz_open`
            // or `void ZLIB_INTERNAL _tr_flush_block`). That function stays in the emitted file, so keep
            // its callees whenever the file is kept — attribute the call to the file node.
            var from = EnclosingFunction(funcSpans, cap.Node.StartPosition.Row + 1) ?? fileNode;
            pendingCalls.Add((from, cap.Node.Text));
        }

        // Pass 4: non-call references INSIDE functions (address-taken: a callback passed/assigned).
        foreach (var cap in _idents.Execute(root).Captures)
        {
            if (IsDead(cap.Node)) continue;
            var pos = (cap.Node.StartPosition.Row, cap.Node.StartPosition.Column);
            if (defNamePositions.Contains(pos) || calleePositions.Contains(pos)) continue;
            var from = EnclosingFunction(funcSpans, cap.Node.StartPosition.Row + 1);
            if (from is { } f)
                pendingRefs.Add((f, cap.Node.Text));
        }

        // Pass 5: function names used as DATA at FILE SCOPE (tables, hooks, registries). Attributed to
        // the file node so keeping the file keeps everything its initializers point at. For a whole
        // initializer_list we scan its span (catching #if-split entries); single `= fn` values match
        // the @ref patterns directly.
        foreach (var cap in _initRefs.Execute(root).Captures)
        {
            if (IsDead(cap.Node)) continue;
            var startRow = cap.Node.StartPosition.Row + 1;
            if (EnclosingFunction(funcSpans, startRow) is not null) continue; // file scope only

            if (cap.Name == "il")
            {
                // Scan only what's INSIDE the braces (using the node's columns), so the declaration
                // prefix on the first line (`static T name[] = {`) isn't read — otherwise the variable's
                // own name would count as a reference to itself and it could never be pruned.
                var startCol = cap.Node.StartPosition.Column;
                var endRow = cap.Node.EndPosition.Row + 1;
                var endCol = cap.Node.EndPosition.Column;
                for (var r = startRow; r <= endRow && r - 1 < srcLines.Length; r++)
                {
                    var line = srcLines[r - 1];
                    if (line.TrimStart().StartsWith('#')) continue;             // skip #if/#endif lines
                    if (dead is not null && r < dead.Length && dead[r]) continue; // skip dead entries
                    var from = r == startRow ? Math.Min(startCol, line.Length) : 0;
                    var to = r == endRow ? Math.Min(endCol, line.Length) : line.Length;
                    if (from < to)
                        foreach (var id in LineIdentifiers(line[from..to]))
                            pendingRefs.Add((fileNode, id));
                }
            }
            else
            {
                pendingRefs.Add((fileNode, cap.Node.Text));
            }
        }
    }

    private static void Add(Dictionary<string, List<NodeId>> map, string name, NodeId id)
    {
        if (!map.TryGetValue(name, out var list))
            map[name] = list = new List<NodeId>();
        list.Add(id);
    }

    /// <summary>Link a token-paste macro to the functions/globals matching its literal fragment.</summary>
    private static void LinkPaste(CodeGraph graph, NodeId macro, PasteKind kind, string frag,
                                  Dictionary<string, List<NodeId>> map, EdgeKind edge)
    {
        if (kind == PasteKind.Exact)
        {
            if (map.TryGetValue(frag, out var exact))
                foreach (var n in exact) graph.AddEdge(macro, n, edge);
            return;
        }
        foreach (var kv in map)
        {
            var hit = kind == PasteKind.Suffix
                ? kv.Key.EndsWith(frag, StringComparison.Ordinal)
                : kv.Key.StartsWith(frag, StringComparison.Ordinal);
            if (hit)
                foreach (var n in kv.Value) graph.AddEdge(macro, n, edge);
        }
    }

    /// <summary>C identifiers appearing in a line of source (used to scan initializer-table bodies).</summary>
    private static IEnumerable<string> LineIdentifiers(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                yield return line[start..i];
            }
            else i++;
        }
    }

    /// <summary>The identifiers appearing in a macro's replacement body — used to reach functions/macros
    /// invoked only through the macro. Skips the <c>#define</c>, the macro name, and (for function-like
    /// macros) the parameter list; parameter names and unrelated tokens simply resolve to nothing.</summary>
    private static IEnumerable<string> MacroBodyIdentifiers(string defineText)
    {
        var s = defineText;
        var i = s.IndexOf('#');
        if (i < 0) yield break;
        i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;   // skip "define"
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++; // skip macro name
        if (i < s.Length && s[i] == '(')                        // function-like: skip balanced params
        {
            var depth = 0;
            while (i < s.Length)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') { depth--; if (depth == 0) { i++; break; } }
                i++;
            }
        }
        while (i < s.Length)
        {
            if (char.IsLetter(s[i]) || s[i] == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                yield return s[start..i];
            }
            else i++;
        }
    }

    private enum PasteKind { Suffix, Prefix, Exact }

    /// <summary>Token-paste (##) fragments in a macro body: `arg ## Suffix` -> keep functions ending
    /// with Suffix; `Prefix ## arg` -> starting with Prefix; `lit ## lit` -> the exact concatenation.</summary>
    private static IEnumerable<(PasteKind Kind, string Frag)> ExtractMacroPastes(string defineText)
    {
        var s = defineText;
        var i = s.IndexOf('#');
        if (i < 0) yield break;
        i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;   // skip "define"
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++; // skip name

        var pars = new HashSet<string>(StringComparer.Ordinal);
        if (i < s.Length && s[i] == '(')
        {
            i++;
            var start = i;
            var depth = 1;
            while (i < s.Length && depth > 0) { if (s[i] == '(') depth++; else if (s[i] == ')') depth--; if (depth > 0) i++; }
            foreach (var p in s[start..Math.Min(i, s.Length)].Split(','))
            {
                var t = p.Trim();
                if (t.Length > 0 && t != "...") pars.Add(t);
            }
            if (i < s.Length) i++; // skip ')'
        }

        var toks = MacroTokens(s, i);
        for (var k = 0; k < toks.Count; k++)
        {
            if (toks[k] != "##") continue;
            var left = k - 1 >= 0 ? toks[k - 1] : null;
            var right = k + 1 < toks.Count ? toks[k + 1] : null;
            if (left is null || right is null || left == "##" || right == "##") continue;
            var lp = pars.Contains(left);
            var rp = pars.Contains(right);
            if (lp && !rp) yield return (PasteKind.Suffix, right);
            else if (!lp && rp) yield return (PasteKind.Prefix, left);
            else if (!lp && !rp) yield return (PasteKind.Exact, left + right);
        }
    }

    private static List<string> MacroTokens(string s, int start)
    {
        var toks = new List<string>();
        var i = start;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsLetter(c) || c == '_')
            {
                var b = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                toks.Add(s[b..i]);
            }
            else if (c == '#' && i + 1 < s.Length && s[i + 1] == '#') { toks.Add("##"); i += 2; }
            else i++;
        }
        return toks;
    }

    private static string BaseName(string path)
    {
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string Unquote(string raw) =>
        raw.Length >= 2 && (raw[0] == '"' || raw[0] == '<') ? raw[1..^1] : raw;

    /// <summary>
    /// The line span of the function DEFINITION a captured name belongs to, or null if the name is not
    /// a definition's own name. Walks up from the name through only the node types that legitimately
    /// wrap a definition's declarator — the function_declarator itself, pointer/reference returns, and
    /// C++ qualified/template names — until it reaches the function_definition. Any other parent
    /// (a declaration/prototype, a parameter list, a class field declaration, a typedef) means this is
    /// not a definition, so we return null and skip it. This is what makes pointer-returning functions
    /// (<c>T *f()</c>) get captured while prototypes and function-pointer parameters do not.
    /// </summary>
    private static (int Start, int End)? DefinitionSpan(TsNode nameNode)
    {
        var n = nameNode;
        for (var i = 0; i < 12; i++)
        {
            var parent = n.Parent;
            if (parent is null) return null;
            var t = parent.Type;
            if (t == "function_definition")
            {
                // A function_definition nested inside another body whose "parameter list" is really a
                // CALL's argument list is a MISPARSE — tree-sitter produces it when a macro-label statement
                // is followed by a call, e.g. wren's `CASE_CODE(CLOSE_UPVALUE): closeUpvalues(fiber,
                // fiber->stackTop - 1)`, parsing the call as a nested definition whose body is the next
                // block. That phantom steals the real call's attribution (the call lands in its span, not
                // the enclosing function) and leaves the true callee unreached → dropped → dangling.
                // Reject ONLY that shape: nested AND its params aren't real parameter_declarations. A real
                // top-level function that cascading error-recovery merely nested keeps a valid parameter
                // list, so it is still captured (crypto libraries with macro-heavy bodies rely on this).
                if (InsideFunctionBody(parent) && HasCallShapedParameters(parent)) return null;
                return (parent.StartPosition.Row + 1, parent.EndPosition.Row + 1);
            }
            if (t is "function_declarator" or "pointer_declarator" or "reference_declarator"
                  or "parenthesized_declarator" or "qualified_identifier" or "template_function")
            {
                n = parent;
                continue;
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// True if this function_definition's parameter list is really a CALL's argument list — the tell-tale
    /// of a call mis-parsed as a nested definition (e.g. <c>closeUpvalues(fiber, fiber->stackTop - 1)</c>).
    /// A genuine definition's parameters are <c>parameter_declaration</c>s (or empty/<c>void</c>/variadic);
    /// an argument list holds expressions/identifiers, or the subtree carries a parse ERROR. Conservative:
    /// only an unmistakable non-parameter child (or error) counts, so a real signature is never rejected.
    /// </summary>
    private static bool HasCallShapedParameters(TsNode funcDef)
    {
        TsNode? decl = ChildForField(funcDef, "declarator");
        for (var i = 0; i < 6 && decl is not null && decl.Type != "function_declarator"; i++)
            decl = ChildForField(decl, "declarator");
        if (decl is null || decl.Type != "function_declarator") return false; // can't tell — don't reject

        var plist = ChildForField(decl, "parameters");
        if (plist is null) return false;
        if (plist.IsError || plist.HasError) return true;
        foreach (var ch in plist.NamedChildren)
        {
            if (ch.IsExtra) continue; // comments
            if (ch.Type is not ("parameter_declaration" or "variadic_parameter"
                             or "optional_parameter_declaration")) return true;
        }
        return false;
    }

    private static TsNode? ChildForField(TsNode n, string field)
    {
        try { return n.GetChildForField(field); }
        catch { return null; }
    }

    /// <summary>True if a node sits inside a function body — a compound_statement or function_definition
    /// ancestor. Used to reject LOCAL variables from global capture; works even when a function's
    /// signature mis-parsed (its body braces still form a compound_statement).</summary>
    private static bool InsideFunctionBody(TsNode n)
    {
        var p = n.Parent;
        for (var i = 0; i < 48 && p is not null; i++)
        {
            var t = p.Type;
            if (t is "compound_statement" or "function_definition") return true;
            if (t == "translation_unit") return false;
            p = p.Parent;
        }
        return false;
    }

    /// <summary>The full declaration span of a file-scope global (walking up through the declarator
    /// wrappers to the declaration), so a whole `T name[...] = { ... };` — however many lines — is
    /// removed as a unit.</summary>
    private static (int Start, int End)? GlobalDeclarationSpan(TsNode nameNode)
    {
        var n = nameNode;
        for (var i = 0; i < 8; i++)
        {
            var parent = n.Parent;
            if (parent is null) return null;
            if (parent.Type == "declaration")
                return (parent.StartPosition.Row + 1, parent.EndPosition.Row + 1);
            n = parent;
        }
        return null;
    }

    private static NodeId? EnclosingFunction(List<(int Start, int End, NodeId Id)> spans, int row)
    {
        NodeId? best = null;
        var bestWidth = int.MaxValue;
        foreach (var (start, end, id) in spans)
        {
            if (row < start || row > end) continue;
            var width = end - start;
            if (width < bestWidth) { bestWidth = width; best = id; }
        }
        return best;
    }

    public void Dispose()
    {
        _globals.Dispose();
        _initRefs.Dispose();
        _idents.Dispose();
        _calls.Dispose();
        _defs.Dispose();
        _lang.Dispose();
    }
}
