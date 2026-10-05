using CodeCarver.Cli;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;

// CodeCarver CLI dispatcher. The real work of `carve` lives in CarveCommand.Run (extracted so it can be
// driven — and coverage-measured — in-process); `demo` and `scan-log` are small enough to stay here.

// Output (summary.txt, the console report) must read the same on every machine: CI runners and a German
// workstation format numbers differently from en-US, so pin the culture before anything is printed.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "help":
    case "--help":
    case "-h":
    case "/?":
        Console.WriteLine($"""
            CodeCarver {CarveCommand.Version()} — carve a source tree down to what an image needs.

            usage:
              codecarver carve <source-dir> --config carve.toml [--stage <name>] [--why <symbol>]
              codecarver init [path]          write an annotated carve.toml (default: carve.toml)
              codecarver scan-log <log>       show what CodeCarver reads from a build log / compile_commands.json
              codecarver version              print the version
              codecarver demo                 run the built-in engine demo
              codecarver help                 this text

            exit codes: 0 ok, 1 runtime failure, 2 usage or configuration error,
                        3 the emitted tree failed verify (it would not link)
            Everything else lives in the config file — run 'init' for the annotated template.
            """);
        return 0;
    case "demo":
        RunDemo();
        return 0;
    case "carve":
        // CarveCommand.Run carries its own crash safety net (source-free diagnostic on an unexpected failure).
        return CarveCommand.Run(args, Console.Out, Console.Error);
    case "scan-log":
        return RunScanLog(args);
    case "init":
        // Write the annotated TOML config template.
        return CarveCommand.Init(args, Console.Out, Console.Error);
    case "--version":
    case "version":
        Console.WriteLine($"CodeCarver {CarveCommand.Version()}");
        return 0;
    default:
        Console.Error.WriteLine($"unknown command '{cmd}'. Commands: carve, init, scan-log, version, demo, help.");
        return 2;
}

static int RunScanLog(string[] args)
{
    if (args.Length < 2 || !File.Exists(args[1]))
    {
        Console.Error.WriteLine("usage: scan-log <build-log-file>");
        return 2;
    }

    var cmds = BuildLogScraper.Parse(File.ReadAllText(args[1]));
    var units = cmds.Select(c => c.File).Distinct().Count();
    var defines = cmds.SelectMany(c => c.Defines).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    var includes = cmds.SelectMany(c => c.Includes).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    Console.WriteLine($"scan-log {args[1]}");
    Console.WriteLine($"  compile commands : {cmds.Count}");
    Console.WriteLine($"  translation units: {units}");
    Console.WriteLine($"  distinct defines : {(defines.Count == 0 ? "(none)" : string.Join(", ", defines.Take(25)))}");
    Console.WriteLine($"  distinct includes: {(includes.Count == 0 ? "(none)" : string.Join(", ", includes.Take(25)))}");
    foreach (var c in cmds.Take(5))
        Console.WriteLine($"    {c.File}  -D[{string.Join(" ", c.Defines)}]  -I[{string.Join(" ", c.Includes)}]");
    return 0;
}

static void RunDemo()
{
    // A tiny embedded-flavoured graph that contains every hazard we discussed, so the output shows
    // the engine handling each: a vector-table ISR, a function-pointer dispatch, a used macro, and
    // genuinely dead debug code.
    var b = new GraphBuilder();

    var main = b.Func("main", "main.c", NodeFlags.None, line: 10);
    var init = b.Func("init", "main.c", line: 30);
    var runLoop = b.Func("run_loop", "main.c", line: 50);
    var readSensor = b.Func("read_sensor", "sensor.c", line: 12);
    var sensorType = b.Type("sensor_t", "sensor.h", line: 4);
    var scaleMacro = b.Macro("SCALE_MV", "sensor.h", line: 9);

    // A command handler reached ONLY by taking its address and dispatching through a pointer.
    var dispatch = b.Func("dispatch", "main.c", line: 70);
    var cmdHandler = b.Func("cmd_handler", "cmd.c", NodeFlags.AddressTaken, line: 20);

    // An ISR reached ONLY via the interrupt vector table — never called in source.
    var timerIsr = b.Func("Timer_ISR", "isr.c", NodeFlags.AddressTaken, line: 15);

    // Dead code: present in the repo, referenced by nothing reachable.
    var debugDump = b.Func("unused_debug_dump", "debug.c", line: 8);
    var debugFmt = b.Func("fmt_hex", "debug.c", line: 40);

    b.Calls(main, init);
    b.Calls(main, runLoop);
    b.Calls(main, dispatch);
    b.Calls(runLoop, readSensor);
    b.Refs(readSensor, sensorType);
    b.Expands(readSensor, scaleMacro);
    b.AddressTaken(dispatch, cmdHandler); // conservative edge: keeps the pointer's target
    b.Calls(debugDump, debugFmt);         // dead subgraph

    var roots = new List<Root>
    {
        new(main, RootKind.EntryPoint, "image entry"),
        new(timerIsr, RootKind.VectorTable, "IRQ7 -> Timer_ISR"),
    };

    var safe = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.Safe);
    var minimal = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.MinimalUnsafe);

    Console.WriteLine("CodeCarver demo — carve of a toy embedded image\n");
    Console.WriteLine($"  nodes total   : {safe.Stats.TotalNodes}");
    Console.WriteLine($"  nodes kept    : {safe.Stats.ReachedNodes}  ({safe.Stats.NodeKeepRatio:0%})");
    Console.WriteLine($"  nodes carved  : {safe.Stats.DroppedNodes}");
    Console.WriteLine($"  files kept    : {string.Join(", ", safe.KeptFiles)}");
    Console.WriteLine($"  files dropped : {string.Join(", ", safe.DroppedFiles)}");

    Console.WriteLine("\n  why the tricky ones survived:");
    Console.WriteLine("    " + safe.Explain(timerIsr));
    Console.WriteLine("    " + safe.Explain(cmdHandler));

    Console.WriteLine("\n  dead code correctly carved:");
    Console.WriteLine("    " + safe.Explain(debugDump));

    var tax = safe.Stats.ReachedNodes - minimal.Stats.ReachedNodes;
    Console.WriteLine($"\n  indirection tax (safe - minimal): {tax} node(s) kept only because of");
    Console.WriteLine("    unresolved function pointers / vtables. Sound carve keeps them; the");
    Console.WriteLine("    minimal set would have dropped cmd_handler and broken the image.");
}
