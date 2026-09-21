using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// Proves the tree-sitter C front-end end-to-end: real C source in, a carve out. These are the first
/// tests that exercise the chosen architecture (tree-sitter extraction → reachability) on actual code
/// rather than hand-built graphs.
/// </summary>
public class CFrontEndTests
{
    private const string MainC = """
        int helper(void);

        int used(void)   { return helper(); }
        int unused(void) { return 42; }

        int main(void)   { return used(); }
        """;

    private const string UtilC = """
        int helper(void) { return 1; }
        int orphan(void) { return 7; }
        """;

    [Fact]
    public void ExtractsFunctions_AndDirectCalls_FromRealC()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("main.c", MainC), ("util.c", UtilC) });

        var names = graph.Nodes.Where(n => n.Kind == NodeKind.Function).Select(n => n.Name).ToHashSet();
        Assert.Contains("main", names);
        Assert.Contains("used", names);
        Assert.Contains("helper", names);
        Assert.Contains("orphan", names);
    }

    [Fact]
    public void CarveFromMain_KeepsCallChain_DropsUnreached()
    {
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("main.c", MainC), ("util.c", UtilC) });

        var main = Find(graph, "main");
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(main, RootKind.EntryPoint) });

        Assert.True(plan.IsKept(Find(graph, "main")));
        Assert.True(plan.IsKept(Find(graph, "used")));
        Assert.True(plan.IsKept(Find(graph, "helper")));   // reached across files: used() -> helper()
        Assert.False(plan.IsKept(Find(graph, "unused")));  // never called
        Assert.False(plan.IsKept(Find(graph, "orphan")));  // never called

        // File-level: util.c stays (helper lives there); nothing here should drop a needed file.
        Assert.Contains("util.c", plan.KeptFiles);
    }

    [Fact]
    public void AddressTakenHandler_IsKept_ThoughNeverCalledDirectly()
    {
        // handler_a is never called — only registered as a callback. A sound carve must keep it.
        const string appC = """
            typedef void (*handler_t)(void);
            void handler_a(void);

            static handler_t g_cb;
            void register_cb(handler_t h) { g_cb = h; }

            void run(void)     { register_cb(handler_a); }
            void unused(void)  { }
            """;
        const string handlersC = """
            void handler_a(void) { }
            void secret(void)    { }
            """;

        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("app.c", appC), ("handlers.c", handlersC) });

        var run = Find(graph, "run");
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(run, RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(Find(graph, "run")));
        Assert.True(plan.IsKept(Find(graph, "register_cb")));      // called by run
        Assert.True(plan.IsKept(Find(graph, "handler_a")));        // reached ONLY via address-taken edge
        Assert.False(plan.IsKept(Find(graph, "unused")));
        Assert.False(plan.IsKept(Find(graph, "secret")));

        // The handler was flagged address-taken during extraction.
        Assert.True(graph.GetNode(Find(graph, "handler_a")).Flags.HasFlag(NodeFlags.AddressTaken));
    }

    [Fact]
    public void MinimalUnsafe_DropsAddressTakenHandler_ShowingTheTax()
    {
        const string src = """
            typedef void (*fp)(void);
            void handler(void);
            void reg(fp f);
            void run(void) { reg(handler); }
            void handler(void) { }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });
        var roots = new[] { new Root(Find(graph, "run"), RootKind.ExplicitSymbol) };

        var safe = ReachabilityEngine.Compute(graph, roots, ReachabilityOptions.Safe);
        var minimal = ReachabilityEngine.Compute(graph, roots, ReachabilityOptions.MinimalUnsafe);

        Assert.True(safe.IsKept(Find(graph, "handler")));      // sound carve keeps it
        Assert.False(minimal.IsKept(Find(graph, "handler")));  // strictly-direct would drop -> broken
    }

    [Fact]
    public void ExtractsTypesAndMacros()
    {
        const string src = """
            #define SCALE_MV 3300
            typedef struct sensor sensor_t;
            enum mode { OFF, ON };

            int read(void) { return SCALE_MV; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("sensor.c", src) });

        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Macro && n.Name == "SCALE_MV");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Type && n.Name == "sensor_t");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Type && n.Name == "mode");
    }

    [Fact]
    public void IncludedHeader_IsKept_AndUnusedHeaderDropped()
    {
        const string mainC = """
            #include "api.h"
            int main(void) { return api_do(); }
            """;
        const string apiH = """
            #ifndef API_H
            #define API_H
            #define OK 0
            int api_do(void);
            #endif
            """;
        const string apiC = """
            #include "api.h"
            int api_do(void) { return OK; }
            """;
        const string unusedH = "#define NOPE 1\n";

        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[]
        {
            ("main.c", mainC), ("api.h", apiH), ("api.c", apiC), ("unused.h", unusedH),
        });

        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "main"), RootKind.EntryPoint) });

        // The header main.c/api.c need must be kept; the unrelated header must be dropped.
        Assert.Contains("main.c", plan.KeptFiles);
        Assert.Contains("api.c", plan.KeptFiles);
        Assert.Contains("api.h", plan.KeptFiles);      // kept via #include closure
        Assert.Contains("unused.h", plan.DroppedFiles);

        // The macro the kept function expands is kept; the unused macro is not.
        Assert.True(plan.IsKept(FindNode(graph, NodeKind.Macro, "OK")));
        Assert.False(plan.IsKept(FindNode(graph, NodeKind.Macro, "NOPE")));
    }

    [Fact]
    public void PointerReturningFunctions_AreCaptured_AndReachable()
    {
        // Regression: `T *f()` wraps the function_declarator in a pointer_declarator. An earlier query
        // matched only a direct function_declarator, so pointer-returning functions were never
        // captured — which left them un-prunable AND under-reached their callees (broke real builds).
        const string src = """
            int *make(void)  { static int x; return &x; }
            int  use(void)   { return *make(); }
            int  unused(void){ return 0; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("p.c", src) });

        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Function && n.Name == "make");

        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "use"), RootKind.ExplicitSymbol) });
        Assert.True(plan.IsKept(Find(graph, "make")));   // reached via use() -> make()
        Assert.False(plan.IsKept(Find(graph, "unused")));
    }

    [Fact]
    public void FileScopeDispatchTable_KeepsHandlers_ReferencedOnlyThere()
    {
        // The embedded case: handlers named only in a file-scope table (like an interrupt vector table)
        // must survive when the file is kept, even though nothing calls them directly.
        const string src = """
            typedef void (*fn_t)(void);

            static void handler_a(void) { }
            static void handler_b(void) { }

            static fn_t table[] = { handler_a, handler_b };

            void run(void) { table[0](); }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("dispatch.c", src) });

        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "run"), RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(Find(graph, "run")));
        Assert.True(plan.IsKept(Find(graph, "handler_a")));  // reached only via the file-scope table
        Assert.True(plan.IsKept(Find(graph, "handler_b")));
    }

    [Fact]
    public void ParenthesizedFunctionName_IsCaptured()
    {
        // Regression (lua's `lua_State *(luaL_newstate)(void)`): the name wrapped in parens must still
        // be captured, or the function is invisible and its references (e.g. &panic) are missed.
        const string src = """
            int (thing)(void) { return 0; }
            int use(void) { return thing(); }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("p.c", src) });

        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Function && n.Name == "thing");
        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "use"), RootKind.ExplicitSymbol) });
        Assert.True(plan.IsKept(Find(graph, "thing")));
    }

    [Fact]
    public void MacroHiddenCall_KeepsHelper_ReachedOnlyThroughAMacroBody()
    {
        // Regression (lua's `#define linkgclist(o,p) linkgclist_(...)`): a function called only from
        // inside a macro body must be kept when a reached function expands that macro.
        const string src = """
            #define WRAP(x) real_helper(x)
            int real_helper(int x) { return x; }
            int run(void) { return WRAP(5); }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });

        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "run"), RootKind.ExplicitSymbol) });
        Assert.True(plan.IsKept(Find(graph, "real_helper"))); // reached: run -> WRAP macro -> real_helper
    }

    [Fact]
    public void IfdefInterleavedBody_NoPhantomKeywordFunctions()
    {
        // Regression (inih): #if directives interleaved with if/else can make tree-sitter mis-parse
        // `if (...)` as a function named "if", which steals call attribution and gets wrongly pruned.
        const string src = """
            static char* find(char* s) { return s; }
            int parse(char* line) {
                char* start = line;
            #if ALLOW_MULTILINE
                if (*start) {
            #if ALLOW_INLINE
                    start = find(start);
            #endif
                }
            #endif
                return 0;
            }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("p.c", src) });

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKind.Function && n.Name == "if");

        var parse = graph.Nodes.First(n => n.Name == "parse").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(parse, RootKind.ExplicitSymbol) });
        Assert.True(plan.IsKept(Find(graph, "find"))); // reached from parse despite the #if maze
    }

    [Fact]
    public void Prototypes_AreNotCapturedAsDefinitions()
    {
        // A bare prototype must not become a function node (no body to carve).
        const string src = """
            int real(void);
            int real(void) { return 1; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("q.c", src) });
        Assert.Equal(1, graph.Nodes.Count(n => n.Kind == NodeKind.Function && n.Name == "real"));
    }

    private static NodeId Find(CodeGraph graph, string name) => FindNode(graph, NodeKind.Function, name);

    private static NodeId FindNode(CodeGraph graph, NodeKind kind, string name)
    {
        foreach (var n in graph.Nodes)
            if (n.Kind == kind && n.Name == name)
                return n.Id;
        throw new InvalidOperationException($"{kind} '{name}' not found");
    }
}
