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
}
