using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Proves the C++ front-end: methods, and virtual dispatch kept soundly via name resolution.</summary>
public class CppFrontEndTests
{
    private const string ShapesCpp = """
        struct Shape {
            virtual int area() const { return 0; }
        };

        struct Circle : Shape {
            int area() const override { return 3; }
        };

        struct Square : Shape {
            int area() const override { return 4; }
        };

        struct Widget {
            void draw() const { }
        };

        int total(Shape* s) {
            return s->area();
        }
        """;

    [Fact]
    public void VirtualCall_KeepsEveryOverride_DropsUnrelated()
    {
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("shapes.cpp", ShapesCpp) });

        // Three distinct area() methods should exist (not merged into one node).
        var areas = graph.Nodes.Count(n => n.Kind == NodeKind.Function && n.Name == "area");
        Assert.Equal(3, areas);

        var total = graph.Nodes.First(n => n.Name == "total").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(total, RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(total));
        // Every area() override is kept (sound over-approximation of virtual dispatch).
        Assert.Equal(3, graph.Nodes.Count(n => n.Name == "area" && plan.IsKept(n.Id)));
        // The unrelated method is carved.
        var draw = graph.Nodes.First(n => n.Name == "draw").Id;
        Assert.False(plan.IsKept(draw));
    }

    [Fact]
    public void ExtractsFreeFunctionsAndMethods()
    {
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("shapes.cpp", ShapesCpp) });

        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Function && n.Name == "total");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Type && n.Name == "Shape");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Type && n.Name == "Widget");
    }

    [Fact]
    public void MacroOpenedNamespace_FunctionsAreCaptured_AndReachable()
    {
        // Serious C++ libraries open their namespace with a MACRO (fmt's FMT_BEGIN_NAMESPACE,
        // pugixml's PUGI_IMPL_NS_BEGIN → `namespace fmt {`). tree-sitter can't see through it, so the
        // whole file mis-parses and NO function is captured — the carve can't even find its roots. The
        // front-end expands such scope-opening macros for parsing (line-preserving). The #define lives in
        // one file, the use in another — mirroring the header/impl split.
        const string hdr = """
            #define LIB_BEGIN namespace lib { inline namespace v1 {
            #define LIB_END } }
            """;
        const string impl = """
            #include "lib_macros.h"
            LIB_BEGIN
            static int helper() { return 1; }
            int entry() { return helper(); }
            static int dead() { return 9; }
            LIB_END
            """;
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("lib_macros.h", hdr), ("impl.cpp", impl) });
        var entry = graph.Nodes.First(n => n.Name == "entry" && n.Kind == NodeKind.Function).Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(entry, RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(entry));                                          // root captured despite macro ns
        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "helper").Id));  // reached entry -> helper
        Assert.False(plan.IsKept(graph.Nodes.First(n => n.Name == "dead").Id));   // unreached -> carved
    }

    [Fact]
    public void ValueMacroWithBalancedBraces_IsNotExpanded()
    {
        // A value macro (compound literal `((V){ 0 })`) has BALANCED braces — it must NOT be treated as a
        // scope opener and expanded, or it corrupts a parse that already works (the wren regression).
        const string src = """
            struct V { int tag; };
            #define ZERO_V ((V){ 0 })
            static V make() { return ZERO_V; }
            int entry() { return make().tag; }
            """;
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("v.cpp", src) });
        var entry = graph.Nodes.First(n => n.Name == "entry" && n.Kind == NodeKind.Function).Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(entry, RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "make").Id)); // parse intact -> reached
    }
}
