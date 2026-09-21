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
    private const string IncludeQuery = "(preproc_include path: (_) @inc)";

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
    private readonly Query _includes;
    private readonly Query _initRefs;
    private readonly Query _globals;

    protected TreeSitterFrontEnd(string grammarLib, string grammarFn, string defsQuery, string callsQuery)
    {
        _lang = new Language(grammarLib, grammarFn);
        _defs = new Query(_lang, defsQuery);
        _calls = new Query(_lang, callsQuery);
        _idents = new Query(_lang, IdentQuery);
        _includes = new Query(_lang, IncludeQuery);
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

        foreach (var (path, text) in inputs)
        {
            if (text.Length == 0) continue; // oversized/empty file: File node already registered; nothing to parse
            ProcessFile(graph, path, text, fileNodeByPath, pathsByBasename,
                        functionsByName, macrosByName, globalsByName, pendingCalls, pendingRefs,
                        pendingMacroRefs, pendingPastes, defines, closedWorldDefines);
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

        return graph;
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
        using var parser = new Parser(_lang);
        using var tree = parser.Parse(text);
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

        // Pass 2: #includes → file/header edges.
        foreach (var cap in _includes.Execute(root).Captures)
        {
            if (IsDead(cap.Node)) continue;
            var target = BaseName(Unquote(cap.Node.Text));
            if (!pathsByBasename.TryGetValue(target, out var targets)) continue;
            foreach (var tp in targets)
                if (tp != path)
                    graph.AddEdge(fileNode, fileNodeByPath[tp], EdgeKind.Includes);
        }

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
                return (parent.StartPosition.Row + 1, parent.EndPosition.Row + 1);
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
        _includes.Dispose();
        _idents.Dispose();
        _calls.Dispose();
        _defs.Dispose();
        _lang.Dispose();
    }
}
