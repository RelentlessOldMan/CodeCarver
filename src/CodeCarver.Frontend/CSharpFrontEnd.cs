using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;
using TreeSitter;
using TsNode = TreeSitter.Node;

namespace CodeCarver.Frontend;

/// <summary>
/// C# front-end (tree-sitter, syntactic) for FILE-LEVEL carving — dropping .cs files no reached method
/// lives in. Methods/constructors become function nodes and invocations/new-expressions become call
/// edges, resolved by NAME across the input (a call to <c>Foo()</c> keeps every <c>Foo</c> — sound
/// over-approximation). It ignores preprocessor defines (C# #if is rarely load-bearing here).
///
/// Deliberately file-level: sound intra-file method pruning in C# needs semantic analysis (overload
/// resolution, interfaces, reflection) that only a real compiler front-end (Roslyn) provides. So
/// `--prune` is not applied to C#; this answers "which files does the build need for these entry
/// points" — already a big win on a large solution.
/// </summary>
public sealed class CSharpFrontEnd : ICarveFrontEnd
{
    private const string DefsQuery = """
        (method_declaration name: (identifier) @function)
        (constructor_declaration name: (identifier) @function)
        (local_function_statement name: (identifier) @function)
        (class_declaration name: (identifier) @type)
        (struct_declaration name: (identifier) @type)
        (interface_declaration name: (identifier) @type)
        (enum_declaration name: (identifier) @type)
        (record_declaration name: (identifier) @type)
        """;

    private const string CallsQuery = """
        (invocation_expression function: (identifier) @callee)
        (invocation_expression function: (member_access_expression name: (identifier) @callee))
        (object_creation_expression type: (identifier) @callee)
        """;

    private readonly Language _lang;
    private readonly Query _defs;
    private readonly Query _calls;

    public CSharpFrontEnd()
    {
        _lang = new Language("tree-sitter-c-sharp.dll", "tree_sitter_c_sharp");
        _defs = new Query(_lang, DefsQuery);
        _calls = new Query(_lang, CallsQuery);
    }

    public CodeGraph BuildGraph(IEnumerable<(string Path, string Text)> files,
                                MacroTable? defines = null, bool closedWorldDefines = false)
    {
        var graph = new CodeGraph();
        var functionsByName = new Dictionary<string, List<NodeId>>(StringComparer.Ordinal);
        var pending = new List<(NodeId From, string Name)>();

        foreach (var (path, text) in files)
            if (text.Length > 0) // oversized/empty-file guard (C# has no include-closure so the CLI never skips .cs)
                ProcessFile(graph, path, text, functionsByName, pending);

        foreach (var (from, name) in pending)
            if (functionsByName.TryGetValue(name, out var targets))
                foreach (var t in targets)
                    graph.AddEdge(from, t, EdgeKind.Calls);

        return graph;
    }

    private void ProcessFile(CodeGraph graph, string path, string text,
                             Dictionary<string, List<NodeId>> functionsByName,
                             List<(NodeId, string)> pending)
    {
        using var parser = new Parser(_lang);
        using var tree = parser.Parse(text);
        if (tree is null) return;
        var root = tree.RootNode;

        var methodSpans = new List<(int Start, int End, NodeId Id)>();

        foreach (var cap in _defs.Execute(root).Captures)
        {
            var node = cap.Node;
            var row = node.StartPosition.Row + 1;
            if (cap.Name == "function")
            {
                var span = MethodSpan(node) ?? (row, row);
                var id = graph.GetOrAddNode(NodeKind.Function, node.Text, path, new SourceSpan(span.Item1, span.Item2));
                if (!functionsByName.TryGetValue(node.Text, out var list))
                    functionsByName[node.Text] = list = new List<NodeId>();
                list.Add(id);
                methodSpans.Add((span.Item1, span.Item2, id));
            }
            else
            {
                graph.GetOrAddNode(NodeKind.Type, node.Text, path, new SourceSpan(row, row));
            }
        }

        foreach (var cap in _calls.Execute(root).Captures)
        {
            var from = Enclosing(methodSpans, cap.Node.StartPosition.Row + 1);
            if (from is { } f)
                pending.Add((f, cap.Node.Text));
        }
    }

    private static (int, int)? MethodSpan(TsNode nameNode)
    {
        var n = nameNode;
        for (var i = 0; i < 10; i++)
        {
            var p = n.Parent;
            if (p is null) break;
            if (p.Type is "method_declaration" or "constructor_declaration" or "local_function_statement")
                return (p.StartPosition.Row + 1, p.EndPosition.Row + 1);
            n = p;
        }
        return null;
    }

    private static NodeId? Enclosing(List<(int Start, int End, NodeId Id)> spans, int row)
    {
        NodeId? best = null;
        var width = int.MaxValue;
        foreach (var (s, e, id) in spans)
            if (row >= s && row <= e && e - s < width) { width = e - s; best = id; }
        return best;
    }

    public void Dispose()
    {
        _calls.Dispose();
        _defs.Dispose();
        _lang.Dispose();
    }
}
