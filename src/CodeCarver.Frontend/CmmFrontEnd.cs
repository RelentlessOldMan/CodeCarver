using System.Text.RegularExpressions;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;

namespace CodeCarver.Frontend;

/// <summary>
/// TRACE32 PRACTICE (.cmm) front-end. Deliberately TEXT-based (line/keyword scan, no grammar
/// dependency), which makes it robust to PRACTICE's many quirks. It models:
///   • subroutines — a <c>Label:</c> at line start (called via <c>GOSUB Label</c>, ended by RETURN);
///   • calls — <c>GOSUB</c> / <c>GOTO</c> to a label;
///   • includes — <c>DO script</c> runs another .cmm (resolved by basename).
/// A subroutine "owns" the lines from its label to the next label, so an unreached one can be pruned.
///
/// .cmm is interpreted, not compiled, so there's no gcc build-verify; it's validated by extraction
/// correctness against real scripts. Known blind spot (like C function pointers): a computed
/// <c>GOSUB &amp;var</c> whose target we can't resolve — those keep the referenced label conservatively
/// only if it's otherwise reached, so prefer file-level carving where dynamic dispatch is heavy.
/// </summary>
public sealed class CmmFrontEnd : ICarveFrontEnd
{
    private static readonly Regex LabelDef = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*):(?:\s|$)", RegexOptions.Compiled);
    private static readonly Regex SubroutineDef = new(@"^\s*SUBROUTINE\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Gosub = new(@"\bGOSUB\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Goto = new(@"\bGOTO\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DoCmd = new(@"\bDO\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public CodeGraph BuildGraph(IEnumerable<(string Path, string Text)> files,
                                MacroTable? defines = null, bool closedWorldDefines = false)
    {
        var graph = new CodeGraph();
        var inputs = files.ToList();

        var fileNodeByPath = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var fileNodeByStem = new Dictionary<string, NodeId>(StringComparer.OrdinalIgnoreCase); // basename w/o .cmm, for DO
        foreach (var (path, _) in inputs)
        {
            var fn = graph.GetOrAddNode(NodeKind.File, path);
            fileNodeByPath[path] = fn;
            fileNodeByStem[Stem(path)] = fn;
        }

        var subsByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var pendingCalls = new List<(NodeId From, string Name)>();  // GOSUB/GOTO
        var pendingIncludes = new List<(NodeId From, string Stem)>(); // DO

        foreach (var (path, text) in inputs)
            if (text.Length > 0) // oversized/empty file: File node already registered; nothing to parse
                ProcessFile(graph, path, text, fileNodeByPath[path], subsByName, pendingCalls, pendingIncludes);

        foreach (var (from, name) in pendingCalls)
            if (subsByName.TryGetValue(name, out var targets))
                foreach (var t in targets) graph.AddEdge(from, t, EdgeKind.Calls);

        foreach (var (from, stem) in pendingIncludes)
            if (fileNodeByStem.TryGetValue(stem, out var target) && !target.Equals(from))
                graph.AddEdge(from, target, EdgeKind.Includes);

        return graph;
    }

    private void ProcessFile(CodeGraph graph, string path, string text, NodeId fileNode,
                             Dictionary<string, List<NodeId>> subsByName,
                             List<(NodeId, string)> pendingCalls,
                             List<(NodeId, string)> pendingIncludes)
    {
        var lines = text.Split('\n');

        // Pass 1: subroutine definitions + their spans (def line .. line before the next def). PRACTICE
        // has two styles: a classic `Label:` and a newer `SUBROUTINE Name ( ... )` block.
        var labels = new List<(int Line, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var lm = LabelDef.Match(lines[i]);
            if (lm.Success) { labels.Add((i + 1, lm.Groups[1].Value)); continue; }
            var sm = SubroutineDef.Match(lines[i]);
            if (sm.Success) labels.Add((i + 1, sm.Groups[1].Value));
        }
        var spans = new List<(int Start, int End, NodeId Id)>();
        for (var k = 0; k < labels.Count; k++)
        {
            var start = labels[k].Line;
            var end = k + 1 < labels.Count ? labels[k + 1].Line - 1 : lines.Length;
            var id = graph.GetOrAddNode(NodeKind.Function, labels[k].Name, path, new SourceSpan(start, end));
            graph.AddEdge(id, fileNode, EdgeKind.DefinedIn);
            if (!subsByName.TryGetValue(labels[k].Name, out var list))
                subsByName[labels[k].Name] = list = new List<NodeId>();
            list.Add(id);
            spans.Add((start, end, id));
        }

        // Pass 2: GOSUB/GOTO/DO, attributed to the enclosing subroutine (or the file for top-level code).
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Length == 0) continue;
            var from = Enclosing(spans, i + 1) ?? fileNode;

            foreach (Match g in Gosub.Matches(line)) pendingCalls.Add((from, g.Groups[1].Value));
            foreach (Match g in Goto.Matches(line)) pendingCalls.Add((from, g.Groups[1].Value));
            foreach (Match d in DoCmd.Matches(line)) pendingIncludes.Add((from, Stem(d.Groups[1].Value)));
        }
    }

    /// <summary>The innermost subroutine whose span contains the line, or null for top-level code.</summary>
    private static NodeId? Enclosing(List<(int Start, int End, NodeId Id)> spans, int line)
    {
        foreach (var (s, e, id) in spans)
            if (line >= s && line <= e) return id;
        return null;
    }

    /// <summary>Basename without directory/&amp;-vars or the .cmm extension (for DO include resolution).</summary>
    private static string Stem(string s)
    {
        var slash = s.LastIndexOfAny(new[] { '/', '\\' });
        var name = slash >= 0 ? s[(slash + 1)..] : s;
        if (name.EndsWith(".cmm", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    private static string StripComment(string line)
    {
        var semi = line.IndexOf(';');
        var slashes = line.IndexOf("//", StringComparison.Ordinal);
        var cut = (semi, slashes) switch
        {
            (< 0, < 0) => -1,
            (< 0, _) => slashes,
            (_, < 0) => semi,
            _ => Math.Min(semi, slashes),
        };
        return cut < 0 ? line : line[..cut];
    }

    public void Dispose() { }
}
