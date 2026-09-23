using System.Text.RegularExpressions;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Produces the seed set for a carve. Because a single deterministic pass is only as complete as its
/// roots, this is where the embedded gotchas are caught — the implicit roots a from-<c>main</c>
/// closure would silently drop:
///
///  • the interrupt/exception <b>vector table</b> (ISRs are jumped to by address, never called);
///  • <c>.init_array</c> / static <b>constructors</b> (run before main);
///  • <b>KEEP()</b>/used/retain-forced and <b>exported</b> symbols.
///
/// Well-known shapes (vector table, init arrays) are auto-discovered by concrete providers; anything
/// exotic the user declares explicitly. A composite provider unions several sources.
/// </summary>
public interface IRootProvider
{
    IEnumerable<Root> Discover(CodeGraph graph);
}

/// <summary>Roots the user named directly: symbols (foo/bar/woot) and whole files to retain.</summary>
public sealed class ExplicitRootProvider : IRootProvider
{
    private readonly IReadOnlyList<string> _symbols;
    private readonly IReadOnlyList<string> _files;

    public ExplicitRootProvider(IEnumerable<string>? symbols = null, IEnumerable<string>? files = null)
    {
        _symbols = symbols?.ToList() ?? new List<string>();
        _files = files?.ToList() ?? new List<string>();
    }

    public IEnumerable<Root> Discover(CodeGraph graph)
    {
        var wantSym = new HashSet<string>(_symbols, StringComparer.Ordinal);
        var wantFile = new HashSet<string>(_files, StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (node.Kind != NodeKind.File && wantSym.Contains(node.Name))
                yield return new Root(node.Id, RootKind.ExplicitSymbol, node.Name);
            else if (node.FilePath is { } f && wantFile.Contains(f))
                yield return new Root(node.Id, RootKind.ExplicitFile, f);
        }
    }
}

/// <summary>
/// Auto-discovers implicit roots the linker/runtime keep regardless of any call: functions/globals the
/// front-end flagged <see cref="NodeFlags.Keep"/> from a <c>constructor</c>/<c>destructor</c>/<c>used</c>/
/// <c>retain</c> attribute or placement in an <c>.init_array</c>-family section. A from-<c>main</c>
/// closure silently drops these (a self-registering driver, an initcall entry) — keeping them is the
/// sound over-approximation.
/// </summary>
public sealed class AttributeRootProvider : IRootProvider
{
    public IEnumerable<Root> Discover(CodeGraph graph)
    {
        foreach (var node in graph.Nodes)
            if (node.Kind is NodeKind.Function or NodeKind.Global && (node.Flags & NodeFlags.Keep) != 0)
                yield return new Root(node.Id, RootKind.LinkerKeep, node.Name);
    }
}

/// <summary>
/// Roots every in-scope C symbol named in a standalone assembly file (<c>.s</c>/<c>.S</c>). Embedded
/// startup keeps the vector table and reset handler in assembly (<c>startup_*.s</c>): the table is
/// <c>.word Handler</c> entries and code does <c>bl func</c> — pure symbol references with no C-level
/// edge, so a from-<c>main</c> closure drops the handlers. Over-approximates (every identifier token,
/// incl. mnemonics/registers); only tokens that match a defined symbol become roots.
/// </summary>
public sealed class AsmReferenceRootProvider : IRootProvider
{
    private static readonly Regex Ident = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);
    private readonly IReadOnlyList<string> _asmTexts;
    public AsmReferenceRootProvider(IEnumerable<string> asmTexts) => _asmTexts = asmTexts.ToList();

    public IEnumerable<Root> Discover(CodeGraph graph)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _asmTexts)
            foreach (Match m in Ident.Matches(t))
                names.Add(m.Value);

        foreach (var node in graph.Nodes)
            if (node.Kind is NodeKind.Function or NodeKind.Global && names.Contains(node.Name))
                yield return new Root(node.Id, RootKind.LinkerKeep, node.Name);
    }
}

/// <summary>
/// Roots C symbols placed in a custom section that a linker script keeps with <c>KEEP(...)</c>. The
/// compiler emits such a symbol into its <c>__attribute__((section("x")))</c> section, but nothing
/// <i>calls</i> it — an initcall / driver-registration / command table entry, collected only by the
/// linker walking the section. It is <c>KEEP(*(x))</c> in the linker script precisely because
/// <c>--gc-sections</c> would otherwise discard it, and (unlike a <c>used</c> symbol) there is no
/// C-level edge to reach it from <c>main</c>. Without this it is silently dropped and the image loses
/// the entry. Generalises the always-kept <c>.init_array</c> family to <b>any</b> KEEP'd section named
/// by the tree's own linker script. Over-approximates deliberately: any symbol whose section matches a
/// KEEP'd pattern is rooted — extra keeps only cost a little size, they never break the build.
/// </summary>
public sealed class LinkerSectionRootProvider : IRootProvider
{
    // Grab the input-section pattern(s) inside a KEEP(...): everything up to the first ')' after KEEP(
    // — for KEEP(*(.foo)) that's "*(.foo", for KEEP(*(SORT(.foo.*))) that's "*(SORT(.foo.*", from which
    // the dotted section tokens are then extracted. Robust to the usual nesting without balancing parens.
    private static readonly Regex KeepDirective = new(@"KEEP\s*\((?<body>[^)]*)", RegexOptions.Compiled);
    private static readonly Regex SectionToken = new(@"\.[A-Za-z_][\w.*?\[\]\-]*", RegexOptions.Compiled);

    private static readonly Regex AttrBlock = new(
        @"__attribute__\s*\(\((?<body>(?:[^()]|\([^()]*\))*)\)\)", RegexOptions.Compiled);
    private static readonly Regex SectionAttr = new(
        @"section\s*\(\s*""(?<s>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex NameBeforeAttr = new(  // trailing:  name / name[...]  __attribute__
        @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\]|\([^()]*\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex NameAfterAttr = new(   // leading:   __attribute__ ... [type] name
        @"^\s*(?:[A-Za-z_][\w*]*[\s*]+)*?([A-Za-z_]\w*)\s*(?:[\(\[=;,]|$)", RegexOptions.Compiled);

    private readonly IReadOnlyList<string> _sourceTexts;
    private readonly IReadOnlyList<string> _linkerScriptTexts;

    public LinkerSectionRootProvider(IEnumerable<string> sourceTexts, IEnumerable<string> linkerScriptTexts)
    {
        _sourceTexts = sourceTexts.ToList();
        _linkerScriptTexts = linkerScriptTexts.ToList();
    }

    public IEnumerable<Root> Discover(CodeGraph graph)
    {
        var kept = KeptSectionMatchers(_linkerScriptTexts);
        if (kept.Count == 0) yield break;

        // symbol name -> the section it is placed in that a linker script KEEPs.
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in _sourceTexts)
            foreach (var (symbol, section) in SectionPlacements(text))
                if (!wanted.ContainsKey(symbol) && kept.Any(re => re.IsMatch(section)))
                    wanted[symbol] = section;

        if (wanted.Count == 0) yield break;
        foreach (var node in graph.Nodes)
            if (node.Kind is NodeKind.Function or NodeKind.Global && wanted.TryGetValue(node.Name, out var sec))
                yield return new Root(node.Id, RootKind.LinkerKeep, $"{node.Name} in KEEP section {sec}");
    }

    /// <summary>Every KEEP'd input-section pattern in the linker scripts, as an anchored glob-matcher
    /// (<c>*</c>→any, <c>?</c>→one). A malformed KEEP is skipped, not guessed at — the baseline roots
    /// still hold, so this can only add keeps, never remove a needed one.</summary>
    private static List<Regex> KeptSectionMatchers(IEnumerable<string> linkerScriptTexts)
    {
        var matchers = new List<Regex>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in linkerScriptTexts)
            foreach (Match keep in KeepDirective.Matches(text))
                foreach (Match tok in SectionToken.Matches(keep.Groups["body"].Value))
                    if (seen.Add(tok.Value))
                        matchers.Add(GlobToRegex(tok.Value));
        return matchers;
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.Compiled);
    }

    /// <summary>(symbol, section) for every <c>__attribute__((section("x")))</c> placement, resolving the
    /// decorated symbol whether the attribute leads or trails the declaration.</summary>
    private static IEnumerable<(string Symbol, string Section)> SectionPlacements(string text)
    {
        foreach (Match m in AttrBlock.Matches(text))
        {
            var sm = SectionAttr.Match(m.Groups["body"].Value);
            if (!sm.Success) continue;
            var section = sm.Groups["s"].Value;

            var before = text.AsSpan(0, m.Index);
            var mb = NameBeforeAttr.Match(before.Length > 200 ? before[^200..].ToString() : before.ToString());
            if (mb.Success) { yield return (mb.Groups[1].Value, section); continue; }

            var after = text.AsSpan(m.Index + m.Length);
            var ma = NameAfterAttr.Match(after.Length > 200 ? after[..200].ToString() : after.ToString());
            if (ma.Success) yield return (ma.Groups[1].Value, section);
        }
    }
}

/// <summary>
/// Roots C++ constructors — a function whose name is a class/struct type name. A constructor runs on
/// every instantiation (<c>T x;</c>, <c>T{}</c>, a static/global instance, <c>new T</c>, a base or
/// member of another constructed class) — none of which is a traceable call, so a constructor always
/// looks unreachable. It cannot be pruned (removing it makes the class's implicit default constructor
/// ill-formed), and because it IS emitted, whatever it calls in its member-initializer list / body must
/// be kept too. Rooting constructors makes the closure sound: the constructor and its callees survive.
/// Over-approximates (keeps every class's constructor + its init dependencies) — the sound price of not
/// modelling instantiation. In C this only fires on the rare function-named-like-a-struct (harmless).
/// </summary>
public sealed class ConstructorRootProvider : IRootProvider
{
    public IEnumerable<Root> Discover(CodeGraph graph)
    {
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
            if (node.Kind == NodeKind.Type) typeNames.Add(node.Name);
        if (typeNames.Count == 0) yield break;

        foreach (var node in graph.Nodes)
            if (node.Kind == NodeKind.Function && typeNames.Contains(node.Name))
                yield return new Root(node.Id, RootKind.LinkerKeep, $"constructor {node.Name}");
    }
}

/// <summary>Unions several providers into one root set.</summary>
public sealed class CompositeRootProvider : IRootProvider
{
    private readonly IReadOnlyList<IRootProvider> _providers;
    public CompositeRootProvider(params IRootProvider[] providers) => _providers = providers;

    public IEnumerable<Root> Discover(CodeGraph graph)
        => _providers.SelectMany(p => p.Discover(graph));
}
