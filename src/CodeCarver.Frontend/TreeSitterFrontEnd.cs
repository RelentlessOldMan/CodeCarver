using System.Text;
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
    // Scope nodes for ScopeIndex (the C++ grammar adds for_range_loop).
    private const string ScopeQueryC = "(compound_statement) @block (for_statement) @for (function_definition) @fn (storage_class_specifier) @storage ";
    // Names a function declares for itself — parameters and locals (P3). An identifier inside the body that
    // refers to one of these is not a reference to a same-named function elsewhere.
    private const string LocalQuery = """
        (declaration declarator: (identifier) @local)
        (init_declarator declarator: (identifier) @local)
        (pointer_declarator declarator: (identifier) @local)
        (array_declarator declarator: (identifier) @local)
        (parameter_declaration declarator: (identifier) @local)
        """;

    /// <summary><c>#include "x"</c> or <c>#include &lt;x&gt;</c>, scanned from text (line-oriented) so it
    /// catches includes tree-sitter misses — e.g. inside an array initializer (the data-fragment case).</summary>
    private static readonly Regex IncludeLine = new(
        """^\s*#\s*include\s+(?:"([^"]+)"|<([^>]+)>)""", RegexOptions.Compiled);
    /// <summary><c>void h(void) __attribute__((weak, alias("target")))</c> — a GCC symbol alias. The
    /// aliasing name IS the target function, so without an edge to the target, dropping the target breaks
    /// the link. Ubiquitous in embedded startup, where every unused handler aliases a Default_Handler.</summary>
    private static readonly Regex AliasAttr = new(
        @"(?<name>[A-Za-z_]\w*)\s*\([^()]*\)\s*__attribute__\s*\(\(\s*[^()]*?\b(?:alias|ifunc)\s*\(\s*""(?<target>[A-Za-z_]\w*)""",
        RegexOptions.Compiled);
    // The other ways C text makes one name an alias of another (pass 2b):
    //   __attribute__((alias("impl"))) void hook(void);     #pragma weak hook = impl     __asm__(".set hook, impl")
    private static readonly Regex AliasAttrBefore = new(
        @"__attribute__\s*\(\([^;{}]*?\b(?:alias|ifunc)\s*\(\s*""(?<target>[A-Za-z_]\w*)""\s*\)[^;{}]*?\)\)[\w\s\*]*?\b(?<name>[A-Za-z_]\w*)\s*\([^(){};]*\)\s*;",
        RegexOptions.Compiled);
    private static readonly Regex PragmaWeakAlias = new(
        @"(?:^[ \t]*#[ \t]*pragma[ \t]+weak|\b_Pragma\s*\(\s*""\s*weak)[ \t]+(?<name>[A-Za-z_]\w*)[ \t]*=[ \t]*(?<target>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex AsmSetAlias = new(
        @"\.(?:set|equ|equiv)\s+(?<name>[A-Za-z_]\w*)\s*,\s*(?<target>[A-Za-z_]\w*)", RegexOptions.Compiled);
    // Body (after the macro's "(") of an alias macro: `f) __attribute__((weak, alias(#f)))`.
    private static readonly Regex AliasMacroBody = new(@"^\s*(\w+)\s*\)[^\n]*\balias\s*\(\s*#\s*\1\b", RegexOptions.Compiled);
    private Regex? _aliasMacroUse;
    // Identity wrappers `#define EXPORT(n) n` / `(n)`, as used on a definition's name: `int EXPORT(f)(void) {`.
    private static readonly Regex IdentityBody = new(@"^\s*(\w+)\s*\)\s*\(?\s*\1\s*\)?\s*$", RegexOptions.Compiled);
    private Regex? _wrapperUse;

    // Keep-attributes: symbols the runtime/linker keep regardless of any call — implicit roots a
    // from-main closure would silently drop (a self-registering `constructor`, an initcall-section entry).
    private static readonly Regex AttrBlock = new(
        @"__attribute(?:__)?\s*\(\((?<body>(?:[^()]|\([^()]*\))*)\)\)|\[\[\s*(?<body>gnu::(?:[^\[\]]|\[[^\]]*\])*)\]\]",
        RegexOptions.Compiled);
    private static readonly Regex KeepKeyword = new(
        @"\b(?:constructor|destructor|used|retain)\b|section\s*\(\s*""\.(?:init_array|preinit_array|fini_array)"
        // A section named like a C identifier: the linker defines __start_/__stop_ for it, so code can walk every entry
        // without naming one (a registration table).
        + @"|section\s*\(\s*""[A-Za-z_]\w*""\s*\)",
        RegexOptions.Compiled);
    // A macro placing what it defines in such a section: a file using it registers something nobody names.
    private static readonly Regex IdentifierSection = new(@"section\s*\(\s*""[A-Za-z_]\w*""\s*\)", RegexOptions.Compiled);
    private HashSet<string> _registerMacros = new(StringComparer.Ordinal);
    private static readonly Regex CommentOrSpace = new(@"/\*.*?\*/|//.*|\s+", RegexOptions.Compiled | RegexOptions.Singleline);
    // Object-like macros defined as `extern` in one place and as nothing in another (the EXTERN-header idiom).
    private readonly HashSet<string> _condExternMacros = new(StringComparer.Ordinal);
    // Function-like macros whose bodies call nothing.
    private readonly HashSet<string> _callFreeMacros = new(StringComparer.Ordinal);
    private static readonly Regex CallInBody = new(@"[A-Za-z_]\w*\s*\(", RegexOptions.Compiled);
    // Variables a header defines (not just declares): defined in each translation unit that includes it.
    private readonly List<(NodeId Id, string Header, bool CondExtern)> _headerData = new();
    // Per object-like macro defined EMPTY, the names the #if conditions around that definition test; and, for the
    // EXTERN-style macros, all of them: the flags a unit #defines (or gets by -D) to turn EXTERN off.
    private readonly Dictionary<string, HashSet<string>> _emptyDefineConditions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _condExternFlags = new(StringComparer.Ordinal);
    // Translation units that #define one of those flags themselves, or get it from their compile command.
    private readonly HashSet<string> _definesExternFlag = new(StringComparer.Ordinal);
    private static readonly Regex ConditionalLine = new(@"^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b(.*)$", RegexOptions.Compiled);
    private static readonly Regex EmptyDefineLine = new(@"^\s*#\s*define\s+([A-Za-z_]\w*)\s*(?:/[*/].*)?$", RegexOptions.Compiled);
    private static readonly Regex DefineName = new(@"^\s*#\s*define\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    /// <summary>For each macro this text #defines as nothing, the identifiers of the #if conditions enclosing it
    /// (<c>#ifdef DEFINE_GLOBALS / #define EXTERN</c>: DEFINE_GLOBALS).</summary>
    private void EmptyDefineConditions(string text)
    {
        var stack = new List<List<string>>();
        foreach (var line in text.Split('\n'))
        {
            if (line.IndexOf('#') < 0) continue;
            if (ConditionalLine.Match(line) is { Success: true } c)
            {
                var ids = Identifier.Matches(c.Groups[2].Value).Select(m => m.Value).Where(v => v != "defined").ToList();
                switch (c.Groups[1].Value)
                {
                    case "if" or "ifdef" or "ifndef": stack.Add(ids); break;
                    case "elif" when stack.Count > 0: stack[^1].AddRange(ids); break;
                    case "endif" when stack.Count > 0: stack.RemoveAt(stack.Count - 1); break;
                }
                continue;
            }
            if (stack.Count == 0 || EmptyDefineLine.Match(line) is not { Success: true } d) continue;
            if (!_emptyDefineConditions.TryGetValue(d.Groups[1].Value, out var set))
                _emptyDefineConditions[d.Groups[1].Value] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var frame in stack) set.UnionWith(frame);
        }
    }
    private static readonly Regex NameBeforeAttr = new( // trailing:  name / name(...) / name[...]  __attribute__
        @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\]|\([^()]*\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex NameAfterAttr = new(  // leading:   __attribute__ ... name( / name[ / name =
        @"^\s*(?:[A-Za-z_][\w*]*[\s*]+)*?([A-Za-z_]\w*)\s*(?:[\(\[=;,]|$)", RegexOptions.Compiled);

    private static readonly Regex AsmLabelDecl = new(
        @"\b(?<name>[A-Za-z_]\w*)\s*\([^(){};]*\)\s*(?:__asm__|__asm|asm)\s*\(\s*""(?<sym>[A-Za-z_.$][\w.$]*)""\s*\)", RegexOptions.Compiled);
    // `#pragma redefine_extname old new`: from the first declaration of old on, its symbol is new (an asm label by pragma).
    private static readonly Regex RedefineExtname = new(
        @"^[ \t]*#[ \t]*pragma[ \t]+redefine_extname[ \t]+(?<name>[A-Za-z_]\w*)[ \t]+(?<sym>[A-Za-z_.$][\w.$]*)", RegexOptions.Compiled | RegexOptions.Multiline);
    // Header functions that are C99 inline definitions (see C99Inline): the translation unit declaring one extern emits it.
    private readonly HashSet<string> _c99InlineFns = new(StringComparer.Ordinal);
    /// <summary>Header functions that are C99 inline definitions (no symbol of their own; verify reads this).</summary>
    public IReadOnlySet<string> C99InlineFunctions => _c99InlineFns;

    /// <summary>The build's command-line macros that rename what a file defines (see <see cref="CommandLineMacros"/>).</summary>
    public IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? CommandLineMacros { get; set; }

    private static readonly Regex AsmKeyword =new(@"\b(?:__asm__|__asm|asm)\b", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    // Function names used as DATA — global/array/struct initializers (vector tables, dispatch tables,
    // hooks structs, function-pointer registries). Structural on purpose (only initializer contexts) so
    // it never mistakes a prototype/extern declaration for a reference. For an @il list we scan its span
    // for identifiers, which also catches entries split across #if branches without an (illegal) nested
    // preproc query pattern.
    private const string InitRefQuery = """
        (initializer_list) @il
        (init_declarator value: (identifier) @ref)
        (init_declarator value: (call_expression) @il)
        (initializer_pair value: (identifier) @ref)
        (init_declarator value: [(pointer_expression) (cast_expression) (unary_expression) (binary_expression)
                                 (conditional_expression) (parenthesized_expression) (field_expression)
                                 (subscript_expression) (compound_literal_expression)] @il)
        """;
    // C++ only: `T obj(&handler);`, `= ns::fn`, `= new T(...)`.
    private const string InitRefQueryCpp = """
        (init_declarator value: [(argument_list) (qualified_identifier) (new_expression)] @il)
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

    // Every declarator of every declaration; GlobalName keeps the file-scope variable definitions among them.
    private const string GlobalQuery = """
        (declaration declarator: (_) @global)
        """;

    private readonly Language _lang;
    private readonly Query _defs;
    private readonly Query _calls;
    private readonly Query _idents;
    private readonly Query _initRefs;
    private readonly Query _globals;
    private readonly Query _locals;
    private readonly Query _scopes;
    // Functions with internal linkage (`static` at file scope in a .c/.cpp): only their own translation unit
    // can call them (P2). Filled while parsing, consulted when uses are resolved.
    private readonly HashSet<NodeId> _fileLocal = new();
    // Reused across the sequential parse loop: a native TSParser is cheap to re-use but not free to
    // create/destroy, and BuildGraph parses thousands of files one at a time on this thread. The budgeted
    // (large-file) path still uses its OWN parser on the worker thread — this instance is single-threaded,
    // touched only by the inline path. Lazily created; disposed with the front-end.
    private Parser? _inlineParser;

    private readonly List<string> _warnings = new();
    // Files kept whole without extraction (fragment, parse timeout, symbol budget, extraction failure).
    private readonly HashSet<string> _unparsed = new(StringComparer.Ordinal);

    private int _recoveredByScan;
    /// <summary>Function definitions a parse with errors missed and the token-scanner backstop defined (pass 1d).</summary>
    public int DefinitionsRecoveredByScan => _recoveredByScan;
    // CODECARVER_TIMING: where the per-file parse time goes (Stopwatch ticks), and how many files parsed with errors.
    private long _tPrep, _tParse, _tScan, _tRead; private int _errorFiles;
    private readonly SortedDictionary<string, long> _timing = new(StringComparer.Ordinal);

    /// <summary>Where the last graph build's time went, in ms, plus the slow-file counts: numbers only, for summary.txt.</summary>
    public IReadOnlyDictionary<string, long> Timing => _timing;

    /// <summary>Files the front-end read but kept whole without extracting definitions or uses.</summary>
    public IReadOnlyCollection<string> UnparsedFiles => _unparsed;

    /// <summary>
    /// A file kept whole without extraction is still emitted whole, so whatever it uses must stay: every distinct
    /// identifier in it becomes a reference from its file node (resolved by name like any use; most match nothing).
    /// Before, such a file contributed no uses at all, and a function only it called was dropped while it was
    /// written — a carve that does not link.
    /// </summary>
    private void KeptWholeRefs(string path, string text, NodeId fileNode, UseList pendingRefs, bool callShapedOnly = false)
    {
        _unparsed.Add(path);
        IdentifierRefs(text, fileNode, pendingRefs, callShapedOnly);
    }

    private static readonly Regex CallShaped = new(@"\b([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>The "every name in this file is a use" rule shared by reference-only includes and files kept whole
    /// without extraction. <paramref name="callShapedOnly"/> limits it to names followed by '(' — calls and
    /// function-like macro uses — for a file over the symbol budget, whose hundreds of thousands of field and
    /// register names would otherwise flood the graph and keep every same-named function in the tree.</summary>
    private static void IdentifierRefs(string text, NodeId fileNode, UseList pendingRefs, bool callShapedOnly)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in callShapedOnly ? CallShaped.Matches(text) : Identifier.Matches(text))
        {
            var name = callShapedOnly ? m.Groups[1].Value : m.Value;
            if (seen.Add(name) && !Keywords.Contains(name)) pendingRefs.Add((fileNode, name));
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _forceKeepFiles = new();
    private readonly HashSet<string> _forceKeepSet = new(StringComparer.Ordinal);
    private void ForceKeep(string path) { if (_forceKeepSet.Add(path)) _forceKeepFiles.Add(path); }
    private Func<string, string>? _read;
    // Headers with a definition whose name a macro computes (a template header: see TemplateInstances).
    private readonly HashSet<string> _macroNamedHeaders = new(StringComparer.Ordinal);
    /// <summary>Header text as TemplateInstances reads it (whole for a template header, directive lines otherwise), by path.</summary>
    private readonly Dictionary<string, string> _includeDirectives = new(StringComparer.Ordinal);
    /// <summary>Files whose extraction threw and were skipped (kept whole rather than crashing the run) —
    /// the CLI roots these so their code is emitted intact, since we couldn't analyse them.</summary>
    public IReadOnlyList<string> ForceKeepFiles => _forceKeepFiles;

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

    /// <summary>Per-file SYMBOL budget: the max number of definitions (functions + macros + types +
    /// globals) a single file may mint into the graph. A file that would exceed it is kept WHOLE instead
    /// of exploded into nodes/edges. This is the shape-AGNOSTIC backstop behind the content-specific
    /// dense-header skip and the byte cap: whatever the generated shape — a register header of tens of
    /// thousands of <c>#define</c>s that slipped the density ratio, a giant enum/inline-fn/X-macro table —
    /// a file that mints this many symbols blows the graph's node/edge memory (the real eval-#14 ~20 GB
    /// cause was one node per <c>#define</c>). Keeping it whole is always sound: nothing it defines is
    /// dropped (a TU is force-rooted; a header rides #include-closure). Checked from the PARSED tree
    /// BEFORE any node is minted, so the blow-up allocation never happens. 0 disables the budget.</summary>
    public int PerFileSymbolBudget { get; set; } = 50_000;

    /// <summary>Where progress notes (slow/big file) go. The CLI points it at its error writer; nothing
    /// writes to the process console directly, so in-process callers and tests capture it (review RB12).</summary>
    public TextWriter Log { get; set; } = TextWriter.Null;

    private readonly List<(string Path, int Symbols)> _symbolBudgetKeptWhole = new();
    /// <summary>Files kept whole because their definition count exceeded <see cref="PerFileSymbolBudget"/>
    /// — surfaced so the CLI can report/diag them, distinct from parse-failure <see cref="ForceKeepFiles"/>.</summary>
    public IReadOnlyList<(string Path, int Symbols)> SymbolBudgetKeptWhole => _symbolBudgetKeptWhole;

    // Scope-opening macros (`FMT_BEGIN_NAMESPACE`, `PUGI_IMPL_NS_BEGIN` → `namespace fmt {` …). tree-sitter
    // can't see through them, so a file that opens its scope with one mis-parses entirely and NO function
    // is captured — the C++ library carve can't even find its roots. We expand ONLY these (not value
    // macros) and ONLY for parsing, on a single line so every node's LINE number still maps to the
    // original text the emitter prunes. Built once across all inputs (the #define is often in a header).
    private static readonly Regex ObjectLikeDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]+((?:\\\r?\n|[^\r\n])*)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private Dictionary<string, string> _scopeMacros = new(StringComparer.Ordinal);
    private Regex? _scopeRegex;

    // Object-like macro whose body is EMPTY (`#define X` with no replacement) — an annotation/marker
    // macro that expands to nothing (calling-convention stubs, feature markers).
    private static readonly Regex ValuelessDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]*\r?$", RegexOptions.Compiled | RegexOptions.Multiline);
    // A macro body that is PURELY function specifier(s) — a LEADING slot (`inline`/`static` before the
    // return type, e.g. pugixml's PUGI_IMPL_FN -> inline) or a TRAILING slot (`) <here> {`, e.g.
    // PUGIXML_NOEXCEPT_IF_NOT_COMPACT -> noexcept). tree-sitter can't tell an UNDEFINED-looking macro
    // token from a return type, so `PUGI_IMPL_FN xml_parse_result xml_document::load_file(...)` (macro +
    // NON-primitive return + qualified name) mis-parses and the function isn't captured — while a
    // void-returning one parses because `void` is a keyword. Blanking a specifier can never make a
    // definition un-parseable; it only drops the specifier from the PARSE copy (emit is untouched).
    private static readonly Regex SpecifierMacroBody = new(
        @"^(?:\s*(?:inline|__inline|__forceinline|static|constexpr|consteval|constinit|explicit|virtual|friend|extern|noexcept(?:\s*\([^()]*\))?|throw\s*\(\s*\)|const|volatile|override|final|mutable))+$",
        RegexOptions.Compiled);
    // Object-like macros that expand to a specifier or to nothing — blanked (length-preserving) for
    // PARSING only. tree-sitter can't see through them, so `foo() PUGIXML_NOEXCEPT_IF_NOT_COMPACT {`
    // mis-parses and the function (plus several after it, via error recovery) is never captured and so
    // can't be rooted — the pugixml load_file/load_string gap. Emitted output is untouched.
    private Regex? _blankRegex;
    // The C grammar parses every file as C; the C++ grammar also reads .c files in a mixed tree. Implicit-int
    // recovery (ImplicitInt) applies to C only — C++ has no implicit int.
    private readonly bool _cGrammar;
    // FUNCTION-LIKE macro names (`#define NAME(...)`). A real function can't share a name with one (the
    // preprocessor would mangle its definition), so a "function" whose name is here is a misparse — a
    // macro invocation like fmt's `FMT_CATCH(...) {}` parsed as a definition. OBJECT-like rename macros
    // (`#define adler32 z_adler32`, zlib's Z_PREFIX) CAN coincide with a real function name, so those must
    // NOT reject the definition — doing so dropped adler32 and broke the link (caught by the map oracle).
    private HashSet<string> _funcLikeMacroNames = new(StringComparer.Ordinal);
    // Macros whose body places a symbol in a section or marks it used/retained/constructor (directly or via
    // another such macro): `#define INITCALL(fn) static void (*__init_##fn)(void) __attribute__((section(".initcalls"),
    // used)) = fn`. A use of one registers something the linker keeps although nothing calls it (review R1).
    private HashSet<string> _keepMacros = new(StringComparer.Ordinal);
    // Function-like macros that define a symbol named after an argument (see DefinerMacros), and a regex of
    // their names for finding uses.
    private Dictionary<string, List<DefinerMacros.Template>> _definers = new(StringComparer.Ordinal);
    // What macros that paste their own parameters (`CAT(a, b) a##b`) build, through wrappers (see PasteMacros).
    private PasteMacros.Table _pasteTable = new();
    private Regex? _pasteUse;
    private HashSet<string> _objectMacros = new(StringComparer.Ordinal);
    /// <summary>The tree's definer macros, for the emitted-tree check (null when there are none).</summary>
    public DefinerMacros.Set? DefinerSet { get; private set; }
    private Regex? _definerUse;
    private HashSet<string> _keepMacrosFnLike = new(StringComparer.Ordinal);
    private Regex? _keepMacroUse;
    private static readonly Regex AnyDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(\()?((?:[^\n\\]|\\\r?\n|\\.)*)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex KeepBody = new(
        @"\bsection\s*\(|\b(?:used|retain|constructor|destructor|__root)\b|#\s*pragma\s+location|_Pragma\s*\(\s*""location"
        + @"|gnu::(?:used|section|retain|constructor|destructor)|__declspec\s*\(\s*allocate", RegexOptions.Compiled);
    private static readonly Regex FuncLikeDefine = new(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Local <c>#include</c>d files with a non-source extension (<c>.inc</c>/<c>.def</c>/generated tables)
    /// that are textually part of a <c>.c</c>'s translation unit but which we do NOT parse as C. We scan
    /// them for identifiers and keep every symbol they name (over-approximation, like inline asm), so a
    /// function/global called ONLY from a generated table (LLVM-style <c>GenDisassemblerTables.inc</c>
    /// calling <c>DecodeARRegisterClass</c>) isn't dropped and left dangling when the emitter copies the
    /// include. The CLI supplies these; each gets a File node and an Includes edge from its includer, so
    /// keeping the includer keeps the table keeps everything it references. Empty by default.
    /// </summary>
    public IReadOnlyList<(string Path, string Text)> ReferenceOnlyIncludes { get; set; }
        = Array.Empty<(string, string)>();

    /// <summary>
    /// Optional per-file preprocessor config. Given a file's path, returns the <see cref="MacroTable"/> to
    /// resolve THAT file's <c>#ifdef</c>s against — used when a build log supplies defines that differ per
    /// translation unit. The CLI builds this so a macro defined in only SOME of a file's compile commands is
    /// treated as UNKNOWN for that file (both branches kept, sound), instead of unioning all TUs' defines and
    /// dropping the <c>#else</c> branch another TU actually compiles. Null (default) → the single global
    /// <c>defines</c> passed to <see cref="BuildGraph"/> is used for every file, as before.
    /// </summary>
    public Func<string, MacroTable?>? PerFileDefines { get; set; }

    /// <summary>Dead lines the build's own preprocessor decided for a file (see CompilerPreprocess), or null to use
    /// the conditional scanner. Takes precedence over <see cref="PerFileDefines"/>.</summary>
    public Func<string, string, bool[]?>? ExactDeadLines { get; set; }

    /// <summary>
    /// Optional parse-progress callback, invoked once per parsed file with (filesDone, filesTotal, bytesDone,
    /// bytesTotal). Parsing is the dominant, roughly byte-linear phase, so a caller can turn measured
    /// throughput (bytesDone / elapsed) plus the known bytesTotal into a live, self-calibrating ETA rather
    /// than a guess. Bytes exclude oversized/skipped files (they aren't parsed). Null (default) = no callback.
    /// </summary>
    public Action<int, int, long, long>? OnParseProgress { get; set; }

    protected TreeSitterFrontEnd(string grammarLib, string grammarFn, string defsQuery, string callsQuery)
    {
        _lang = new Language(grammarLib, grammarFn);
        _cGrammar = grammarLib == "tree-sitter-c";
        _defs = new Query(_lang, defsQuery);
        _calls = new Query(_lang, callsQuery);
        _idents = new Query(_lang, IdentQuery);
        _initRefs = new Query(_lang, _cGrammar ? InitRefQuery : InitRefQuery + InitRefQueryCpp);
        _globals = new Query(_lang, GlobalQuery);
        _locals = new Query(_lang, LocalQuery);
        _scopes = new Query(_lang, _cGrammar ? ScopeQueryC : ScopeQueryC + "(for_range_loop) @for");
    }

    /// <summary>
    /// Optional include search path for a file (review P1): the carve-relative directories its compile command
    /// passes with -I/-iquote/-isystem, in order, and whether they are EXACT for this file (its own command) or
    /// a union (a header, or a file with no command). Null = no build information: includes resolve beside the
    /// includer, else by basename.
    /// </summary>
    public Func<string, (IReadOnlyList<string> Dirs, bool Exact)?>? IncludeSearch { get; set; }

    /// <summary>
    /// Build the dependency graph for the given files. When <paramref name="defines"/> is supplied, the
    /// preprocessor conditionals are resolved against it and code in dead <c>#ifdef</c> branches is
    /// ignored — a config-specific (tighter) carve. With no defines, every branch is kept (safe).
    /// </summary>
    public CodeGraph BuildGraph(IEnumerable<(string Path, string Text)> files, MacroTable? defines = null,
                                bool closedWorldDefines = false)
    {
        // Compatibility overload: materialize the text once (a Dictionary) and hand the streaming core a
        // reader over it. Used by tests and small in-memory callers; holds all text (fine at that scale).
        var list = files as IReadOnlyCollection<(string Path, string Text)> ?? files.ToList();
        var paths = new List<string>(list.Count);
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, t) in list) { paths.Add(p); text.TryAdd(p, t); }
        return BuildGraph(paths, p => text.TryGetValue(p, out var t) ? t : "", defines, closedWorldDefines);
    }

    /// <summary>
    /// Streaming graph build: takes the file <paramref name="paths"/> and a <paramref name="read"/> callback
    /// invoked to fetch each file's text on demand (returning <c>""</c> for a skip/keep-whole/unreadable file).
    /// Each file's text is read, used, and released before the next — so peak memory is one file's text, not
    /// the whole tree's. <paramref name="read"/> may be called more than once per path (a scope-macro pre-pass
    /// then the parse pass); it must be idempotent and cheap to repeat (the OS file cache serves the re-read).
    /// </summary>
    public CodeGraph BuildGraph(IReadOnlyList<string> paths, Func<string, string> read, MacroTable? defines = null,
                                bool closedWorldDefines = false)
    {
        _warnings.Clear();
        _forceKeepFiles.Clear();
        _forceKeepSet.Clear();
        _read = read;
        _macroNamedHeaders.Clear();
        _includeDirectives.Clear();
        _c99InlineFns.Clear();
        _fileLocal.Clear();
        _condExternMacros.Clear();
        _callFreeMacros.Clear();
        _headerData.Clear();
        _emptyDefineConditions.Clear();
        _condExternFlags.Clear();
        _definesExternFlag.Clear();
        _unparsed.Clear();
        _symbolBudgetKeptWhole.Clear();
        var graph = new CodeGraph();
        // CODECARVER_TIMING: sub-phases of the graph build (pre-pass, parse, resolution) to the log.
        // Always measured (summary.txt reports it); printed only with CODECARVER_TIMING.
        var logTiming = Environment.GetEnvironmentVariable("CODECARVER_TIMING") is not null;
        _timing.Clear();
        _tPrep = _tParse = _tScan = _tRead = 0;
        _errorFiles = 0;
        var phaseClock = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string what)
        {
            _timing[what.Replace(' ', '-').Replace('+', '-')] = phaseClock.ElapsedMilliseconds;
            if (logTiming) Log.WriteLine($"  timing  :   {what,-14}{phaseClock.ElapsedMilliseconds,7} ms");
            phaseClock.Restart();
        }
        var (bytesTotal, filesTotal) = BuildScopeMacros(paths, read); // pre-pass: scope macros + parse work totals
        Phase("macro pre-pass");

        var fileNodeByPath = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var pathsByBasename = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Concat(ReferenceOnlyIncludes.Select(r => r.Path))) // reference includes get File nodes
        {                                                                              // + basenames so #include resolves
            if (fileNodeByPath.ContainsKey(path)) continue;
            fileNodeByPath[path] = graph.GetOrAddNode(NodeKind.File, path);
            var bas = BaseName(path);
            if (!pathsByBasename.TryGetValue(bas, out var list2))
                pathsByBasename[bas] = list2 = new List<string>();
            list2.Add(path);
        }

        var functionsByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var macrosByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var globalsByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var namePool = new Dictionary<string, string>(StringComparer.Ordinal);   // one string per distinct name (RB2)
        var pendingCalls = new UseList(namePool);
        var pendingRefs = new UseList(namePool);
        var pendingMacroRefs = new UseList(namePool);
        var pendingPastes = new List<(NodeId Macro, PasteKind Kind, string Frag)>();

        var keepNames = new HashSet<string>(StringComparer.Ordinal);
        var keepAt = new List<(string Name, string Path)>();   // where each keep attribute sits (translation units)
        var timeFiles = logTiming;
        var fsw = new System.Diagnostics.Stopwatch();
        long slowestMs = 0, slowestBytes = 0, slowMsTotal = 0, parsedBytes = 0, largestBytes = 0;
        int slowFiles = 0, verySlowFiles = 0, parsedFiles = 0, filesOver1MB = 0;

        long bytesDone = 0; var filesDone = 0;

        foreach (var path in paths)
        {
            var tr = System.Diagnostics.Stopwatch.GetTimestamp();
            var text = read(path);
            _tRead += System.Diagnostics.Stopwatch.GetTimestamp() - tr;
            if (text.Length == 0) continue; // oversized/empty file: File node already registered; nothing to parse
            fsw.Restart();
            try
            {
                // Per-file #ifdef config: when a build log supplies per-TU defines, PerFileDefines yields the
                // set consistent for THIS file (a macro defined in only SOME of a file's compile commands is
                // omitted -> UNKNOWN -> both branches kept, sound). Falls back to the global `defines` when no
                // per-file table exists (no build log, or a file absent from it).
                var fileDefines = PerFileDefines?.Invoke(path) ?? defines;
                ProcessFile(graph, path, text, fileNodeByPath, pathsByBasename,
                            functionsByName, macrosByName, globalsByName, pendingCalls, pendingRefs,
                            pendingMacroRefs, pendingPastes, fileDefines, closedWorldDefines);
                foreach (var n in ScanKeepAttributes(text)) { keepNames.Add(n); if (IsTranslationUnit(path)) keepAt.Add((n, path)); }
                if (_keepMacroUse is not null && fileNodeByPath.TryGetValue(path, out var kfile))
                    ScanKeepMacroUses(graph, text, kfile, keepNames, pendingRefs);
            }
            catch (Exception ex)   // never let one pathological file sink a whole-repo carve
            {
                _warnings.Add($"{path}: extraction failed ({ex.GetType().Name}: {ex.Message}) — kept whole, not carved");
                ForceKeep(path);
                if (fileNodeByPath.TryGetValue(path, out var failedFile)) KeptWholeRefs(path, text, failedFile, pendingRefs);
            }
            var fileMs = fsw.ElapsedMilliseconds;
            if (timeFiles && fileMs >= 300)
                Log.WriteLine($"  slowfile: {fileMs,6} ms  {path} ({text.Length:N0} B)");
            parsedFiles++; parsedBytes += text.Length;
            largestBytes = Math.Max(largestBytes, text.Length);
            if (text.Length >= 1 << 20) filesOver1MB++;
            if (fileMs >= 1000) { slowFiles++; slowMsTotal += fileMs; }
            if (fileMs >= 10_000) verySlowFiles++;
            if (fileMs > slowestMs) { slowestMs = fileMs; slowestBytes = text.Length; }

            bytesDone += text.Length; filesDone++;
            OnParseProgress?.Invoke(filesDone, filesTotal, bytesDone, bytesTotal);
        }

        // Reference-only includes (.inc/.def generated tables): not parsed as a TU, but every symbol they
        // name is kept (attributed to the include's file node, reached via the includer's Includes edge).
        foreach (var (path, text) in ReferenceOnlyIncludes)
        {
            if (text.Length == 0 || !fileNodeByPath.TryGetValue(path, out var incNode)) continue;
            IdentifierRefs(text, incNode, pendingRefs, callShapedOnly: false);
        }

        Phase("parse files");
        static long Ms(long ticks) => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;
        _timing["parse-files.rewrites"] = Ms(_tPrep);
        _timing["parse-files.tree-sitter"] = Ms(_tParse);
        _timing["parse-files.scan"] = Ms(_tScan);
        _timing["parse-files.read"] = Ms(_tRead);
        _timing["files.parsed"] = parsedFiles;
        _timing["files.parsedBytes"] = parsedBytes;
        _timing["files.largestBytes"] = largestBytes;
        _timing["files.over1MB"] = filesOver1MB;
        _timing["files.withParseErrors"] = _errorFiles;
        _timing["files.slowOver1s"] = slowFiles;
        _timing["files.slowOver10s"] = verySlowFiles;
        _timing["files.slowOver1sTotalMs"] = slowMsTotal;
        _timing["files.slowestMs"] = slowestMs;
        _timing["files.slowestBytes"] = slowestBytes;
        if (logTiming)
        {
            Log.WriteLine($"  timing  :     rewrites    {Ms(_tPrep),7} ms");
            Log.WriteLine($"  timing  :     tree-sitter {Ms(_tParse),7} ms");
            Log.WriteLine($"  timing  :     scan 1d     {Ms(_tScan),7} ms  ({_errorFiles:N0} files with parse errors, {_recoveredByScan:N0} recovered)");
            Log.WriteLine($"  timing  :     read        {Ms(_tRead),7} ms");
        }
        // P2: a file that is #included by another (unity build, "#include the .c") shares its statics with the
        // includer, so its statics are not restricted.
        var includedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            if (n.Kind == NodeKind.File)
                foreach (var e in graph.OutEdges(n.Id))
                    if (e.Kind == EdgeKind.Includes) includedFiles.Add(graph.GetNode(e.To).Name);
        HeaderData(graph, fileNodeByPath, globalsByName);
        bool Visible(NodeId from, NodeId target)
        {
            if (!_fileLocal.Contains(target)) return true;
            var tf = graph.GetNode(target).FilePath;
            var ff = graph.GetNode(from).FilePath;
            // Only a use from ANOTHER translation unit is ruled out; a header or table can be included into the
            // static's own TU, and an included .c is part of its includer.
            return ff is null || tf is null || ff == tf || !IsTranslationUnit(ff) || includedFiles.Contains(tf);
        }
        foreach (var (from, name) in pendingCalls)
            ResolveUse(graph, from, name, functionsByName, macrosByName, globalsByName, EdgeKind.Calls, Visible);
        foreach (var (from, name) in pendingRefs)
            ResolveUse(graph, from, name, functionsByName, macrosByName, globalsByName, EdgeKind.AddressTaken, Visible);
        // A macro that expands to a call/another macro reaches those — so a function or global used ONLY
        // through a macro body (e.g. `#define getSBox(n) sbox[n]`) is not lost when its callers are pruned.
        foreach (var (from, name) in pendingMacroRefs)
            ResolveUse(graph, from, name, functionsByName, macrosByName, globalsByName, EdgeKind.Calls);

        // Token-paste (##): a macro that builds a name like `arg ## Suffix` generates functions we can't
        // see statically. Conservatively link the macro to every function whose name matches the literal
        // fragment, so a name-generating macro (dispatch/handler tables) keeps its generated targets.
        Phase("resolve uses");
        foreach (var (macro, kind, frag) in pendingPastes)
        {
            if (frag.Length == 0) continue;
            // A paste in a definer's declarator slot (`n##_desc = ...`) DEFINES the name at each use (pass 1c),
            // it does not refer to every `*_desc` in the tree.
            if (_definers.TryGetValue(graph.GetNode(macro).Name, out var templates)
                && templates.Any(t => kind == PasteKind.Suffix ? t.Prefix.Length == 0 && t.Suffix == frag
                                    : kind == PasteKind.Prefix && t.Suffix.Length == 0 && t.Prefix == frag))
                continue;
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
        // A kept symbol the parse made no node for in its file (an attribute between the declarator and its initializer):
        // its translation unit stays whole, so what it registers and what that references survive.
        foreach (var (name, kpath) in keepAt)
            if (!(functionsByName.TryGetValue(name, out var kf) && kf.Any(id => graph.GetNode(id).FilePath == kpath))
                && !(globalsByName.TryGetValue(name, out var kg) && kg.Any(id => graph.GetNode(id).FilePath == kpath))
                && !_forceKeepSet.Contains(kpath))
                ForceKeep(kpath);

        Phase("pastes+rest");
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
        if (!text.Contains("asm")) yield break; // cheap guard: skip the regex on files with no inline asm
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

    /// <summary>
    /// Uses of keep macros (see <see cref="_keepMacros"/>) outside preprocessor lines. A function-like use
    /// (<c>INITCALL(drv_init);</c>) roots its file and takes the address of every identifier in its arguments;
    /// either kind also keeps the symbol it decorates (<c>RAMFUNC void f(void)</c>), found like an attribute.
    /// </summary>
    private void ScanKeepMacroUses(CodeGraph graph, string text, NodeId fileNode, HashSet<string> keepNames,
                                   UseList pendingRefs)
    {
        foreach (Match m in _keepMacroUse!.Matches(text))
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            if (text.AsSpan(lineStart, m.Index - lineStart).TrimStart().StartsWith("#")) continue; // the #define itself
            var end = m.Index + m.Length;
            if (_keepMacrosFnLike.Contains(m.Value))
            {
                var i = end;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] != '(') continue;
                var depth = 0; var start = i;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')' && --depth == 0) { i++; break; }
                }
                graph.AddFlag(fileNode, NodeFlags.Keep);
                foreach (Match id in Identifier.Matches(text[start..i])) pendingRefs.Add((fileNode, id.Value));
                end = i;
            }
            // A registration nobody names (`RITE(1)` placing a static's pointer in a walked section): the
            // translation unit stays whole.
            if (_registerMacros.Contains(m.Value) && IsTranslationUnit(graph.GetNode(fileNode).Name) && !_forceKeepSet.Contains(graph.GetNode(fileNode).Name))
                ForceKeep(graph.GetNode(fileNode).Name);
            var before = text.AsSpan(0, m.Index);
            var mb = NameBeforeAttr.Match(before.Length > 200 ? before[^200..].ToString() : before.ToString());
            if (mb.Success && mb.Groups[1].Value is var bn && !_keepMacros.Contains(bn)) keepNames.Add(bn);
            var after = text.AsSpan(end);
            var ma = NameAfterAttr.Match(after.Length > 200 ? after[..200].ToString() : after.ToString());
            if (ma.Success) keepNames.Add(ma.Groups[1].Value);
        }
    }

    /// <summary>Names decorated with a keep-attribute (constructor/destructor/used/retain/init-array
    /// section), resolving the decorated symbol whether the attribute leads or trails the declaration.</summary>
    private static IEnumerable<string> ScanKeepAttributes(string text)
    {
        if (!text.Contains("__attribute") && !text.Contains("[[")) yield break; // cheap guard: no attributes -> skip the regex
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
    /// <summary>Collect object-like macros whose replacement is scope-structural (opens/closes a
    /// namespace or brace scope), across ALL inputs (the #define is often in a header, the use in a .cc).
    /// Value macros (numbers, attributes) are deliberately left alone — expanding them risks changing a
    /// parse that already works. Multi-line (<c>\</c>-continued) replacements are flattened to one line so
    /// expansion never shifts line numbers.</summary>
    private (long BytesTotal, int FilesTotal) BuildScopeMacros(IReadOnlyList<string> paths, Func<string, string> read)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        var fnLike = new HashSet<string>(StringComparer.Ordinal);
        var bodies = new Dictionary<string, (bool FnLike, List<string> Bodies)>(StringComparer.Ordinal);
        long bytesTotal = 0; var filesTotal = 0;

        // Single streaming pass: read each file once and run every collector on it, so the whole tree's text
        // is never resident at once. Also tallies the parse work totals (non-empty files/bytes) for the ETA —
        // saving a separate pass. Each file's text is released before the next.
        foreach (var path in paths)
        {
            var text = SourceText.Normalize(read(path));
            if (text.Length == 0) continue;
            bytesTotal += text.Length; filesTotal++;
            if (!IsTranslationUnit(path) && TemplateInstances.HasMacroNamedHead(text)) _macroNamedHeaders.Add(path);
            if (!IsTranslationUnit(path)) _c99InlineFns.UnionWith(C99Inline.HeaderDefinitions(text));

            foreach (Match m in ObjectLikeDefine.Matches(text))
            {
                var name = m.Groups[1].Value;
                var repl = Regex.Replace(m.Groups[2].Value, @"\\\r?\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
                if (!defs.ContainsKey(name)) defs[name] = repl;
                if (map.ContainsKey(name)) continue;
                if (repl.Length == 0 || repl.Length > 200) continue;
                // Scope OPENERS/CLOSERS only: an unbalanced net brace count (`namespace fmt {` = +2, `}}` =
                // -2) or the `namespace` keyword. A brace-BALANCED replacement is a value macro — a
                // compound literal like wren's `((Value){ VAL_NULL, { 0 } })` — and expanding it would
                // corrupt a parse that already works. Those must be left alone.
                var net = 0;
                foreach (var c in repl) { if (c == '{') net++; else if (c == '}') net--; }
                if (net == 0 && !repl.Contains("namespace")) continue;
                map[name] = repl;
            }
            foreach (Match m in ValuelessDefine.Matches(text))
            {
                var n = m.Groups[1].Value;
                if (!defs.ContainsKey(n)) defs[n] = "";
            }
            foreach (Match m in FuncLikeDefine.Matches(text)) fnLike.Add(m.Groups[1].Value);
            if (text.Contains("define", StringComparison.Ordinal))
            {
                foreach (Match m in AnyDefine.Matches(text))
                {
                    var name = m.Groups[1].Value;
                    if (!bodies.TryGetValue(name, out var b)) bodies[name] = b = (m.Groups[2].Success, new List<string>());
                    if (b.Bodies.Count < 8) b.Bodies.Add(m.Groups[3].Value);
                }
                if (text.Contains("extern", StringComparison.Ordinal)) EmptyDefineConditions(text);
            }
        }

        // Keep macros: a keep construct in the body, or (transitively) a use of another keep macro.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in bodies)
            if (kv.Value.Bodies.Any(b => KeepBody.IsMatch(b))) keep.Add(kv.Key);
        for (var changed = keep.Count > 0; changed;)
        {
            changed = false;
            foreach (var kv in bodies)
                if (!keep.Contains(kv.Key) && kv.Value.Bodies.Any(b => Identifier.Matches(b).Any(id => keep.Contains(id.Value))))
                { keep.Add(kv.Key); changed = true; }
        }
        _keepMacros = keep;
        var register = new HashSet<string>(bodies.Where(kv => kv.Value.Bodies.Any(b => IdentifierSection.IsMatch(b))).Select(kv => kv.Key), StringComparer.Ordinal);
        for (var changed = register.Count > 0; changed;)
        {
            changed = false;
            foreach (var kv in bodies)
                if (!register.Contains(kv.Key) && kv.Value.Bodies.Any(b => Identifier.Matches(b).Any(id => register.Contains(id.Value))))
                { register.Add(kv.Key); changed = true; }
        }
        _registerMacros = register;
        // `#ifdef DEFINE_GLOBALS / #define EXTERN / #else / #define EXTERN extern`: a header's `EXTERN int g;` is a
        // definition in the unit that turns it off (see HeaderData).
        foreach (var kv in bodies)
        {
            if (kv.Value.FnLike)
            {
                // `#define BIT(n) (1u << (n))`: using it calls nothing (see HasCall).
                if (kv.Value.Bodies.All(b => !CallInBody.IsMatch(b))) _callFreeMacros.Add(kv.Key);
                continue;
            }
            var forms = kv.Value.Bodies.Select(b => CommentOrSpace.Replace(b, "")).ToList();
            if (!forms.Contains("extern") || !forms.Contains("")) continue;
            _condExternMacros.Add(kv.Key);
            if (_emptyDefineConditions.TryGetValue(kv.Key, out var flags)) _condExternFlags.UnionWith(flags);
        }
        _keepMacrosFnLike = new HashSet<string>(keep.Where(k => bodies[k].FnLike), StringComparer.Ordinal);
        _keepMacroUse = keep.Count == 0 ? null
            : new Regex(@"\b(?:" + string.Join("|", keep.Select(Regex.Escape)) + @")\b", RegexOptions.Compiled);

        _definers = DefinerMacros.Build(bodies);
        _objectMacros = new HashSet<string>(defs.Keys, StringComparer.Ordinal);
        _pasteTable = PasteMacros.Build(bodies, _objectMacros.Contains);
        _pasteUse = PasteMacros.UseRegex(_pasteTable);
        DefinerSet = DefinerMacros.MakeSet(_definers);
        _definerUse = _definers.Count == 0 ? null
            : new Regex(@"\b(" + string.Join("|", _definers.Keys.Select(Regex.Escape)) + @")\s*\(", RegexOptions.Compiled);

        _scopeMacros = map;
        _scopeRegex = map.Count == 0 ? null
            : new Regex(@"\b(?:" + string.Join("|", map.Keys.Select(Regex.Escape)) + @")\b", RegexOptions.Compiled);

        // Specifier/empty object-like macros to blank for parsing (see _blankRegex docs). `defs` holds ALL
        // object-like definitions (valued + valueless) gathered above; seed the blank set with those that are
        // empty or a pure specifier, then close transitively (a macro whose body is a single already-blankable
        // macro, e.g. PUGIXML_NOEXCEPT_IF_NOT_COMPACT -> PUGIXML_NOEXCEPT -> noexcept).
        var blank = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in defs)
            if (kv.Value.Length == 0 || SpecifierMacroBody.IsMatch(kv.Value)) blank.Add(kv.Key);
        for (var changed = true; changed;)   // transitive closure (bounded: the set only grows)
        {
            changed = false;
            foreach (var kv in defs)
                if (!blank.Contains(kv.Key) && blank.Contains(kv.Value)) { blank.Add(kv.Key); changed = true; }
        }
        // Never blank an occurrence followed by "(": a valueless #define NAME in one target must not erase calls
        // to another target's real function NAME (review N1).
        _blankRegex = blank.Count == 0 ? null
            : new Regex(@"\b(?:" + string.Join("|", blank.Select(Regex.Escape)) + @")\b(?!\s*\()", RegexOptions.Compiled);

        _funcLikeMacroNames = fnLike;

        // Alias macros: `#define WEAK_ALIAS(f) __attribute__((weak, alias(#f)))`. A declarator followed by one is an
        // alias of the macro's argument (pass 2b).
        var aliasMacros = bodies.Where(kv => kv.Value.FnLike && kv.Value.Bodies.Any(b => AliasMacroBody.IsMatch(b)))
                                .Select(kv => kv.Key).ToList();
        var wrappers = bodies.Where(kv => kv.Value.FnLike && kv.Value.Bodies.Count > 0 && kv.Value.Bodies.All(b => IdentityBody.IsMatch(b)))
                             .Select(kv => kv.Key).ToList();
        _wrapperUse = wrappers.Count == 0 ? null
            : new Regex(@"\b(?:" + string.Join("|", wrappers.Select(Regex.Escape)) + @")\s*\(\s*(?<name>[A-Za-z_]\w*)\s*\)(?=\s*\()",
                        RegexOptions.Compiled);
        _aliasMacroUse = aliasMacros.Count == 0 ? null
            : new Regex(@"\b(?<name>[A-Za-z_]\w*)\s*\([^(){};]*\)\s*(?:" + string.Join("|", aliasMacros.Select(Regex.Escape))
                        + @")\s*\(\s*(?<target>[A-Za-z_]\w*)\s*\)\s*;", RegexOptions.Compiled);
        return (bytesTotal, filesTotal);
    }

    /// <summary>`int EXPORT(name)(params)` where <c>#define EXPORT(n) n</c> (or <c>(n)</c>): the parser sees a call-shaped
    /// head named EXPORT. For parsing only, blank the wrapper and its parentheses so the head reads <c>int name(params)</c>.
    /// Length-preserving; #define lines are left alone.</summary>
    private string UnwrapNames(string text)
    {
        if (_wrapperUse is null || !_wrapperUse.IsMatch(text)) return text;
        var sb = new StringBuilder(text);
        foreach (Match m in _wrapperUse.Matches(text))
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            if (text.AsSpan(lineStart, m.Index - lineStart).TrimStart().StartsWith("#")) continue;
            var name = m.Groups["name"];
            for (var k = m.Index; k < name.Index; k++) if (sb[k] != '\n') sb[k] = ' ';
            for (var k = name.Index + name.Length; k < m.Index + m.Length; k++) if (sb[k] != '\n') sb[k] = ' ';
        }
        return sb.ToString();
    }

    /// <summary>Replace scope-opening macros with their (single-line) expansion, for parsing only. Skips
    /// preprocessor lines (the #define itself, #if using the macro) and preserves the line count exactly,
    /// so a captured node's line numbers still index the original text the emitter reads.</summary>
    private string ExpandScopeMacros(string text)
    {
        if (_scopeRegex is null && _blankRegex is null) return text;
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length + 128);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.TrimStart().StartsWith('#'))
            {
                if (_scopeRegex is not null)
                    line = _scopeRegex.Replace(line, mm => _scopeMacros.TryGetValue(mm.Value, out var r) ? r : mm.Value);
                // Blank specifier/empty macros with equal-length spaces so LINE and COLUMN still map to the
                // original text the emitter reads (the token is on the same line as the function signature).
                if (_blankRegex is not null)
                    line = _blankRegex.Replace(line, mm => new string(' ', mm.Length));
            }
            sb.Append(line);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    // Worker stack for the budgeted parse. A `new Thread(start)` gets .NET's DEFAULT ~1MB stack — much
    // smaller than the main thread's — and tree-sitter's deep native error-recovery on a large/degenerate
    // file overflows it, surfacing as a native AccessViolation (0xC0000005) that the CLR will NOT deliver
    // to a managed catch, so the process dies silently (real eval-#2 crash: ~3% of >=256KB worker parses;
    // never on the inline main-thread path). A large explicit stack lets the recursion fit.
    private const int WorkerStackBytes = 64 * 1024 * 1024;

    private Tree? ParseWithBudget(string text, string path, out bool timedOut)
    {
        timedOut = false;
        if (ParseBudgetMs <= 0 || text.Length < BudgetMinBytes)
            return (_inlineParser ??= new Parser(_lang)).Parse(text);

        // Breadcrumb BEFORE the parse: an AV is a corrupted-state exception we can't catch, so this stderr
        // line (flushed) is the only way a crash is attributable to a file instead of reading as a CI flake.
        Log.WriteLine($"  bigparse: {path} ({text.Length:N0} B, {ParseBudgetMs} ms budget) ...");
        Log.Flush();

        Tree? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            // not `using`: if abandoned on timeout, let process teardown reclaim it. A managed parse error
            // (not an AV) is captured and reported as keep-whole rather than propagated.
            try { var parser = new Parser(_lang); result = parser.Parse(text); parser.Dispose(); }
            catch (Exception ex) { failure = ex; }
        }, WorkerStackBytes) { IsBackground = true, Name = "ts-parse-budget" };
        worker.Start();
        if (worker.Join(ParseBudgetMs)) // Join true => happens-before on `result`/`failure`
        {
            if (failure is not null) { _warnings.Add($"{path}: parse threw {failure.GetType().Name} — kept whole, not carved"); return null; }
            return result;
        }
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

    /// <summary>`static` on a function DEFINITION at file scope (not a class member, not inside a namespace
    /// block where it could still be a member) — internal linkage.</summary>
    /// <summary>The name heads a call-shaped declarator that is itself called: <c>CAT(TNAME, get)(void)</c>.</summary>
    private static bool ComputedName(TsNode nameNode)
        => nameNode.Parent is { Type: "function_declarator" } p && p.Parent is { Type: "function_declarator" };

    private static bool IsFileScopeStatic(TsNode nameNode)
    {
        TsNode? def = nameNode;
        for (var i = 0; i < 12 && def is not null && def.Type != "function_definition"; i++)
        {
            if (def.Type is "qualified_identifier" or "field_identifier") return false;   // Class::method
            def = def.Parent;
        }
        if (def is null || def.Type != "function_definition") return false;
        if (def.Parent?.Type is not ("translation_unit" or "preproc_if" or "preproc_ifdef" or "preproc_else" or "preproc_elif"))
            return false;
        foreach (var ch in def.Children)
            if (ch.Type == "storage_class_specifier" && ch.Text == "static") return true;
        return false;
    }

    /// <summary>Resolve an #include the way the compiler would (P1). Quoted: beside the includer first (gcc's
    /// rule, exact even without a build log). Then the file's -I directories: for a TU with its own command the
    /// FIRST hit is what the compiler opens; with a union of directories every hit is kept (sound).</summary>
    private List<string> ResolveInclude(string includer, string raw, bool quoted, Dictionary<string, NodeId> files)
    {
        var hits = new List<string>();
        var inc = raw.Replace('\\', '/');
        if (quoted)
        {
            var dir = includer.Contains('/') ? includer[..includer.LastIndexOf('/')] : "";
            if (NormRel(dir.Length == 0 ? inc : dir + "/" + inc) is { } beside && files.ContainsKey(beside)) { hits.Add(beside); return hits; }
        }
        if (IncludeSearch?.Invoke(includer) is not { } search) return hits;
        foreach (var d in search.Dirs)
        {
            if (NormRel(d.Length == 0 ? inc : d.TrimEnd('/') + "/" + inc) is not { } cand || !files.ContainsKey(cand)) continue;
            if (!hits.Contains(cand)) hits.Add(cand);
            if (search.Exact && IsTranslationUnit(includer)) break;
        }
        return hits;
    }

    private static string? NormRel(string rel)
    {
        var parts = new List<string>();
        foreach (var seg in rel.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>Parameters and locals by name, each with where its scope starts (the declaration) and ends
    /// (the enclosing block, or the whole function for a parameter).</summary>
    /// <summary>
    /// The scopes of one parsed file, by byte range: function definitions, blocks, <c>for</c> loops, and block-scope
    /// <c>extern</c> declarations. Answers "inside a function body?" and "innermost scope" by binary search over the
    /// (properly nested) ranges. Tree-sitter's <c>Node.Parent</c> re-walks from the root on every call, so the
    /// ancestor walks this replaces cost O(depth^2) per identifier — 48 s of a 150 s carve of the death corpus.
    /// </summary>
    private sealed class ScopeIndex
    {
        private const byte Block = 0, For = 1, Function = 2;
        private readonly int[] _start, _end, _parent;
        private readonly byte[] _kind;
        private readonly List<(int Start, int End)> _externs = new();

        public ScopeIndex(Query query, TsNode root)
        {
            var list = new List<(int S, int E, byte K)>();
            foreach (var cap in query.Execute(root).Captures)
            {
                var n = cap.Node;
                switch (cap.Name)
                {
                    case "block": list.Add((n.StartIndex, n.EndIndex, Block)); break;
                    case "for": list.Add((n.StartIndex, n.EndIndex, For)); break;
                    case "fn": list.Add((n.StartIndex, n.EndIndex, Function)); break;
                    case "storage":
                        if (n.Text == "extern" && n.Parent is { Type: "declaration" } d) _externs.Add((d.StartIndex, d.EndIndex));
                        break;
                }
            }
            list.Sort((a, b) => a.S != b.S ? a.S.CompareTo(b.S) : b.E.CompareTo(a.E));   // outer before inner
            _start = new int[list.Count]; _end = new int[list.Count]; _kind = new byte[list.Count]; _parent = new int[list.Count];
            var open = new Stack<int>();
            for (var i = 0; i < list.Count; i++)
            {
                (_start[i], _end[i], _kind[i]) = list[i];
                while (open.Count > 0 && _end[open.Peek()] <= _start[i]) open.Pop();
                _parent[i] = open.Count > 0 ? open.Peek() : -1;
                open.Push(i);
            }
        }

        /// <summary>The innermost scope containing byte <paramref name="pos"/>, or -1. The last scope starting at
        /// or before it is the container or a descendant of it, so its parent chain reaches the container.</summary>
        private int Innermost(int pos)
        {
            int lo = 0, hi = _start.Length - 1, idx = -1;
            while (lo <= hi) { var mid = (lo + hi) >> 1; if (_start[mid] <= pos) { idx = mid; lo = mid + 1; } else hi = mid - 1; }
            while (idx >= 0 && _end[idx] <= pos) idx = _parent[idx];
            return idx;
        }

        /// <summary>Inside a function body or block (what <c>InsideFunctionBody</c> answers by walking up).</summary>
        public bool InBody(int pos)
        {
            for (var i = Innermost(pos); i >= 0; i = _parent[i])
                if (_kind[i] is Block or Function) return true;
            return false;
        }

        /// <summary>The end of the innermost block / for / function scope containing <paramref name="pos"/>.</summary>
        public int? ScopeEnd(int pos) => Innermost(pos) is var i and >= 0 ? _end[i] : null;

        /// <summary>The end of the innermost function definition containing <paramref name="pos"/>.</summary>
        public int? FunctionEnd(int pos)
        {
            for (var i = Innermost(pos); i >= 0; i = _parent[i])
                if (_kind[i] == Function) return _end[i];
            return null;
        }

        public bool InExternDeclaration(int pos) => _externs.Any(e => e.Start <= pos && pos < e.End);
    }

    private Dictionary<string, List<(int From, int To)>> CollectLocals(TsNode root, ScopeIndex scopes)
    {
        var r = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (var cap in _locals.Execute(root).Captures)
        {
            var n = cap.Node;
            var at = n.StartIndex;
            int? scopeEnd;
            if (scopes.InBody(at))
            {
                // A block-scope `extern int g;` names the GLOBAL g — its uses are references, not locals.
                if (scopes.InExternDeclaration(at)) continue;
                scopeEnd = scopes.ScopeEnd(at);
            }
            else
                // A parameter of a function DEFINITION: in scope for that function's body (a prototype's parameter
                // is in no function definition).
                scopeEnd = scopes.FunctionEnd(at);
            if (scopeEnd is not { } end) continue;
            var name = n.Text;
            if (!r.TryGetValue(name, out var l)) r[name] = l = new List<(int, int)>();
            l.Add((at, end));
        }
        return r;
    }

    private static bool IsLocalUse(Dictionary<string, List<(int From, int To)>> locals, string name, int pos)
    {
        if (!locals.TryGetValue(name, out var l)) return false;
        foreach (var (from, to) in l)
            if (pos >= from && pos < to) return true;   // the declaration itself, and every use after it
        return false;
    }

    /// <summary>
    /// Bodies whose function head is split across preprocessor branches (see pass 1a): a bare file- or
    /// namespace-scope compound_statement preceded by a function head that tree-sitter read as a declaration
    /// with a MISSING ';' — directly, or as the last thing in each branch of an #if/#ifdef chain. Yields the
    /// head's name node(s), the node the construct starts at, the body, and whether the construct is exactly
    /// heads + body (so the whole span can be removed safely).
    /// </summary>
    private static IEnumerable<(List<TsNode> Names, TsNode Start, TsNode Body, bool Clean)> SplitHeadDefinitions(TsNode root)
    {
        if (!root.HasError) yield break;   // every split head carries a MISSING ';'
        var scopes = new Stack<TsNode>();
        scopes.Push(root);
        while (scopes.Count > 0)
        {
            var scope = scopes.Pop();
            TsNode? prev = null;
            foreach (var child in scope.NamedChildren)
            {
                if (child.Type == "comment") continue;
                if (child.Type is "namespace_definition" or "linkage_specification"
                    && child.GetChildForField("body") is { } nested)
                    scopes.Push(nested);
                if (child.Type == "compound_statement" && prev is not null)
                {
                    var names = new List<TsNode>();
                    var clean = CollectSplitHeads(prev, names);
                    if (names.Count > 0) yield return (names, prev, child, clean);
                }
                prev = child;
            }
        }
    }

    /// <summary>Adds the function names of the MISSING-';' heads in <paramref name="n"/> (a declaration or an
    /// #if chain) and returns true when it holds nothing else.</summary>
    private static bool CollectSplitHeads(TsNode n, List<TsNode> names)
    {
        if (n.Type == "declaration")
        {
            if (!n.Children.Any(c => c.IsMissing) || HeadName(n) is not { } name) return false;
            names.Add(name);
            return true;
        }
        if (n.Type is not ("preproc_if" or "preproc_ifdef" or "preproc_elif" or "preproc_elifdef" or "preproc_else"))
            return false;
        var cond = n.GetChildForField("condition") ?? n.GetChildForField("name");
        var alt = n.GetChildForField("alternative");
        var clean = true;
        var content = 0;
        foreach (var c in n.NamedChildren)
        {
            if (c.Type == "comment" || (cond is not null && c.Id == cond.Id) || (alt is not null && c.Id == alt.Id)) continue;
            content++;
            if (!CollectSplitHeads(c, names)) clean = false;
        }
        if (content != 1) clean = false;
        if (alt is not null && !CollectSplitHeads(alt, names)) clean = false;
        return clean;
    }

    /// <summary>
    /// The name node of a file-scope variable definition that becomes a graph node, or null. Another file can link
    /// only to a non-static variable of a translation unit: that is what must keep its file. Arrays (tables) are nodes
    /// everywhere, so an unused one can be pruned. A static or header scalar stays text kept with its file: a product
    /// tree has millions (padding, constants), and as nodes they would only cost graph size. An <c>extern</c> without
    /// initializer only declares. A declaration with a parse error is often not one at all (comment words after a
    /// spliced <c>*\</c>+<c>/</c> read as <c>this comment ends at ...</c>): it mints nothing.
    /// </summary>
    /// <param name="headerDefinition">A header variable every including unit defines (see <see cref="HeaderData"/>).</param>
    private TsNode? MintedGlobal(TsNode decl, string path, string[] lines, out bool isStatic, out bool condExtern, out bool headerDefinition)
    {
        isStatic = false;
        condExtern = false;
        headerDefinition = false;
        if (decl.Parent is not { Type: "declaration" } declaration || declaration.HasError) return null;
        var storage = declaration.Children.Where(c => c.Type == "storage_class_specifier").Select(c => c.Text).ToList();
        if (storage.Contains("extern") && decl.Type != "init_declarator") return null;
        isStatic = storage.Contains("static");
        headerDefinition = !isStatic && !IsTranslationUnit(path) && HeaderDefines(declaration, decl, storage, lines, out condExtern);
        if (!HasArrayDeclarator(decl) && (isStatic || (!IsTranslationUnit(path) && !headerDefinition)))
            return null;
        return GlobalName(decl) is { IsMissing: false, Text.Length: > 0 } node ? node : null;
    }

    private static readonly Regex LeadingWord = new(@"^\s*([A-Za-z_]\w*)", RegexOptions.Compiled);

    /// <summary>A header scalar that is a definition in every unit including it: initialized and not extern, or
    /// introduced by an EXTERN-style macro (see <see cref="HeaderData"/>). C++ <c>const</c>/<c>constexpr</c>/<c>inline</c>
    /// variables and template members are each unit's own (or merged), so not.</summary>
    private bool HeaderDefines(TsNode declaration, TsNode decl, List<string> storage, string[] lines, out bool condExtern)
    {
        condExtern = false;
        if (storage.Contains("extern") || storage.Contains("inline") || declaration.Parent?.Type == "template_declaration") return false;
        if (!_cGrammar && declaration.Children.Any(c => c.Type is "type_qualifier" && c.Text is "const" or "constexpr" or "constinit")) return false;
        var row = declaration.StartPosition.Row;
        if (_condExternMacros.Count > 0 && row < lines.Length && LeadingWord.Match(lines[row]) is { Success: true } w
            && _condExternMacros.Contains(w.Groups[1].Value))
            return condExtern = true;
        return decl.Type == "init_declarator";
    }

    /// <summary>
    /// A variable a header DEFINES (<c>int g = 1;</c>, or <c>EXTERN int g;</c> with EXTERN empty in one unit) is defined
    /// in the translation units that include it, not in the header: keeping the header alone links nothing. Each such
    /// node references those units, so a use keeps one that compiles it. For the EXTERN idiom only the units that
    /// define a flag the #if around the empty EXTERN tests (by #define or -D) are linked; when none does, all are
    /// (sound). Skipped when a translation unit defines the name itself with external linkage.
    /// </summary>
    private void HeaderData(CodeGraph graph, Dictionary<string, NodeId> fileNodeByPath,
                            Dictionary<string, List<NodeId>> globalsByName)
    {
        if (_headerData.Count == 0) return;
        var includers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            if (n.Kind == NodeKind.File)
                foreach (var e in graph.OutEdges(n.Id))
                    if (e.Kind == EdgeKind.Includes)
                    {
                        var h = graph.GetNode(e.To).Name;
                        if (!includers.TryGetValue(h, out var l)) includers[h] = l = new List<string>();
                        l.Add(n.Name);
                    }
        var unitsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> Units(string header)
        {
            if (unitsOf.TryGetValue(header, out var have)) return have;
            var units = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { header };
            var work = new Queue<string>();
            work.Enqueue(header);
            while (work.Count > 0)
                foreach (var f in includers.GetValueOrDefault(work.Dequeue()) ?? new List<string>())
                {
                    if (!seen.Add(f)) continue;
                    if (IsTranslationUnit(f)) units.Add(f);
                    else work.Enqueue(f);
                }
            units.Sort(StringComparer.Ordinal);
            return unitsOf[header] = units;
        }
        foreach (var (id, header, condExtern) in _headerData)
        {
            var name = graph.GetNode(id).Name;
            // A unit that defines it itself, with external linkage (a same-named static elsewhere is another variable).
            if (globalsByName.TryGetValue(name, out var defs)
                && defs.Any(d => graph.GetNode(d) is { FilePath: { } p } dn && IsTranslationUnit(p) && (dn.Flags & NodeFlags.FileLocal) == 0))
                continue;
            var units = Units(header);
            if (condExtern && units.Where(_definesExternFlag.Contains).ToList() is { Count: > 0 } off) units = off;
            foreach (var u in units)
                if (fileNodeByPath.TryGetValue(u, out var un)) graph.AddEdge(id, un, EdgeKind.References);
        }
    }

    /// <summary>A declaration whose type specifier has a body and a tag other code can name: <c>struct cfg { int a; } g;</c>,
    /// <c>enum m {A} g;</c>. An anonymous one (<c>static const struct { ... } cmds[]</c>) is nameable nowhere else.</summary>
    private static bool DefinesType(TsNode declaration)
        => declaration.GetChildForField("type") is { Type: "struct_specifier" or "union_specifier" or "enum_specifier" or "class_specifier" } t
           && t.GetChildForField("body") is not null && t.GetChildForField("name") is not null;

    /// <summary>A C++ variable whose construction can run code: one whose initializer calls a function (not a macro),
    /// or an object of a non-builtin type (not through a pointer) that isn't brace-initialized — a table of aggregates
    /// (<c>Entry t[] = {{...}}</c>) runs nothing.</summary>
    private bool CppObject(TsNode declaration, TsNode decl, string path)
    {
        if (_cGrammar || path.EndsWith(".c", StringComparison.OrdinalIgnoreCase)) return false;
        var value = decl.Type == "init_declarator" ? decl.GetChildForField("value") : null;
        if (value is not null && HasCall(value)) return true;
        if (declaration.GetChildForField("type")?.Type is "primitive_type" or "sized_type_specifier" or "enum_specifier") return false;
        if (value?.Type == "initializer_list") return false;
        for (var d = decl; d is not null; d = d.GetChildForField("declarator"))
            if (d.Type is "pointer_declarator" or "reference_declarator" or "function_declarator") return false;
        return true;
    }

    /// <summary>A call, <c>new</c> or lambda anywhere in an initializer; a function-like macro whose body calls nothing
    /// (<c>BIT(1)</c>) isn't one.
    /// Iterative and bounded: a generated initializer can nest tens of thousands of levels deep, and past the budget
    /// the answer is yes (keep).</summary>
    private bool HasCall(TsNode n)
    {
        var stack = new Stack<TsNode>();
        stack.Push(n);
        for (var budget = 200_000; stack.Count > 0; budget--)
        {
            if (budget == 0) return true;
            var c = stack.Pop();
            if (c.Type is "new_expression" or "lambda_expression") return true;
            if (c.Type == "call_expression"
                && !(c.GetChildForField("function") is { Type: "identifier" } f && _callFreeMacros.Contains(f.Text)))
                return true;
            foreach (var k in c.NamedChildren) stack.Push(k);
        }
        return false;
    }

    /// <summary>Does the declarator declare an array (<c>int t[4]</c>, <c>const char *names[] = {...}</c>)?</summary>
    private static bool HasArrayDeclarator(TsNode declarator)
    {
        var d = declarator;
        for (var i = 0; i < 12 && d is not null; i++)
        {
            if (d.Type == "array_declarator") return true;
            if (d.Type is "identifier" or "function_declarator") return false;
            d = d.Type == "parenthesized_declarator" ? d.NamedChildren.FirstOrDefault(c => c.Type != "comment")
                : d.GetChildForField("declarator") ?? d.NamedChildren.LastOrDefault();
        }
        return false;
    }

    /// <summary>The name a declaration's declarator defines when it is a variable (through initializers, pointers,
    /// arrays, parentheses and function-pointer declarators), or null for a prototype or anything unnamed.</summary>
    private static TsNode? GlobalName(TsNode declarator)
    {
        var d = declarator;
        for (var i = 0; i < 12 && d is not null; i++)
            switch (d.Type)
            {
                case "identifier": return d;
                case "qualified_identifier": return d.GetChildForField("name") is { Type: "identifier" } q ? q : null;
                case "init_declarator" or "array_declarator" or "attributed_declarator":
                    d = d.GetChildForField("declarator"); break;
                case "pointer_declarator" or "reference_declarator":
                    d = d.GetChildForField("declarator") ?? d.NamedChildren.LastOrDefault(); break;
                case "parenthesized_declarator":
                    d = d.NamedChildren.FirstOrDefault(c => c.Type != "comment"); break;
                case "function_declarator":
                    // `int (*hook)(int)` is a pointer variable; `int f(void)` and `int (f)(void)` are prototypes.
                    var inner = d.GetChildForField("declarator");
                    if (inner?.Type != "parenthesized_declarator"
                        || inner.NamedChildren.FirstOrDefault(c => c.Type != "comment")?.Type is not ("pointer_declarator" or "reference_declarator"))
                        return null;
                    d = inner; break;
                default: return null;
            }
        return null;
    }

    /// <summary>The name node of a declaration whose declarator is a function declarator, or null.</summary>
    private static TsNode? HeadName(TsNode declaration)
    {
        var d = declaration.GetChildForField("declarator");
        for (var i = 0; i < 8 && d is not null; i++)
        {
            if (d.Type == "function_declarator")
            {
                var name = d.GetChildForField("declarator");
                if (name?.Type == "parenthesized_declarator") name = name.NamedChildren.FirstOrDefault();
                if (name?.Type == "qualified_identifier") name = name.GetChildForField("name");
                return name?.Type is "identifier" or "field_identifier" ? name : null;
            }
            d = d.Type is "pointer_declarator" or "reference_declarator" ? d.GetChildForField("declarator") ?? d.NamedChildren.LastOrDefault() : null;
        }
        return null;
    }

    /// <summary>
    /// The text tree-sitter is given for a file: every parse-only, line-preserving rewrite. Spans, the dead-line map and
    /// the emitter all read the ORIGINAL text. <paramref name="blanked"/> is text removed for parsing whose names must
    /// still count as the file's uses; <paramref name="bareKAndR"/> are K&amp;R heads defined without a removable body.
    /// </summary>
    private string PrepareParseText(string path, string text, out List<(string Name, int Line, int EndLine)>? bareKAndR,
                                    out string blanked)
    {
        // Expand scope-opening macros, so a file that opens its namespace with `FMT_BEGIN_NAMESPACE` is structured
        // correctly and its functions are captured.
        var parseText = ExpandScopeMacros(Digraphs.Rewrite(CodeCarver.Core.Preprocess.PreprocessorScanner.BlankAlwaysDead(text)));
        parseText = UnwrapNames(parseText);
        bareKAndR = null;
        var cut = "";
        if (_cGrammar || path.EndsWith(".c", StringComparison.OrdinalIgnoreCase))
        {
            parseText = ImplicitInt.Rewrite(parseText, _funcLikeMacroNames.Contains, out _, out bareKAndR);
            // A mixed C/C++ tree reads .c files with the C++ grammar, which rejects K&R parameter lists.
            if (!_cGrammar) parseText = ImplicitInt.KAndRToPrototype(parseText, out _);
            // Heads the C grammar can't read (`int WINAPI f(void)`, AUTOSAR `FUNC(void, X) f(...)`, a #pragma before
            // the body).
            parseText = HeadNormalizer.Normalize(parseText, out cut);
        }
        // Alternative parameter sets (#if/#else inside a parameter list) parsed as the first one.
        parseText = ParamListConditionals.Blank(parseText, initializers: !_cGrammar, out var branches);
        blanked = cut.Length == 0 ? branches : branches.Length == 0 ? cut : cut + "\n" + branches;
        return parseText;
    }

    /// <summary>
    /// Why a definition the link check found never became a graph node: fixed shape names, source-free, so a remote
    /// evaluation can report them as counts. <paramref name="line"/> is the 1-based line of the name in
    /// <paramref name="text"/>. Text features describe the head; parser features say what tree-sitter made of the
    /// name after the same parse-only rewrites the carve applied.
    /// </summary>
    public IReadOnlyList<string> DiagnoseDefinition(string path, string text, int line, string name)
    {
        var shapes = new List<string>();
        if (_unparsed.Contains(path)) { shapes.Add("fileNotParsed"); return shapes; }   // nothing the parser did applies
        if (_funcLikeMacroNames.Contains(name)) shapes.Add("nameIsAFunctionLikeMacro");
        shapes.AddRange(DefinitionHead.TextShapes(text, line, name));

        text = SourceText.Normalize(text);
        var parseText = PrepareParseText(path, text, out var bare, out _);
        if (bare is not null && bare.Any(b => b.Name == name && b.Line == line)) shapes.Add("bareKAndRHead");
        using var parser = new Parser(_lang);
        using var tree = parser.Parse(parseText);
        if (tree is null) { shapes.Add("parseFailed"); return shapes; }
        TsNode? hit = null;
        var row = line - 1;
        void Find(TsNode n)
        {
            if (hit is not null || n.StartPosition.Row > row || n.EndPosition.Row < row) return;
            if (n.StartPosition.Row == row && n.Text == name && n.Type is "identifier" or "type_identifier" or "field_identifier")
            {
                hit = n;
                return;
            }
            foreach (var c in n.Children) Find(c);
        }
        Find(tree.RootNode);
        if (hit is null) { shapes.Add("parserSawNoName"); return shapes; }
        if (hit.Type == "type_identifier") shapes.Add("nameReadAsType");
        var inError = false; var inDef = false; var inDecl = false; var inBody = false;
        var errorStart = int.MaxValue;
        for (var a = hit.Parent; a is not null; a = a.Parent)
        {
            switch (a.Type)
            {
                case "ERROR": inError = true; errorStart = Math.Min(errorStart, a.StartPosition.Row); break;
                case "function_definition": inDef = true; break;
                case "declaration": if (!inDef) inDecl = true; break;
                case "compound_statement": if (!inDef) inBody = true; break;
            }
        }
        if (inError) shapes.Add("insideParseError");
        // The error began well above this head: code before it (not the head) broke the parse and swallowed it.
        if (inError && errorStart < row - 2) shapes.Add("parseErrorStartsAbove");
        if (inBody) shapes.Add("insideABody");
        if (inDecl) shapes.Add("parsedAsDeclaration");
        if (inDef && !inError && !inBody && hit.Type != "type_identifier") shapes.Add("parsedAsDefinitionButRejected");
        // Otherwise: the grammar node the name ended up in (a fixed tree-sitter node name, so still source-free).
        if (!inError && !inBody && !inDecl && !inDef && hit.Parent is { } parent) shapes.Add("parsedAs_" + parent.Type);
        return shapes;
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
                                   EdgeKind functionEdge, Func<NodeId, NodeId, bool>? visible = null)
    {
        // Link the name to EVERY kind that defines it. One tree holds many targets/configurations, so a name
        // can be a function in one place and a macro or a global in another; picking the first kind found
        // dropped the others (review N1).
        if (functionsByName.TryGetValue(name, out var fns))
        {
            foreach (var target in fns)
            {
                if (visible is not null && !visible(from, target)) continue;
                graph.AddEdge(from, target, functionEdge);
                if (functionEdge == EdgeKind.AddressTaken)
                    graph.AddFlag(target, NodeFlags.AddressTaken);
            }
        }
        if (macrosByName.TryGetValue(name, out var macros))
        {
            foreach (var target in macros)
                graph.AddEdge(from, target, EdgeKind.Expands);
        }
        if (globalsByName.TryGetValue(name, out var globals))
        {
            foreach (var target in globals)
                if (visible is null || visible(from, target))
                    graph.AddEdge(from, target, EdgeKind.References);
        }
    }

    private void ProcessFile(CodeGraph graph, string path, string text,
                             Dictionary<string, NodeId> fileNodeByPath,
                             Dictionary<string, List<string>> pathsByBasename,
                             Dictionary<string, List<NodeId>> functionsByName,
                             Dictionary<string, List<NodeId>> macrosByName,
                             Dictionary<string, List<NodeId>> globalsByName,
                             UseList pendingCalls,
                             UseList pendingRefs,
                             UseList pendingMacroRefs,
                             List<(NodeId, PasteKind, string)> pendingPastes,
                             MacroTable? defines,
                             bool closedWorldDefines)
    {
        text = SourceText.Normalize(text);   // trigraphs, names split by a backslash-newline (offsets and lines kept)
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
            KeptWholeRefs(path, text, fileNodeByPath[path], pendingRefs);
            return;
        }

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var parseText = PrepareParseText(path, text, out var bareKAndR, out var blankedForParse);
        if (blankedForParse.Length > 0) IdentifierRefs(blankedForParse, fileNodeByPath[path], pendingRefs, callShapedOnly: false);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var tree = ParseWithBudget(parseText, path, out var timedOut);
        _tPrep += t1 - t0; _tParse += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
        if (timedOut)
        {
            _warnings.Add($"{path}: parse exceeded the {ParseBudgetMs} ms budget — kept whole, not carved");
            // Its definitions are unknown, so nothing could reach it: a translation unit must be force-kept like
            // one over the symbol budget, or a call into it finds no definition and the file is dropped.
            if (IsTranslationUnit(path)) ForceKeep(path);
            KeptWholeRefs(path, text, fileNodeByPath[path], pendingRefs);
            return;
        }
        if (tree is null) return;
        var root = tree.RootNode;

        // Shape-agnostic node-explosion backstop: BEFORE minting a single node, count the definitions this
        // file would contribute (functions/macros/types + file-scope globals). A file over PerFileSymbolBudget
        // is kept WHOLE — the eval-#14 ~20 GB cause was one graph node per #define, and this catches ANY
        // generated shape (huge enum / inline-fn / X-macro tables) that slips the #define-density skip, not
        // just #define-dense ones. Sound: a TU is force-rooted (its code stays); a header rides #include-
        // closure — nothing it defines can be dropped. Only files big enough to POSSIBLY exceed the budget are
        // counted (a captured symbol needs >=2 bytes), so the overwhelming majority of small files pay nothing.
        if (PerFileSymbolBudget > 0 && text.Length >= (long)PerFileSymbolBudget * 2)
        {
            var lines = text.Split('\n');
            var symbols = CountCaptures(_defs, root) + _globals.Execute(root).Captures.Count(c => MintedGlobal(c.Node, path, lines, out _, out _, out _) is not null);
            if (symbols > PerFileSymbolBudget)
            {
                _symbolBudgetKeptWhole.Add((path, symbols));
                var how = IsTranslationUnit(path) ? "force-rooted, kept whole" : "kept whole via #include-closure";
                _warnings.Add($"{path}: would mint {symbols:N0} symbols (> {PerFileSymbolBudget:N0} budget) — {how}, not carved (guards graph-memory blow-up)");
                if (IsTranslationUnit(path)) ForceKeep(path);
                // A translation unit is emitted whole and compiled: every name counts (a handler table has no '(').
                // A generated header over budget is mostly field/register names: its calls are what can need code.
                KeptWholeRefs(path, text, fileNodeByPath[path], pendingRefs, callShapedOnly: !IsTranslationUnit(path));
                return;
            }
        }

        // Scope ranges once per file: pass 1b and pass 4 ask "inside a function body?" per capture (see ScopeIndex).
        var scopes = new ScopeIndex(_scopes, root);
        var fileNode = fileNodeByPath[path];
        var srcLines = text.Split('\n');

        // When configured, code in dead #ifdef branches is skipped (config-specific carve).
        var dead = ExactDeadLines?.Invoke(path, text)
                   ?? (defines is null ? null : PreprocessorScanner.DeadLineMap(text, defines, closedWorldDefines));
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
                if (IsTranslationUnit(path) && IsFileScopeStatic(node)) { _fileLocal.Add(fid); graph.AddFlag(fid, NodeFlags.FileLocal); }
                // `int CAT(TNAME, get)(void) { ... }`: the real name is computed (see TemplateInstances), so nothing
                // calls this one by name. It stays whenever its file does.
                if (_macroNamedHeaders.Contains(path) || (_funcLikeMacroNames.Contains(name) && ComputedName(node)))
                    graph.AddEdge(fileNode, fid, EdgeKind.References);
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
                foreach (var built in PasteMacros.BodyNames(_pasteTable, name))
                    pendingPastes.Add((mid, PasteKind.Exact, built));
            }
            else // class / struct / enum / namespace / type
            {
                var tid = graph.GetOrAddNode(NodeKind.Type, name, path, new SourceSpan(row, row));
                graph.AddEdge(tid, fileNode, EdgeKind.DefinedIn);
            }
        }

        // Pass 1a: a function head split across #if/#else/#endif with the body's '{' after the #endif —
        //   #ifdef VARIANT_SDI / void helper_sdi(int x) / #else / void helper(int x) / #endif / { ... }
        // Neither branch is a definition to tree-sitter (each head is a declaration with a MISSING ';', and the
        // body is a bare top-level compound_statement), so no function was minted and the file was dropped
        // while a call still needed it (work eval, 1.0.159). Every live branch's name is defined by the shared
        // body; they become one unit — same span, linked both ways — so reaching any of them keeps the body.
        foreach (var (names, spanStart, body, clean) in SplitHeadDefinitions(root))
        {
            var live = names.Where(n => !IsDead(n) && !Keywords.Contains(n.Text)).ToList();
            if (live.Count == 0) continue;
            var span = new SourceSpan(spanStart.StartPosition.Row + 1, body.EndPosition.Row + 1);
            var ids = new List<NodeId>();
            foreach (var n in live)
            {
                defNamePositions.Add((n.StartPosition.Row, n.StartPosition.Column));
                var fid = graph.GetOrAddNode(NodeKind.Function, n.Text, path, span);
                graph.AddEdge(fid, fileNode, EdgeKind.DefinedIn);
                Add(functionsByName, n.Text, fid);
                ids.Add(fid);
            }
            foreach (var a in ids)
                foreach (var b in ids)
                    if (a != b) graph.AddEdge(a, b, EdgeKind.Calls);
            if (clean)
                funcSpans.Add((span.StartLine, span.EndLine, ids[0]));
            else
                // The #if block holds more than the heads: removing the span would take that with it. Keep the
                // construct with its file instead (its body's calls are already attributed to the file).
                foreach (var id in ids) graph.AddEdge(fileNode, id, EdgeKind.References);
        }

        // Pass 1b': K&R definitions with bare, undeclared parameters straight into the body — `add(a, b) { ... }`
        // (work eval, 1.0.162). The same text could be a macro-headed body from a header outside the tree, so the
        // name is defined but the body is not made removable: the node is kept with its file (its calls are already
        // the file's). A real one keeps its file when called; a misread macro head only adds an unused name.
        // (Found by the implicit-int scan above; ExpandScopeMacros keeps lines, so they are the original text's.)
        if (bareKAndR is not null)
            foreach (var (name, line, endLine) in bareKAndR)
            {
                if (Keywords.Contains(name) || (dead is not null && line < dead.Length && dead[line])) continue;
                if (functionsByName.TryGetValue(name, out var have) && have.Any(h => graph.GetNode(h).FilePath == path)) continue;
                var fid = graph.GetOrAddNode(NodeKind.Function, name, path, new SourceSpan(line, endLine));
                graph.AddEdge(fid, fileNode, EdgeKind.DefinedIn);
                graph.AddEdge(fileNode, fid, EdgeKind.References);
                Add(functionsByName, name, fid);
            }

        // Pass 1d (backstop): a file whose parse has errors can lose ordinary definitions to error recovery — a
        // construct the grammar can't read swallows the function around or after it (work eval, 1.0.167: three plain
        // `void name ()` definitions inside parse errors). The link check finds definitions with a token scanner that
        // needs no parse; run the same scanner here and define every function it sees that the parse missed. Like a
        // bare K&R head, the node is kept with its file and never carved on its own (the body's calls are already the
        // file's), so a misread costs one extra name, never a cut body.
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (root.HasError) _errorFiles++;
        var scannedData = new List<CodeCarver.Core.Reachability.EmittedLinkCheck.Definition>();
        if (root.HasError)
            foreach (var d in CodeCarver.Core.Reachability.EmittedLinkCheck.Scan(text, header: !IsTranslationUnit(path)).Definitions)
            {
                if (d.Data) { scannedData.Add(d); continue; }   // after pass 1b, which mints what parsed
                if (Keywords.Contains(d.Name) || _funcLikeMacroNames.Contains(d.Name)) continue;
                if (dead is not null && d.Line < dead.Length && dead[d.Line]) continue;
                if (functionsByName.TryGetValue(d.Name, out var have) && have.Any(h => graph.GetNode(h).FilePath == path)) continue;
                var fid = graph.GetOrAddNode(NodeKind.Function, d.Name, path, new SourceSpan(d.Line, d.Line));
                graph.AddEdge(fid, fileNode, EdgeKind.DefinedIn);
                graph.AddEdge(fileNode, fid, EdgeKind.References);
                Add(functionsByName, d.Name, fid);
                if (d.Static && IsTranslationUnit(path)) { _fileLocal.Add(fid); graph.AddFlag(fid, NodeFlags.FileLocal); }
                _recoveredByScan++;
            }
        _tScan += System.Diagnostics.Stopwatch.GetTimestamp() - t2;

        // Pass 1c: symbols a macro use defines — `FW_DECLARE(uart, uart_init, uart_fini);` defining `uart_desc`,
        // or `DEFINE_TASK(blink) { ... }` defining `blink` (see DefinerMacros). Each defined name gets a node
        // spanning the use (and its body), referencing the macro and every identifier in the arguments, so a
        // file elsewhere naming `uart_desc` keeps this file, and this use keeps uart_init. A use that also
        // registers something (a keep macro: section/used/constructor) stays whenever its file does.
        if (_definerUse is not null)
            foreach (var use in DefinerMacros.Uses(text, _definerUse, _definers))
            {
                if (dead is not null && use.StartLine < dead.Length && dead[use.StartLine]) continue;
                var span = new SourceSpan(use.StartLine, use.EndLine);
                var ids = new List<NodeId>();
                foreach (var (name, isFn) in use.Defines)
                {
                    var id = graph.GetOrAddNode(isFn ? NodeKind.Function : NodeKind.Global, name, path, span);
                    graph.AddEdge(id, fileNode, EdgeKind.DefinedIn);
                    Add(isFn ? functionsByName : globalsByName, name, id);
                    pendingRefs.Add((id, use.Macro));
                    foreach (var arg in use.ArgIdentifiers) pendingRefs.Add((id, arg));
                    // A defined OBJECT can have effects nobody references: a C++ `static Registrar n##_reg(...)` registers
                    // at start-up. Before this pass the use was never removable; keep that — an object a use defines
                    // stays whenever its file does (a function it defines is still removable when unused).
                    if (_keepMacros.Contains(use.Macro) || !isFn) graph.AddEdge(fileNode, id, EdgeKind.References);
                    ids.Add(id);
                }
                foreach (var a in ids)
                    foreach (var b in ids)
                        if (a != b) graph.AddEdge(a, b, EdgeKind.Calls);
                funcSpans.Add((use.StartLine, use.EndLine, ids[0]));   // the body's calls belong to the defined name
            }

        // Pass 1b: file-scope variable DEFINITIONS of every shape (scalars, structs, pointers, arrays, initialized
        // or tentative), so a use keeps the file that defines them: a data-only file is otherwise dropped and the
        // carved tree fails to link. Span = the whole declaration, so a big lookup table can be removed wholesale
        // when nothing reachable references it. An `extern` without initializer only declares; a file-scope
        // `static` is its translation unit's own.
        foreach (var cap in _globals.Execute(root).Captures)
        {
            var decl = cap.Node;
            if (IsDead(decl)) continue;
            if (scopes.InBody(decl.StartIndex)) continue; // a LOCAL variable, not a file-scope global
            if (MintedGlobal(decl, path, srcLines, out var isStatic, out var condExtern, out var headerDefinition) is not { } node) continue;
            var gname = node.Text;
            var span = GlobalDeclarationSpan(node);
            if (span is null) continue;
            var gid = graph.GetOrAddNode(NodeKind.Global, gname, path, new SourceSpan(span.Value.Start, span.Value.End));
            graph.AddEdge(gid, fileNode, EdgeKind.DefinedIn);
            Add(globalsByName, gname, gid);
            if (isStatic && IsTranslationUnit(path)) { _fileLocal.Add(gid); graph.AddFlag(gid, NodeFlags.FileLocal); }
            if (headerDefinition) _headerData.Add((gid, path, condExtern));
            // Not removable on its own: a declaration that also defines a named type (`struct cfg {...} dead_cfg;` — the
            // type goes with it), or a C++ object whose constructor can register something at start-up. Only in a unit:
            // a header is never cut, and such an edge there would pull in every unit including it.
            if (IsTranslationUnit(path) && decl.Parent is { } owner && (DefinesType(owner) || CppObject(owner, decl, path)))
                graph.AddEdge(fileNode, gid, EdgeKind.References);
            // What its initializer names, read from the text after the name: a section-placed entry
            // (`const fn_t e __attribute__((section("x"))) = handler;`) doesn't parse as an initializer at all, and
            // the entry is kept by itself (a linker KEEP root) while the file around it may be pruned.
            var inBlock = false;
            var nameCol = node.EndPosition.Column;
            for (var r = span.Value.Start; r <= span.Value.End && r - 1 < srcLines.Length; r++)
            {
                if (dead is not null && r < dead.Length && dead[r]) continue;
                var line = srcLines[r - 1];
                if (line.TrimStart().StartsWith('#')) continue;
                var from = r == node.StartPosition.Row + 1 ? Math.Min(nameCol, line.Length) : 0;
                foreach (var id in LineIdentifiers(StripNonCode(line[from..], ref inBlock)))
                    if (id != gname) pendingRefs.Add((gid, id));
            }
        }

        // Pass 1b (backstop): file-scope data the parse lost to an error — an attribute or SDK macro it can't read
        // (`int v __attribute__((aligned(4)));`, `__IO uint32_t v;`). The link check's scanner names it; the node is kept
        // with its file, so a use elsewhere keeps the file that defines it.
        if (IsTranslationUnit(path))
            foreach (var d in scannedData)
            {
                if (d.Static || Keywords.Contains(d.Name) || (dead is not null && d.Line < dead.Length && dead[d.Line])) continue;
                if (globalsByName.TryGetValue(d.Name, out var have) && have.Any(h => graph.GetNode(h).FilePath == path)) continue;
                var gid = graph.GetOrAddNode(NodeKind.Global, d.Name, path, new SourceSpan(d.Line, d.Line));
                graph.AddEdge(gid, fileNode, EdgeKind.DefinedIn);
                graph.AddEdge(fileNode, gid, EdgeKind.References);
                Add(globalsByName, d.Name, gid);
            }
        if (_condExternFlags.Count > 0 && IsTranslationUnit(path))
        {
            if (defines is not null && _condExternFlags.Any(defines.IsDefined)) _definesExternFlag.Add(path);
            else
                foreach (var l in srcLines)
                    if (l.IndexOf('#') >= 0 && DefineName.Match(l) is { Success: true } dm && _condExternFlags.Contains(dm.Groups[1].Value))
                    { _definesExternFlag.Add(path); break; }
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
            var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            // P1: resolve like the compiler — beside the includer (quoted form), then the TU's -I dirs in order —
            // and only when that finds nothing fall back to every file with the basename (sound, may over-keep).
            var exact = ResolveInclude(path, raw, m.Groups[1].Success, fileNodeByPath);
            IEnumerable<string> targets;
            if (exact.Count > 0) targets = exact;
            else if (pathsByBasename.TryGetValue(BaseName(raw), out var byBase)) targets = byBase;
            else continue;
            foreach (var tp in targets)
                if (tp != path)
                    graph.AddEdge(fileNode, fileNodeByPath[tp], EdgeKind.Includes);
        }

        // Pass 2a: names a template header defines in this translation unit (`#define TNAME red` / `#include "tmpl.h"`
        // with `int CAT(TNAME, get)(void) { ... }` inside). Each instance is a node at the instantiating line, kept
        // with the file (removing that #include would remove every instance), so a call to red_get keeps this unit.
        // The build's function-like -D macros name heads too (`'-DHIDE(n)=hid_##n'`: `int HIDE(den)(void) {`).
        var cmdFnMacros = CodeCarver.Core.Preprocess.CommandLineMacros.AnyFunctionLike(CommandLineMacros);
        // The template's macros may come from any header the unit includes first (a config header), so with a template
        // header in the tree every unit that includes anything is walked.
        if (_read is not null && IsTranslationUnit(path)
            && ((_macroNamedHeaders.Count > 0 && text.Contains("include", StringComparison.Ordinal))
                || (TemplateInstances.HasMacroNamedHead(text) && (text.Contains("define", StringComparison.Ordinal) || cmdFnMacros))))
        {
            string? ReadInclude(string raw)
            {
                var hit = ResolveInclude(path, raw, true, fileNodeByPath);
                if (hit.Count == 0 && pathsByBasename.TryGetValue(BaseName(raw), out var byBase)) hit = byBase;
                var h = hit.FirstOrDefault(_macroNamedHeaders.Contains) ?? hit.FirstOrDefault();
                if (h is null) return null;
                // Same string per header: TemplateInstances reads a header with no template once per unit.
                if (!_includeDirectives.TryGetValue(h, out var t))
                {
                    t = SourceText.Normalize(_read(h));
                    if (!_macroNamedHeaders.Contains(h)) t = TemplateInstances.DirectivesOnly(t);
                    _includeDirectives[h] = t;
                }
                return t;
            }
            foreach (var (name, line) in TemplateInstances.Find(text, ReadInclude, CommandLineMacros))
            {
                if (Keywords.Contains(name) || (dead is not null && line < dead.Length && dead[line])) continue;
                if (functionsByName.TryGetValue(name, out var have) && have.Any(h => graph.GetNode(h).FilePath == path)) continue;
                var iid = graph.GetOrAddNode(NodeKind.Function, name, path, new SourceSpan(line, line));
                graph.AddEdge(iid, fileNode, EdgeKind.DefinedIn);
                graph.AddEdge(fileNode, iid, EdgeKind.References);
                Add(functionsByName, name, iid);
            }
        }

        // Pass 2b: symbol aliases. `void NMI_Handler(void) __attribute__((alias("Default_Handler")))` is a
        // bodyless declaration tree-sitter treats as a prototype (uncaptured), yet it IS a real symbol the
        // vector table points at — and it REQUIRES its target. Register the alias name as a function and
        // link it to the target, so a reference to the alias (e.g. from the vector table) keeps the target.
        IEnumerable<Match> aliases = Array.Empty<Match>();
        // An ifunc (`int f(int) __attribute__((ifunc("resolve_f")))`) is defined here too: by its resolver.
        if (text.Contains("alias", StringComparison.Ordinal) || text.Contains("ifunc", StringComparison.Ordinal))
            aliases =aliases.Concat(AliasAttr.Matches(text)).Concat(AliasAttrBefore.Matches(text));
        if (text.Contains("ragma", StringComparison.Ordinal)) aliases = aliases.Concat(PragmaWeakAlias.Matches(text));   // #pragma and _Pragma
        if (text.Contains("asm", StringComparison.Ordinal)) aliases = aliases.Concat(AsmSetAlias.Matches(text));
        if (_aliasMacroUse is not null) aliases = aliases.Concat(_aliasMacroUse.Matches(text));
        foreach (Match m in aliases)
        {
            var name = m.Groups["name"].Value;
            if (Keywords.Contains(name)) continue;
            var ln = 1;
            for (var k = 0; k < m.Groups["name"].Index && k < text.Length; k++) if (text[k] == '\n') ln++;
            if (dead is not null && ln < dead.Length && dead[ln]) continue;
            var aid = graph.GetOrAddNode(NodeKind.Function, name, path, new SourceSpan(ln, ln));
            graph.AddEdge(aid, fileNode, EdgeKind.DefinedIn);
            Add(functionsByName, name, aid);
            pendingCalls.Add((aid, m.Groups["target"].Value)); // alias -> target: keeping the alias keeps it
        }

        // Pass 2b': symbols the linker sees under another name. An asm label (`int hidden(int) __asm__("sym");`): when
        // the C name has a body here, this file defines sym (sym -> the body); otherwise a call to the C name here is a
        // call to sym (the C name -> sym). Linked with --wrap=X, every call to X goes to __wrap_X: a file defining
        // __wrap_X also stands for X (X -> __wrap_X), so the wrapper stays whenever X is called (pass 3 sends
        // __real_X to X). Over-approximate when the link doesn't wrap: the wrapper is kept for nothing.
        // `#pragma redefine_extname cname sym` renames the same way.
        void LinkerName(string cname, string sym, int index)
        {
            if (Keywords.Contains(cname)) return;
            var ln = 1;
            for (var k = 0; k < index && k < text.Length; k++) if (text[k] == '\n') ln++;
            if (dead is not null && ln < dead.Length && dead[ln]) return;
            var bodyHere = functionsByName.TryGetValue(cname, out var have) && have.Any(h => graph.GetNode(h).FilePath == path);
            var (defined, target) = bodyHere ? (sym, cname) : (cname, sym);
            var lid = graph.GetOrAddNode(NodeKind.Function, defined, path, new SourceSpan(ln, ln));
            graph.AddEdge(lid, fileNode, EdgeKind.DefinedIn);
            Add(functionsByName, defined, lid);
            pendingCalls.Add((lid, target));
        }
        if (text.Contains("asm", StringComparison.Ordinal))
            foreach (Match m in AsmLabelDecl.Matches(text)) LinkerName(m.Groups["name"].Value, m.Groups["sym"].Value, m.Index);
        if (text.Contains("redefine_extname", StringComparison.Ordinal))
            foreach (Match m in RedefineExtname.Matches(text)) LinkerName(m.Groups["name"].Value, m.Groups["sym"].Value, m.Index);
        // A command-line rename (-Dsecret_rite=true_rite): a function defined here as secret_rite is the symbol true_rite.
        if (CommandLineMacros is { Count: > 0 } clm)
            foreach (var (s, e, id) in funcSpans.ToList())
                foreach (var to in CodeCarver.Core.Preprocess.CommandLineMacros.RenamesOf(clm, graph.GetNode(id).Name))
                {
                    var rid = graph.GetOrAddNode(NodeKind.Function, to, path, new SourceSpan(s, e));
                    graph.AddEdge(rid, fileNode, EdgeKind.DefinedIn);
                    Add(functionsByName, to, rid);
                    pendingCalls.Add((rid, graph.GetNode(id).Name));
                }
        // C99 inline: the header holds the body, this translation unit (declaring it extern, or without inline) emits it.
        if (_c99InlineFns.Count > 0 && IsTranslationUnit(path))
            foreach (var (name, index) in C99Inline.Emitters(text, _c99InlineFns)) LinkerName(name, name, index);
        if (text.Contains("__wrap_", StringComparison.Ordinal))
            foreach (var (s, e, id) in funcSpans.ToList())
                if (graph.GetNode(id).Name is { Length: > 7 } wn && wn.StartsWith("__wrap_", StringComparison.Ordinal))
                {
                    var xid = graph.GetOrAddNode(NodeKind.Function, wn[7..], path, new SourceSpan(s, e));
                    graph.AddEdge(xid, fileNode, EdgeKind.DefinedIn);
                    Add(functionsByName, wn[7..], xid);
                    pendingCalls.Add((xid, wn));
                }

        // Pass 2c: inline-asm symbol references. A function/global named only inside asm("...") is kept
        // via the file (attributed to fileNode, like a file-scope address-take), so it survives if this
        // translation unit is kept. Sound over-approximation for the inline-asm blind spot.
        foreach (var id in ScanAsmIdentifiers(text))
            pendingRefs.Add((fileNode, id));
        // A by-name lookup (dlsym(h, "name")): the string is the reference (see DynamicLookup).
        foreach (var (name, _) in DynamicLookup.Names(text))
            pendingRefs.Add((fileNode, name));

        // O(1) enclosing-function lookup. Fill a per-line array with the SMALLEST span covering each line
        // (widest first, so nested/smaller spans overwrite). This turns the per-identifier enclosing
        // lookup below from O(identifiers x functions) into O(lines + identifiers) — the difference
        // between ~18s and <1s on a 9 MB amalgamated header.
        var maxLine = 0;
        foreach (var s in funcSpans) if (s.End > maxLine) maxLine = s.End;
        var byLine = new NodeId?[maxLine + 2];
        foreach (var s in funcSpans.OrderByDescending(s => s.End - s.Start))
            for (var r = s.Start; r <= s.End && r < byLine.Length; r++)
                byLine[r] = s.Id;
        NodeId? Enclosing(int row) => row >= 0 && row < byLine.Length ? byLine[row] : null;

        // Pass 3: direct calls, attributed to the enclosing function by span.
        var calleePositions = new HashSet<(int, int)>();
        foreach (var cap in _calls.Execute(root).Captures)
        {
            // Each Node property is a native call: read position and text once (millions of captures on a big tree).
            var node = cap.Node;
            var sp = node.StartPosition;
            if (dead is not null && sp.Row + 1 < dead.Length && dead[sp.Row + 1]) continue;
            calleePositions.Add((sp.Row, sp.Column));
            // A call not inside any captured function is inside a function we failed to parse (e.g. an
            // unusual macro-prefixed / bare-typedef-return declaration like zlib's `local gzFile gz_open`
            // or `void ZLIB_INTERNAL _tr_flush_block`). That function stays in the emitted file, so keep
            // its callees whenever the file is kept — attribute the call to the file node.
            var from = Enclosing(sp.Row + 1) ?? fileNode;
            var callee = node.Text;
            pendingCalls.Add((from, callee));
            if (callee.Length > 7 && callee.StartsWith("__real_", StringComparison.Ordinal)) pendingCalls.Add((from, callee[7..]));   // --wrap's original
        }

        // Pass 3b: `DESC(uart)` / `CAT(uart, _desc)`: a use of a macro that pastes its own parameters names what it
        // builds. Found in the text, not the parse, so a use in any position counts (a declarator, a parse error).
        if (_pasteUse is { } pasteUse && pasteUse.IsMatch(text))
            foreach (var (line, macro, args) in PasteMacros.Uses(_pasteTable, pasteUse, SourceText.CodeOnly(text)))
            {
                if (dead is not null && line < dead.Length && dead[line]) continue;
                var from = Enclosing(line) ?? fileNode;
                foreach (var (kind, frag) in PasteMacros.UseFragments(_pasteTable, macro, args, _objectMacros.Contains))
                    pendingPastes.Add((from, ToPasteKind(kind), frag));
            }

        // Pass 4: non-call references INSIDE functions (address-taken: a callback passed/assigned).
        // InsideError only matters when the file actually has a parse error somewhere; checking once
        // avoids a costly ancestor walk per file-scope identifier in the common (clean-parse) case.
        var treeHasError = root.HasError;
        var locals = CollectLocals(root, scopes);
        foreach (var cap in _idents.Execute(root).Captures)
        {
            var node = cap.Node;
            var sp = node.StartPosition;
            if (dead is not null && sp.Row + 1 < dead.Length && dead[sp.Row + 1]) continue;
            var pos = (sp.Row, sp.Column);
            if (defNamePositions.Contains(pos) || calleePositions.Contains(pos)) continue;
            var name = node.Text;
            var at = node.StartIndex;
            if (IsLocalUse(locals, name, at)) continue;
            var from = Enclosing(sp.Row + 1);
            if (from is { } f)
                pendingRefs.Add((f, name));
            else if (scopes.InBody(at) || (treeHasError && InsideError(node)))
                // Attribute to the file (kept while the file is), same fallback as pass 3's calls, in two
                // cases the enclosing-function lookup can't see: (a) inside a function body whose signature we couldn't
                // capture — a macro-defined header like janet's `JANET_CORE_FN(os_shell, ...)`, so a
                // callback taken there (`janet_ev_threaded_await(os_shell_subr, ...)`) isn't lost; (b)
                // inside an ERROR subtree — a file-scope function-pointer table tree-sitter couldn't parse
                // because it sits in macro-invocation soup (janet's `OPMETHOD(...)` run before the
                // `JanetMethod x[] = {..., cfun_..., ...}` table). Both are real misparses, not the
                // clean-parsing file-scope prototype that must NOT be swept in (it would keep every
                // declared function). Over-approximation bounded to the misparsed region — sound.
                pendingRefs.Add((fileNode, name));
        }

        // Pass 5: function names used as DATA at FILE SCOPE (tables, hooks, registries). Attributed to
        // the file node so keeping the file keeps everything its initializers point at. For a whole
        // initializer_list we scan its span (catching #if-split entries); single `= fn` values match
        // the @ref patterns directly.
        foreach (var cap in _initRefs.Execute(root).Captures)
        {
            if (IsDead(cap.Node)) continue;
            var startRow = cap.Node.StartPosition.Row + 1;
            if (Enclosing(startRow) is not null) continue; // file scope only

            if (cap.Name == "il")
            {
                // Scan only what's INSIDE the braces (using the node's columns), so the declaration
                // prefix on the first line (`static T name[] = {`) isn't read — otherwise the variable's
                // own name would count as a reference to itself and it could never be pruned.
                var startCol = cap.Node.StartPosition.Column;
                var endRow = cap.Node.EndPosition.Row + 1;
                var endCol = cap.Node.EndPosition.Column;
                var inBlock = false; // /* */ state carried across the initializer's lines
                for (var r = startRow; r <= endRow && r - 1 < srcLines.Length; r++)
                {
                    var line = srcLines[r - 1];
                    if (line.TrimStart().StartsWith('#')) continue;             // skip #if/#endif lines
                    if (dead is not null && r < dead.Length && dead[r]) continue; // skip dead entries
                    var from = r == startRow ? Math.Min(startCol, line.Length) : 0;
                    var to = r == endRow ? Math.Min(endCol, line.Length) : line.Length;
                    if (from < to)
                        // Blank comments and string/char literals first: a symbol named only in a comment
                        // (`/* used by foo */`) or string ("foo") is NOT a real reference, and treating it
                        // as one keeps dead code (a common table pattern with per-entry comments).
                        foreach (var id in LineIdentifiers(StripNonCode(line[from..to], ref inBlock)))
                            pendingRefs.Add((fileNode, id));
                }
            }
            else
            {
                pendingRefs.Add((fileNode, cap.Node.Text));
            }
        }
    }

    /// <summary>Count a query's captures over the tree WITHOUT materializing them — used by the per-file
    /// symbol budget to decide keep-whole before any node is allocated. Enumeration only (no list), so the
    /// gate never itself allocates the explosion it exists to prevent.</summary>
    private static int CountCaptures(Query q, TsNode root)
    {
        var n = 0;
        foreach (var _ in q.Execute(root).Captures) n++;
        return n;
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

    /// <summary>Blank out // and /* */ comments and string/char-literal contents on a line, so a symbol
    /// name that appears only in a comment or a string is not mistaken for a code reference. <paramref
    /// name="inBlock"/> carries <c>/* */</c> state across the lines of a multi-line initializer.</summary>
    private static string StripNonCode(string s, ref bool inBlock)
    {
        var sb = new StringBuilder(s.Length);
        var quote = '\0';
        for (var c = 0; c < s.Length; c++)
        {
            var ch = s[c];
            if (inBlock)
            {
                if (ch == '*' && c + 1 < s.Length && s[c + 1] == '/') { inBlock = false; c++; }
            }
            else if (quote != '\0')
            {
                if (ch == '\\') c++;                       // skip an escaped char (\" \\ \')
                else if (ch == quote) quote = '\0';
            }
            else if (ch == '/' && c + 1 < s.Length && s[c + 1] == '/') break;      // line comment: rest is dead
            else if (ch == '/' && c + 1 < s.Length && s[c + 1] == '*') { inBlock = true; c++; }
            else if (ch is '"' or '\'') quote = ch;
            else sb.Append(ch);
        }
        return sb.ToString();
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

    private static PasteKind ToPasteKind(PasteMacros.Kind k)
        => k switch { PasteMacros.Kind.Prefix => PasteKind.Prefix, PasteMacros.Kind.Suffix => PasteKind.Suffix, _ => PasteKind.Exact };

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
    private (int Start, int End)? DefinitionSpan(TsNode nameNode)
    {
        // A "function" whose name is a FUNCTION-LIKE macro is never a real definition — a macro invocation
        // like fmt's `FMT_CATCH(...) {}` (catch (x)) parsed as one; capturing it truncates the real
        // enclosing function and prunes the macro line (shattering try/catch). A real function can't share
        // a name with a function-like macro (the preprocessor would mangle its definition), so nothing
        // real is lost. Object-like RENAME macros (`#define adler32 z_adler32`, zlib Z_PREFIX) are NOT in
        // this set — they legitimately coincide with a real function name, and rejecting those dropped
        // the real function and broke the link (a bug the map oracle caught).
        var macroNamed = _funcLikeMacroNames.Contains(nameNode.Text);

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
                // A name that is ALSO a function-like macro somewhere in the tree: in a multi-target tree that is
                // often another target's macro, and the real function here must not be lost (review N1). Reject
                // only the misparse shape the rule exists for — a macro invocation `FMT_CATCH(x) {}`: no return
                // type, call-shaped "parameters", or nested inside a body.
                if (macroNamed && (ChildForField(parent, "type") is null || HasCallShapedParameters(parent)
                                   || InsideFunctionBody(parent))) return null;
                var start = parent.StartPosition.Row + 1;
                // Include a leading `template<...>` (possibly several, nested) so pruning a templated
                // function/method removes the whole thing — otherwise the `template<int N>` line is left
                // orphaned above the deleted body: `template<int N>\n };` → "expected unqualified-id".
                var tp = parent.Parent;
                while (tp is not null && tp.Type == "template_declaration") { start = tp.StartPosition.Row + 1; tp = tp.Parent; }
                return (start, parent.EndPosition.Row + 1);
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
    /// <summary>True if any ancestor (up to the translation unit) is a parse ERROR node — the node sits
    /// in a region tree-sitter couldn't parse (e.g. a file-scope table buried in macro-invocation soup).
    /// Used to over-keep function references there rather than silently lose them.</summary>
    private static bool InsideError(TsNode n)
    {
        var p = n.Parent;
        for (var i = 0; i < 48 && p is not null; i++)
        {
            if (p.IsError || p.Type == "ERROR") return true;
            if (p.Type == "translation_unit") return false;
            p = p.Parent;
        }
        return false;
    }

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

    public void Dispose()
    {
        _inlineParser?.Dispose();
        _locals.Dispose();
        _globals.Dispose();
        _initRefs.Dispose();
        _idents.Dispose();
        _calls.Dispose();
        _defs.Dispose();
        _lang.Dispose();
    }
}
