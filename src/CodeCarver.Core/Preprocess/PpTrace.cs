using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The macros a Clang build used, from Clang's own preprocessor: LLVM's <c>pp-trace</c> runs each compile command of
/// the build log again, preprocessing only, and prints every macro it expanded and every name an <c>#ifdef</c>,
/// <c>#ifndef</c>, <c>#elifdef</c>, <c>#elifndef</c> or <c>defined</c> tested. That is the set GCC's <c>-E -dU</c>
/// prints (<see cref="UsedMacros"/>), for builds whose compiler is Clang, and it needs nothing from the build: the
/// commands come from its log, and pp-trace runs where the carve runs (docs/USED-MACROS.md).
///
/// Clang preprocesses for any target from any host, but a pp-trace of another version than the build's compiler can
/// answer <c>__has_builtin</c> and its predefined macros differently: <see cref="Version"/> is printed for that.
/// The names are trusted only when every command ran cleanly (see <see cref="Complete"/>).
/// </summary>
public sealed class PpTrace
{
    /// <summary>The callbacks that name a macro the compile used or tested.</summary>
    public const string Callbacks = "MacroExpands,Defined,Ifdef,Ifndef,Elifdef,Elifndef";

    /// <summary>Every name some command used or tested, by key (<see cref="NameKey"/>); two names on one key keep a
    /// define more, never one less.</summary>
    public HashSet<ulong> Names { get; } = new();

    /// <summary>Commands pp-trace runs: every one but those assembling a file the preprocessor never reads (<c>.s</c>, <c>.asm</c>).</summary>
    public int Commands { get; private set; }
    /// <summary>Commands that assemble <c>.s</c> or <c>.asm</c>: no preprocessor, so nothing to trace.</summary>
    public int NotPreprocessed { get; private set; }
    public int Succeeded { get; private set; }
    public int Failed => Commands - Succeeded;
    /// <summary>Commands not run because an include path or the source they name does not exist here.</summary>
    public int MissingPaths { get; private set; }
    /// <summary>Commands not run because a flag would load or run other code.</summary>
    public int Refused { get; private set; }
    /// <summary>Commands not run because the log doesn't say all of them (an unreadable response file) or where they ran.</summary>
    public int Incomplete { get; private set; }
    /// <summary>Commands pp-trace didn't finish within the time limit.</summary>
    public int TimedOut { get; private set; }
    /// <summary>pp-trace's version line (<c>LLVM version 17.0.6</c>), or null when it printed none.</summary>
    public string? Version { get; private set; }

    /// <summary>The memory the pp-trace runs had (<see cref="ChildMemoryGate"/>), and what they held: the most one
    /// held, the most all held at once, how many ran at once, and how many were stopped to stay in it and ran again.</summary>
    public long BudgetBytes { get; private set; }
    public long PeakChildBytes { get; private set; }
    public long PeakTotalBytes { get; private set; }
    public int PeakRunning { get; private set; }
    public int Evictions { get; private set; }
    /// <summary>Runs that alone held more than the budget (each ran alone).</summary>
    public int OverBudgetAlone { get; private set; }

    /// <summary>Every command ran and printed its names.</summary>
    public bool Complete => Commands > 0 && Succeeded == Commands;

    /// <summary>
    /// Run pp-trace (<paramref name="exe"/>) on every command. Each command's <see cref="CompileCommand.Directory"/>
    /// must be absolute and its paths already mapped onto this machine. The runs share <paramref name="memoryBudget"/>
    /// (default: <see cref="ChildMemoryGate.BudgetForRun"/>, what memoryStopMb leaves the carve) and run at most
    /// <paramref name="parallel"/> at once (default: one per core but one).
    /// </summary>
    public static PpTrace Run(string exe, IReadOnlyList<CompileCommand> commands, int timeoutMs = 1_800_000, int? parallel = null, long? memoryBudget = null)
    {
        var most = parallel ?? Math.Max(1, Environment.ProcessorCount - 1);
        using var gate = new ChildMemoryGate(memoryBudget ?? ChildMemoryGate.BudgetForRun(), most);
        var traced = commands.Where(c => Preprocessed(c.File)).ToList();
        var r = new PpTrace { Commands = traced.Count, NotPreprocessed = commands.Count - traced.Count, Version = ReadVersion(exe) };
        int succeeded = 0, missing = 0, refused = 0, incomplete = 0, timedOut = 0;
        var namesLock = new object();
        Parallel.ForEach(traced, new ParallelOptions { MaxDegreeOfParallelism = most }, cmd =>
        {
            if (cmd.Incomplete || cmd.AlternativeDirectories.Count > 0) { Interlocked.Increment(ref incomplete); return; }
            if (CompilerPreprocess.Refuse(cmd.Arguments)) { Interlocked.Increment(ref refused); return; }
            if (!Directory.Exists(cmd.Directory) || CompilerPreprocess.MissingPath(cmd)) { Interlocked.Increment(ref missing); return; }
            var names = new HashSet<ulong>();
            bool ok, late;
            while (true)
            {
                using var lease = gate.Admit();
                (ok, late) = TraceOne(exe, cmd, names, timeoutMs, lease);
                if (!lease.Evicted) break;
                names.Clear();   // stopped for memory: run again when there is room
            }
            if (late) Interlocked.Increment(ref timedOut);
            if (!ok) return;
            Interlocked.Increment(ref succeeded);
            lock (namesLock) r.Names.UnionWith(names);
        });
        r.Succeeded = succeeded;
        r.MissingPaths = missing;
        r.Refused = refused;
        r.Incomplete = incomplete;
        r.TimedOut = timedOut;
        r.BudgetBytes = gate.Budget;
        r.PeakChildBytes = gate.PeakChildBytes;
        r.PeakTotalBytes = gate.PeakTotalBytes;
        r.PeakRunning = gate.PeakRunning;
        r.Evictions = gate.Evictions;
        r.OverBudgetAlone = gate.OverBudgetAlone;
        return r;
    }

    /// <summary>The preprocessor reads every source but plain assembly: <c>.s</c> (lower case) and <c>.asm</c>.</summary>
    internal static bool Preprocessed(string file) =>
        !file.EndsWith(".s", StringComparison.Ordinal) && !file.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>.S</c> and <c>.sx</c>: assembly through the C preprocessor. pp-trace parses as C after preprocessing,
    /// so their parse errors are expected; only a fatal one (a missing include) means the names are short.</summary>
    internal static bool AssemblyWithCpp(string file) =>
        file.EndsWith(".S", StringComparison.Ordinal) || file.EndsWith(".sx", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>LLVM version x.y.z</c> line of <c>--version</c>, or null.</summary>
    public static string? ReadVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
            });
            if (p is null) return null;
            p.StandardInput.Close();
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { /* already gone */ } return null; }
            return Regex.Match(text, @"LLVM version \S+") is { Success: true } m ? m.Value : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static readonly Regex TargetDriver = new(@"^(?<triple>[\w.+]+(?:-[\w.+]+)+)-(?:clang|clang\+\+|gcc|g\+\+|cc|c\+\+|cpp)(?:-\d+(?:\.\d+)*)?$",
                                                     RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The target a driver's name gives (<c>aarch64-none-elf</c> of <c>aarch64-none-elf-clang</c>), or null.
    /// The compiler takes its target from its own name; pp-trace doesn't, so it is passed as <c>--target</c>.</summary>
    internal static string? TargetOf(string? driver)
    {
        if (driver is null) return null;
        var name = driver.Replace('\\', '/').Split('/')[^1];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return TargetDriver.Match(name) is { Success: true } m ? m.Groups["triple"].Value : null;
    }

    /// <summary>The command line pp-trace is given for <paramref name="cmd"/>: the driver's name (which says C or C++),
    /// its target when only the name says it, the command's flags without outputs or other sources, then the source.</summary>
    internal static List<string> CompileArguments(CompileCommand cmd)
    {
        var driver = cmd.Driver is { } d ? d.Replace('\\', '/').Split('/')[^1] : "clang";
        if (driver.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) driver = driver[..^4];
        var args = new List<string> { driver };
        var flags = CompilerPreprocess.WithoutOutputs(cmd.Arguments);
        if (TargetOf(driver) is { } t && !flags.Any(a => a is "-target" || a.StartsWith("--target=", StringComparison.Ordinal)))
            args.Add("--target=" + t);
        if (AssemblyWithCpp(cmd.File)) args.Add("-ferror-limit=0");   // past 20 errors Clang stops, preprocessing too
        args.AddRange(flags.Where(a => !BuildLogScraper.IsSourceFile(a)));
        args.Add(cmd.File);
        return args;
    }

    /// <summary>Run pp-trace on one command and add the names it prints. Ok: it exited 0 within the time limit.</summary>
    private static (bool Ok, bool TimedOut) TraceOne(string exe, CompileCommand cmd, HashSet<ulong> names, int timeoutMs, ChildMemoryGate.Lease lease)
    {
        // The command goes in a compile database of its own: no command-line quoting or length limit to meet.
        var db = Path.Combine(Path.GetTempPath(), "codecarver-ppt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(db);
            var source = Path.GetFullPath(Path.Combine(cmd.Directory, cmd.File));
            File.WriteAllText(Path.Combine(db, "compile_commands.json"), JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object> { ["directory"] = cmd.Directory, ["file"] = source, ["arguments"] = CompileArguments(cmd) },
            }));
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = cmd.Directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-p", db, "--callbacks=" + Callbacks, source }) psi.ArgumentList.Add(a);

            using var launch = IoMeter.Start(IoMeter.Kind.Launch);   // until pp-trace exits
            using var p = Process.Start(psi);
            if (p is null) return (false, false);
            lease.Track(p);
            p.StandardInput.Close();
            var fatal = false;   // drained so a full stderr pipe can't block it
            p.ErrorDataReceived += (_, e) => { if (e.Data?.Contains("fatal error:", StringComparison.Ordinal) == true) fatal = true; };
            p.BeginErrorReadLine();
            var timedOut = false;
            using var timer = new Timer(_ =>
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }, null, timeoutMs, Timeout.Infinite);
            ReadNames(p.StandardOutput, names);
            p.WaitForExit();   // also waits for stderr's last line
            var clean = p.ExitCode == 0 || (AssemblyWithCpp(cmd.File) && p.ExitCode == 1 && !fatal);
            return (!timedOut && !lease.Evicted && clean, timedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (false, false); }
        finally
        {
            try { Directory.Delete(db, true); } catch { /* best effort */ }
        }
    }

    private const string NameField = "MacroNameTok: ";

    /// <summary>The names of pp-trace's YAML output: one <c>MacroNameTok: NAME</c> line per callback.</summary>
    internal static void ReadNames(TextReader output, HashSet<ulong> names)
    {
        for (var line = output.ReadLine(); line is not null; line = output.ReadLine())
        {
            var s = line.AsSpan().TrimStart();
            if (!s.StartsWith(NameField, StringComparison.Ordinal)) continue;
            s = s[NameField.Length..];
            var n = 0;
            while (n < s.Length && (char.IsAsciiLetterOrDigit(s[n]) || s[n] == '_' || s[n] == '$')) n++;
            if (n > 0) names.Add(NameKey.Short(s[..n]));
        }
    }
}
