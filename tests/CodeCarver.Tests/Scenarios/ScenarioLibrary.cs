using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Tests.Scenarios;

/// <summary>
/// A "we only need XYZ" carve condition with its expected outcome. These are hand-built graphs that
/// model real reachability shapes (embedded firmware, a static library's public API, a CLI
/// subcommand, callback/plugin dispatch, C++ virtual dispatch, macro dependencies). They give
/// regression coverage today and are the templates a real-repo front-end must reproduce — when we
/// wire the linker-map oracle, each real scenario asserts the same kinds of facts against real code.
/// </summary>
public sealed class CarveScenario
{
    public required string Name { get; init; }
    public required string Intent { get; init; }
    public required CodeGraph Graph { get; init; }
    public required IReadOnlyList<Root> Roots { get; init; }

    /// <summary>Symbol names that MUST survive the carve (dropping one is a soundness failure).</summary>
    public string[] MustKeep { get; init; } = Array.Empty<string>();

    /// <summary>Symbol names that must be carved away.</summary>
    public string[] MustDrop { get; init; } = Array.Empty<string>();

    /// <summary>Files that must be dropped entirely (file-level carve).</summary>
    public string[] MustDropFiles { get; init; } = Array.Empty<string>();

    public override string ToString() => Name;
}

public static class ScenarioLibrary
{
    public static IReadOnlyList<CarveScenario> All { get; } = new[]
    {
        EmbeddedImage(),
        LibraryPublicApi(),
        CliSubcommand(),
        CallbackDispatch(),
        VirtualDispatch(),
        MacroDependency(),
    };

    public static CarveScenario Get(string name) => All.First(s => s.Name == name);

    // ── 1. Embedded image: only main() + the timer ISR are needed ────────────────────────────────
    private static CarveScenario EmbeddedImage()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var init = b.Func("init", "main.c");
        var runLoop = b.Func("run_loop", "main.c");
        var pollUart = b.Func("poll_uart", "main.c");
        var readSensor = b.Func("read_sensor", "sensor.c");
        var sensorT = b.Type("sensor_t", "sensor.h");
        var scale = b.Macro("SCALE_MV", "sensor.h");
        var isr = b.Func("Timer_ISR", "isr.c", NodeFlags.AddressTaken);
        var dbgDump = b.Func("debug_dump", "debug.c");
        var fmtHex = b.Func("fmt_hex", "debug.c");
        var spiInit = b.Func("spi_init", "spi.c"); // an unused peripheral driver

        b.Calls(main, init);
        b.Calls(main, runLoop);
        b.Calls(main, pollUart);
        b.Calls(runLoop, readSensor);
        b.Refs(readSensor, sensorT);
        b.Expands(readSensor, scale);
        b.Calls(dbgDump, fmtHex); // dead subgraph

        return new CarveScenario
        {
            Name = "embedded-image",
            Intent = "We only need main() and the timer interrupt; drop debug + unused drivers.",
            Graph = b.Graph,
            Roots = new[]
            {
                new Root(main, RootKind.EntryPoint, "image entry"),
                new Root(isr, RootKind.VectorTable, "IRQ7"),
            },
            MustKeep = new[] { "main", "run_loop", "read_sensor", "sensor_t", "SCALE_MV", "Timer_ISR" },
            MustDrop = new[] { "debug_dump", "fmt_hex", "spi_init" },
            MustDropFiles = new[] { "debug.c", "spi.c" },
        };
    }

    // ── 2. Static library: only two of the public API entry points are needed ────────────────────
    private static CarveScenario LibraryPublicApi()
    {
        var b = new GraphBuilder();
        var open = b.Func("lib_open", "api.c");
        var read = b.Func("lib_read", "api.c");
        var admin = b.Func("lib_admin", "api.c"); // public, but not needed here
        var allocCtx = b.Func("alloc_ctx", "core.c");
        var zeroMem = b.Func("zero_mem", "core.c");
        var adminReset = b.Func("admin_reset", "admin.c");
        var wipeAll = b.Func("wipe_all", "admin.c");

        b.Calls(open, allocCtx);
        b.Calls(read, allocCtx);
        b.Calls(allocCtx, zeroMem);
        b.Calls(admin, adminReset);
        b.Calls(adminReset, wipeAll);

        return new CarveScenario
        {
            Name = "library-public-api",
            Intent = "We only call lib_open/lib_read; the admin API and its subtree are unnecessary.",
            Graph = b.Graph,
            Roots = new[]
            {
                new Root(open, RootKind.ExplicitSymbol, "lib_open"),
                new Root(read, RootKind.ExplicitSymbol, "lib_read"),
            },
            MustKeep = new[] { "lib_open", "lib_read", "alloc_ctx", "zero_mem" },
            MustDrop = new[] { "lib_admin", "admin_reset", "wipe_all" },
            MustDropFiles = new[] { "admin.c" },
        };
    }

    // ── 3. CLI tool: only one subcommand is needed (even main() gets carved) ──────────────────────
    private static CarveScenario CliSubcommand()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c");
        var cmdBuild = b.Func("cmd_build", "build.c");
        var compileAll = b.Func("compile_all", "build.c");
        var readFile = b.Func("read_file", "io.c"); // shared util
        var cmdDeploy = b.Func("cmd_deploy", "deploy.c");
        var upload = b.Func("upload", "deploy.c");

        b.Calls(main, cmdBuild);
        b.Calls(main, cmdDeploy);
        b.Calls(cmdBuild, compileAll);
        b.Calls(compileAll, readFile);
        b.Calls(cmdDeploy, upload);
        b.Calls(upload, readFile);

        return new CarveScenario
        {
            Name = "cli-subcommand",
            Intent = "We only need the 'build' subcommand entry; deploy and even main() are unnecessary.",
            Graph = b.Graph,
            Roots = new[] { new Root(cmdBuild, RootKind.ExplicitSymbol, "cmd_build") },
            MustKeep = new[] { "cmd_build", "compile_all", "read_file" },
            MustDrop = new[] { "main", "cmd_deploy", "upload" },
            MustDropFiles = new[] { "deploy.c", "main.c" },
        };
    }

    // ── 4. Callback dispatch: a handler reached only through a function pointer must survive ──────
    private static CarveScenario CallbackDispatch()
    {
        var b = new GraphBuilder();
        var run = b.Func("run", "app.c");
        var dispatch = b.Func("dispatch", "app.c");
        var handlerA = b.Func("handler_a", "ha.c", NodeFlags.AddressTaken);
        var logLine = b.Func("log_line", "log.c");
        var handlerB = b.Func("handler_b", "hb.c"); // never registered / never called
        var secret = b.Func("secret", "hb.c");

        b.Calls(run, dispatch);
        b.AddressTaken(dispatch, handlerA); // conservative edge: keep the pointer target
        b.Calls(handlerA, logLine);
        b.Calls(handlerB, secret); // dead

        return new CarveScenario
        {
            Name = "callback-dispatch",
            Intent = "We need run(); a handler is reachable only via a function pointer and must be kept.",
            Graph = b.Graph,
            Roots = new[] { new Root(run, RootKind.ExplicitSymbol, "run") },
            MustKeep = new[] { "run", "dispatch", "handler_a", "log_line" },
            MustDrop = new[] { "handler_b", "secret" },
            MustDropFiles = new[] { "hb.c" },
        };
    }

    // ── 5. C++ virtual dispatch: calling a virtual keeps all its overrides (conservative) ────────
    private static CarveScenario VirtualDispatch()
    {
        var b = new GraphBuilder();
        var process = b.Func("process", "main.cpp");
        var shapeArea = b.Func("Shape__area", "shape.cpp", NodeFlags.Virtual);
        var circleArea = b.Func("Circle__area", "circle.cpp", NodeFlags.Virtual);
        var squareArea = b.Func("Square__area", "square.cpp", NodeFlags.Virtual);
        var widgetDraw = b.Func("Widget__draw", "widget.cpp"); // unrelated class

        b.Calls(process, shapeArea);
        b.Vtable(shapeArea, circleArea); // any override could be dispatched
        b.Vtable(shapeArea, squareArea);

        return new CarveScenario
        {
            Name = "virtual-dispatch",
            Intent = "We call Shape::area through a base pointer; every override must be kept.",
            Graph = b.Graph,
            Roots = new[] { new Root(process, RootKind.ExplicitSymbol, "process") },
            MustKeep = new[] { "process", "Shape__area", "Circle__area", "Square__area" },
            MustDrop = new[] { "Widget__draw" },
            MustDropFiles = new[] { "widget.cpp" },
        };
    }

    // ── 6. Macro dependency: a kept function expands a macro; its header survives ─────────────────
    private static CarveScenario MacroDependency()
    {
        var b = new GraphBuilder();
        var compute = b.Func("compute", "calc.c");
        var clamp = b.Macro("CLAMP", "macros.h");
        var debugTrace = b.Macro("DEBUG_TRACE", "macros.h"); // unused macro in a kept header
        var oldCalc = b.Func("old_calc", "legacy.c");

        b.Expands(compute, clamp);

        return new CarveScenario
        {
            Name = "macro-dependency",
            Intent = "compute() expands CLAMP; keep the macro + its header, drop unused macros/files.",
            Graph = b.Graph,
            Roots = new[] { new Root(compute, RootKind.ExplicitSymbol, "compute") },
            MustKeep = new[] { "compute", "CLAMP" },
            MustDrop = new[] { "old_calc", "DEBUG_TRACE" },
            MustDropFiles = new[] { "legacy.c" },
        };
    }
}
