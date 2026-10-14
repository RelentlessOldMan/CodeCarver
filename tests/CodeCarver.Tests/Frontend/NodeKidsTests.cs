using System.Text;
using CodeCarver.Frontend;
using TreeSitter;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// The parse lists children with a cursor and finds parents with a binary search, both because tree-sitter's own calls
/// are slow in a big file with parse errors. They must give exactly tree-sitter's answer on every node: the nodes it
/// would list, in its order, and the parent it would return — in a file full of errors, MISSING nodes and wide nodes.
/// </summary>
public sealed class NodeKidsTests
{
    static string ErrorHeavy()
    {
        var sb = new StringBuilder("#include \"sdk.h\"\n");
        for (var i = 0; i < 120; i++)
        {
            sb.Append($"MODK_SECTION(\".fast\") MODK_INLINE void fn_{i}(MODK_T *p) MODK_ATTR {{\n");
            for (var k = 0; k < 3; k++)
                sb.Append($"    MODK_WRITE(p->reg_{k}, MODK_FIELD({i}, {k})); if (p->st & MODK_BIT({k})) {{ tbl_{i % 5}[{k}] = fn_{i}; }}\n");
            sb.Append($"#if MODK_CFG_{i % 7}\n    p->x = 1 MODK_ODD;\n#else\n    p->x = 2;\n#endif\n}}\n");
            sb.Append($"MODK_TABLE_ENTRY(fn_{i}, {i}) MODK_WEIRD {{ 1, 2, 3 }};\n");
            sb.Append((i % 3) switch
            {
                0 => $"#ifdef MODK_A\nvoid head_a{i}(int x)\n#else\nvoid head_b{i}(int x)\n#endif\n{{ return; }}\n",
                1 => $"int modt_{i} = ;\nstatic int modd_{i}[] = {{ 1, 2, \n",
                _ => $"void (*cb_{i})(int) = fn_{i};\nstruct modk_s{i} {{ int a; int b }};\n",
            });
        }
        return sb.ToString();
    }

    static IEnumerable<Node> Every(Node root)
    {
        using var cur = new TreeCursor(root);
        var visit = true;
        while (true)
        {
            if (visit)
            {
                yield return cur.CurrentNode;
                if (cur.GotoFirstChild()) continue;
            }
            if (cur.GotoNextSibling()) { visit = true; continue; }
            if (!cur.GotoParent()) yield break;
            visit = false;
        }
    }

    [Theory]
    [InlineData("tree-sitter-c", "tree_sitter_c")]
    [InlineData("tree-sitter-cpp", "tree_sitter_cpp")]
    public void ChildrenAndParents_AreTreeSittersOwn_OnEveryNode(string lib, string fn)
    {
        var lang = new Language(lib, fn);
        using var parser = new Parser(lang);
        using var tree = parser.Parse(ErrorHeavy())!;
        var root = tree.RootNode;
        Assert.True(root.HasError);
        var finder = new ParentFinder(root);
        int nodes = 0, missing = 0, wide = 0;
        foreach (var n in Every(root))
        {
            nodes++;
            if (n.IsMissing) missing++;
            var all = n.Children.ToList();
            if (all.Count >= ParentFinder.WideChildren) wide++;
            Assert.Equal(all.Select(c => c.Id), NodeKids.All(n).Select(c => c.Id));
            Assert.Equal(n.NamedChildren.Select(c => c.Id), NodeKids.Named(n).Select(c => c.Id));
            Assert.Equal(n.Parent?.Id, finder.ParentOf(n)?.Id);
        }
        // The file has the shapes the search must get right: many nodes, MISSING ones, and wide ones.
        Assert.True(nodes > 10_000, $"{nodes} nodes");
        Assert.True(missing > 0, "no MISSING node");
        Assert.True(wide > 0, "no wide node");
    }
}
