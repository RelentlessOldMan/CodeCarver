using CodeCarver.Core.Frontend;
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
    public void OversizedFile_PassedEmpty_IsStillKeptWholeViaIncludeClosure()
    {
        // Scalability: a multi-GB auto-generated register header can't be read into a string (>2GB
        // throws) or parsed (tree-sitter memory explodes). The CLI passes such files with EMPTY text —
        // they become File nodes only, never parsed — and must still be kept whole when a kept unit
        // #includes them, and dropped when nothing does. (Here "" stands in for "too big to parse".)
        const string appC = """
            #include "chip_regs.h"
            int use(void) { return REG_BANK7; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[]
        {
            ("app.c", appC),
            ("chip_regs.h", ""),   // "too big to parse" — registered as a File node only
            ("other_regs.h", ""),  // also huge, but included by nothing kept
        });

        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "use"), RootKind.ExplicitSymbol) });

        Assert.Contains("chip_regs.h", plan.KeptFiles);      // kept whole via #include-closure
        Assert.Contains("other_regs.h", plan.DroppedFiles);  // nobody includes it -> safe to drop
        // The oversized header contributed no symbol nodes (never parsed) — only a File node.
        Assert.DoesNotContain(graph.Nodes, n => n.Kind != NodeKind.File && n.FilePath == "chip_regs.h");
    }

    [Fact]
    public void InlineAsmSymbolReference_KeepsTarget()
    {
        // A symbol named only inside inline asm (`bl helper`, `.word my_isr`) has no C-level edge — a
        // from-main closure drops it and the link/behaviour breaks. It must be kept; unrelated dead code not.
        const string src = """
            void helper(void);
            void my_isr(void);
            void helper(void) { }
            void my_isr(void) { }
            int trampoline(void) { __asm__ volatile ("bl helper\n\t.word my_isr"); return 0; }
            int dead(void) { return 0; }
            int main(void) { return trampoline(); }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });
        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "main"), RootKind.EntryPoint) });

        Assert.True(plan.IsKept(Find(graph, "helper")));  // referenced from inline asm
        Assert.True(plan.IsKept(Find(graph, "my_isr")));  // referenced from inline asm
        Assert.False(plan.IsKept(Find(graph, "dead")));   // genuinely unused
    }

    [Fact]
    public void KeepAttributes_AreImplicitRoots_KeptEvenIfUnreferenced()
    {
        // constructor/destructor/used run or are retained by the runtime/linker, not by any call — a
        // from-main closure would drop a self-registering driver or an initcall and ship a broken image.
        const string src = """
            __attribute__((constructor)) static void ctor(void) { }
            void dtor(void) __attribute__((destructor));
            void dtor(void) { }
            __attribute__((used)) static int retained(int x) { return x; }
            static int dead(int x) { return x; }
            int used_fn(void) { return 1; }
            int main(void) { return used_fn(); }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });
        var roots = new CompositeRootProvider(
            new ExplicitRootProvider(symbols: new[] { "main" }),
            new AttributeRootProvider()).Discover(graph).ToList();
        var plan = ReachabilityEngine.Compute(graph, roots);

        Assert.True(plan.IsKept(Find(graph, "ctor")));      // __attribute__((constructor))
        Assert.True(plan.IsKept(Find(graph, "dtor")));      // __attribute__((destructor)), trailing form
        Assert.True(plan.IsKept(Find(graph, "retained")));  // __attribute__((used))
        Assert.True(plan.IsKept(Find(graph, "used_fn")));   // main -> used_fn
        Assert.False(plan.IsKept(Find(graph, "dead")));     // genuinely dead -> carved
    }

    [Fact]
    public void VectorTableAndWeakAlias_KeepIsrsAndAliasTarget_DropUnused()
    {
        // The embedded shape: ISRs reached only through the vector table, and unused handlers declared as
        // weak aliases to a Default_Handler. Rooting the reset handler must keep the table's ISRs AND the
        // alias TARGET (dropping it breaks the link — a real bug the ARM tier caught), and drop dead code.
        const string startup = """
            extern int main(void);
            void Reset_Handler(void);
            void SysTick_Handler(void);
            void Default_Handler(void);
            void NMI_Handler(void) __attribute__((weak, alias("Default_Handler")));
            void (* const g_vectors[])(void) = { Reset_Handler, NMI_Handler, SysTick_Handler };
            void Reset_Handler(void) { main(); }
            void Default_Handler(void) { for(;;){} }
            """;
        const string app = """
            void SysTick_Handler(void) { }
            int used(void) { return 1; }
            int dead(void) { return 0; }
            int main(void) { return used(); }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("startup.c", startup), ("app.c", app) });
        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "Reset_Handler"), RootKind.EntryPoint) });

        Assert.True(plan.IsKept(Find(graph, "SysTick_Handler")));  // reached via the vector table
        Assert.True(plan.IsKept(Find(graph, "NMI_Handler")));      // alias symbol, in the table
        Assert.True(plan.IsKept(Find(graph, "Default_Handler")));  // alias TARGET — must survive
        Assert.True(plan.IsKept(Find(graph, "used")));             // main -> used
        Assert.False(plan.IsKept(Find(graph, "dead")));            // nothing reaches it
    }

    [Fact]
    public void IncludeFragment_IsKeptWhole_AndReferencingIncludeDetected()
    {
        // Finding A: a header that's a bare byte list #included INSIDE an array initializer is valid in
        // context but invalid stand-alone (stalls tree-sitter). It must be (a) detected + kept whole with a
        // warning, and (b) still kept via the include — which tree-sitter misses in that position, so the
        // include is text-scanned. Dropping it would break the emitted build.
        var frag = string.Join("\n", Enumerable.Repeat("0x00, 0x11, 0x22, 0x33, 0x44, 0x55,", 60));
        const string appC = """
            static const unsigned char img[] = {
            #include "blob.h"
            };
            int use(void){ return img[0]; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("app.c", appC), ("blob.h", frag) });

        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "use"), RootKind.ExplicitSymbol) });

        Assert.Contains("blob.h", plan.KeptFiles); // kept via the in-initializer #include (text-scanned)
        Assert.Contains(fe.Warnings, w => w.Contains("blob.h") && w.Contains("fragment"));
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

    [Fact]
    public void CallbackInMacroDefinedFunction_IsKept_ViaFile()
    {
        // A function whose signature is hidden behind a macro (janet's `JANET_CORE_FN(name, ...)`) is not
        // captured, so a callback taken inside its body has no enclosing function to attribute to. It must
        // fall back to the file, or the callback target is dropped and the carved file dangles. real_root
        // keeps the file; my_callback is referenced ONLY inside the macro-defined body.
        const string src = """
            static int my_callback(int x){ return x + 1; }
            int register_cb(int (*f)(int));

            #define CORE_FN(name) int name(void)

            CORE_FN(do_register) {
                return register_cb(my_callback);
            }

            int real_root(void){ return 7; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });
        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "real_root"), RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(Find(graph, "my_callback")));
    }

    [Fact]
    public void FunctionInErrorWrappedTable_IsKept_ViaFile()
    {
        // A file-scope function-pointer table buried in macro-invocation soup (janet's `OPMETHOD(...)` run
        // before a `JanetMethod x[] = {..., cfun, ...}` table) is wrapped by tree-sitter in an ERROR node,
        // so the initializer_list scan misses it. A real function referenced only there would be dropped.
        // Identifiers inside an ERROR subtree fall back to the file. root keeps the file; real_div is
        // referenced ONLY in the mis-parsed table.
        const string src = """
            typedef struct { const char *name; void *fn; } M;

            static int real_div(int x){ return x / 2; }

            OPMETHOD(int, s, sub, -)
            OPMETHOD(int, s, mul, *)
            DIVMETHOD(int, s, rem, %)

            static M methods[] = {
                {"div", real_div},
            };

            int root(void){ return methods[0].name[0]; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });
        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "root"), RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(Find(graph, "real_div")));
    }

    [Fact]
    public void SymbolsInMacroCallInitializer_AreKept_ViaFile()
    {
        // A file-scope global initialized by a MACRO invocation (quickjs's
        // `static const X y = JS_OBJECT_DEF("qjs", qjs_methods, ...)`) references other symbols as macro
        // arguments. The initializer is a call_expression, not an initializer_list, so the plain init scan
        // missed the arguments and a table/function referenced only there was dropped → dangling. Pass 5
        // now scans call_expression initializers too. probe_fn and handlers are referenced ONLY inside the
        // macro-call initializers.
        const string src = """
            typedef struct { void *tab; int len; } Obj;

            static int probe_fn(int x){ return x; }
            static int handlers[] = { 0 };

            #define REGISTER(t, n) { (t), (n) }

            static const Obj registered = REGISTER(handlers, 1);
            static const Obj with_fn = REGISTER(probe_fn, 0);

            const void *root(void){ return registered.tab; }
            """;
        using var fe = new CFrontEnd();
        var graph = fe.BuildGraph(new[] { ("m.c", src) });
        var plan = ReachabilityEngine.Compute(graph,
            new[] { new Root(Find(graph, "root"), RootKind.ExplicitSymbol) });

        Assert.True(plan.IsKept(Find(graph, "probe_fn")));                                  // fn as macro arg
        Assert.True(plan.IsKept(FindNode(graph, NodeKind.Global, "handlers")));             // table as macro arg
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
