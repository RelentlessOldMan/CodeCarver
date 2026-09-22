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

/// <summary>Unions several providers into one root set.</summary>
public sealed class CompositeRootProvider : IRootProvider
{
    private readonly IReadOnlyList<IRootProvider> _providers;
    public CompositeRootProvider(params IRootProvider[] providers) => _providers = providers;

    public IEnumerable<Root> Discover(CodeGraph graph)
        => _providers.SelectMany(p => p.Discover(graph));
}
