using CodeCarver.Core.Frontend;
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
    public void ConstructorInitListCallee_IsKept()
    {
        // A constructor runs on every instantiation (untraceable) and can't be pruned; because it IS
        // emitted, whatever it calls in its member-initializer list must be kept too — or the carved
        // constructor references an undefined function (real pugixml bug: xml_buffered_writer's ctor
        // called get_write_encoding in its init list). ConstructorRootProvider roots the constructor so
        // its callees follow. `use` reaches the type Writer; nothing calls the ctor or the helper.
        const string src = """
            int compute_mode(int x);
            struct Writer {
                int mode;
                Writer(int x) : mode(compute_mode(x)) {}
                int emit() const { return mode; }
            };
            int compute_mode(int x) { return x + 1; }
            int use() { Writer w(3); return w.emit(); }
            int dead_helper() { return 9; }
            """;
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("w.cpp", src) });
        var roots = new ExplicitRootProvider(symbols: new[] { "use" }).Discover(graph)
            .Concat(new ConstructorRootProvider().Discover(graph)).ToList();
        var plan = ReachabilityEngine.Compute(graph, roots);

        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "compute_mode").Id)); // ctor init-list callee kept
        Assert.False(plan.IsKept(graph.Nodes.First(n => n.Name == "dead_helper").Id)); // genuinely dead -> carved
    }

    [Fact]
    public void TemplateArgumentCall_KeepsCallee()
    {
        // A function/method invoked with EXPLICIT template arguments — `compute<3>(x)`, `s.prev<2>(x)` —
        // must still create a call edge to the callee. tree-sitter parses `compute<3>` as a template_function
        // and `s.prev<2>` as a template_method (NOT a plain identifier / field_identifier), so without the
        // template-call query patterns the edge is missed and the callee is pruned -> the carve fails to
        // compile. This is the real simdjson `simd8::prev<N>` (and get<N>/shr<N>) drop the C++ link oracle
        // caught: those methods were called only with explicit template args and got dropped.
        const string src = """
            int base_helper(int x) { return x + 1; }
            template<int N> int compute(int x) { return base_helper(x) + N; }
            struct S {
                int member_helper(int x) const { return x * 2; }
                template<int N> int prev(int x) const { return member_helper(x) + N; }
            };
            int root(S& s) { return compute<3>(5) + s.prev<2>(7); }
            int dead(int x) { return x; }
            """;
        using var fe = new CppFrontEnd();
        var graph = fe.BuildGraph(new[] { ("t.cpp", src) });
        var roots = new ExplicitRootProvider(symbols: new[] { "root" }).Discover(graph).ToList();
        var plan = ReachabilityEngine.Compute(graph, roots);

        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "compute").Id));       // free template fn: compute<3>(5)
        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "base_helper").Id));   // its callee follows
        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "prev").Id));          // template method: s.prev<2>(7)
        Assert.True(plan.IsKept(graph.Nodes.First(n => n.Name == "member_helper").Id)); // its callee follows
        Assert.False(plan.IsKept(graph.Nodes.First(n => n.Name == "dead").Id));         // unreferenced -> carved
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
