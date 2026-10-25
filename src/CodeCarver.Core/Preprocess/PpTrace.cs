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
    /// <summary>Commands not run because a path they name does not exist here: where it ran, its source, or a file
    /// <c>-include</c> or <c>-imacros</c> names. The three counts after it say which, by the first missing.</summary>
    public int MissingPaths { get; private set; }
    public int MissingWorkDir { get; private set; }
    public int MissingSource { get; private set; }
    public int MissingForcedInclude { get; private set; }
    /// <summary>Commands run although an include directory is missing here. Clang skips a missing directory, as in the
    /// build, and a header only it held stops the run with a fatal error, a failed command. Outside the carve root it
    /// is the build machine's toolchain or sysroot. Inside, the tree the build compiled didn't have it either: a
    /// directory the build never had, or one it made (<see cref="MissingDirsInRootFromBuild"/>).</summary>
    public int RanWithoutOutsideDirs { get; private set; }
    public int RanWithoutDirsInRoot { get; private set; }
    /// <summary>Distinct include directories inside the carve root missing here, and of those, the ones the build
    /// wrote to: at or above, or under, the directory of a file some command's <c>-o</c> names.</summary>
    public int MissingDirsInRoot { get; private set; }
    public int MissingDirsInRootFromBuild { get; private set; }
    /// <summary>Commands not run because a flag would load or run other code.</summary>
    public int Refused { get; private set; }
    /// <summary>Commands not run because the log doesn't say all of them (an unreadable response file) or where they ran.</summary>
    public int Incomplete { get; private set; }
    /// <summary>Commands pp-trace didn't finish within the time limit.</summary>
    public int TimedOut { get; private set; }
    /// <summary>Of the commands that ran and failed, within the time limit, why, by what pp-trace printed: a crash, an
    /// include it couldn't find (a fatal error), an option the driver rejected, another error, no error printed, or
    /// pp-trace didn't start. The first that applies counts.</summary>
    public int FailedCrash { get; private set; }
    public int FailedHeaderNotFound { get; private set; }
    public int FailedOption { get; private set; }
    public int FailedError { get; private set; }
    public int FailedNoMessage { get; private set; }
    public int FailedNotStarted { get; private set; }
    /// <summary>Of <see cref="FailedHeaderNotFound"/>, those whose header is one of the C standard library's
    /// (<c>stdio.h</c>, <c>string.h</c>, ...): the build's C library, usually in its toolchain's folders, isn't
    /// where the command says. And the distinct headers not found, by name.</summary>
    public int FailedHeaderNotFoundCLibrary { get; private set; }
    public int HeadersNotFound { get; private set; }
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
    public static PpTrace Run(string exe, IReadOnlyList<CompileCommand> commands, int timeoutMs = 1_800_000, int? parallel = null, long? memoryBudget = null,
                              string? root = null)
    {
        var most = parallel ?? Math.Max(1, Environment.ProcessorCount - 1);
        using var gate = new ChildMemoryGate(memoryBudget ?? ChildMemoryGate.BudgetForRun(), most);
        var traced = commands.Where(c => Preprocessed(c.File)).ToList();
        var r = new PpTrace { Commands = traced.Count, NotPreprocessed = commands.Count - traced.Count, Version = ReadVersion(exe) };
        int succeeded = 0, missing = 0, refused = 0, incomplete = 0, timedOut = 0, outside = 0, inRoot = 0;
        var missingBy = new int[4];
        var missingInRoot = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(PathComparer.Default);
        var failedBy = new int[Enum.GetValues<Failure>().Length];
        int cLibrary = 0;
        var notFound = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var namesLock = new object();
        Parallel.ForEach(traced, new ParallelOptions { MaxDegreeOfParallelism = most }, cmd =>
        {
            if (cmd.Incomplete || cmd.AlternativeDirectories.Count > 0) { Interlocked.Increment(ref incomplete); return; }
            if (CompilerPreprocess.Refuse(cmd.Arguments)) { Interlocked.Increment(ref refused); return; }
            var dirs = new List<(string Dir, bool Inside)>();
            var why = Missing(cmd, root, dirs);
            if (why != MissingKind.None)
            {
                Interlocked.Increment(ref missing);
                Interlocked.Increment(ref missingBy[(int)why]);
                return;
            }
            if (dirs.Any(d => !d.Inside)) Interlocked.Increment(ref outside);
            if (dirs.Any(d => d.Inside)) Interlocked.Increment(ref inRoot);
            foreach (var d in dirs) if (d.Inside) missingInRoot.TryAdd(d.Dir, 0);
            var names = new HashSet<ulong>();
            bool ok, late;
            Failure failure;
            string? header;
            while (true)
            {
                using var lease = gate.Admit();
                (ok, late, failure, header) = TraceOne(exe, cmd, names, timeoutMs, lease);
                if (!lease.Evicted) break;
                names.Clear();   // stopped for memory: run again when there is room
            }
            if (late) Interlocked.Increment(ref timedOut);
            if (!ok && !late)
            {
                Interlocked.Increment(ref failedBy[(int)failure]);
                if (header is not null)
                {
                    notFound.TryAdd(header, 0);
                    if (CLibraryHeaders.Contains(header)) Interlocked.Increment(ref cLibrary);
                }
            }
            if (!ok) return;
            Interlocked.Increment(ref succeeded);
            lock (namesLock) r.Names.UnionWith(names);
        });
        r.Succeeded = succeeded;
        r.MissingPaths = missing;
        r.MissingWorkDir = missingBy[(int)MissingKind.WorkDir];
        r.MissingSource = missingBy[(int)MissingKind.Source];
        r.MissingForcedInclude = missingBy[(int)MissingKind.ForcedInclude];
        r.RanWithoutOutsideDirs = outside;
        r.RanWithoutDirsInRoot = inRoot;
        r.MissingDirsInRoot = missingInRoot.Count;
        var outDirs = OutputDirs(commands);
        r.MissingDirsInRootFromBuild = missingInRoot.Keys.Count(d => outDirs.Any(o => Within(o, d) || Within(d, o)));
        r.Refused = refused;
        r.Incomplete = incomplete;
        r.TimedOut = timedOut;
        r.FailedCrash = failedBy[(int)Failure.Crash];
        r.FailedHeaderNotFound = failedBy[(int)Failure.HeaderNotFound];
        r.FailedOption = failedBy[(int)Failure.Option];
        r.FailedError = failedBy[(int)Failure.Error];
        r.FailedNoMessage = failedBy[(int)Failure.NoMessage];
        r.FailedNotStarted = failedBy[(int)Failure.NotStarted];
        r.FailedHeaderNotFoundCLibrary = cLibrary;
        r.HeadersNotFound = notFound.Count;
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
    internal enum MissingKind { None, WorkDir, Source, ForcedInclude }

    static readonly string[] DirFlags = { "-isystem", "-iquote", "-idirafter", "-I" };
    static readonly string[] FileFlags = { "-include", "-imacros" };

    /// <summary>The first path of <paramref name="cmd"/> missing here that keeps it from running (see
    /// <see cref="MissingPaths"/>). A missing include directory doesn't: it goes in <paramref name="missingDirs"/>,
    /// with whether it is inside <paramref name="root"/> (null root: every directory is inside).</summary>
    internal static MissingKind Missing(CompileCommand cmd, string? root, List<(string Dir, bool Inside)> missingDirs)
    {
        string Full(string p) => Path.GetFullPath(Path.IsPathFullyQualified(p) ? p : Path.Combine(cmd.Directory, p));
        try
        {
            if (!Directory.Exists(cmd.Directory)) return MissingKind.WorkDir;
            if (!File.Exists(Full(cmd.File))) return MissingKind.Source;
            var rootFull = root is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var args = cmd.Arguments;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                var file = FileFlags.FirstOrDefault(f => a.StartsWith(f, StringComparison.Ordinal));
                var flag = file ?? DirFlags.FirstOrDefault(f => a.StartsWith(f, StringComparison.Ordinal));
                if (flag is null) continue;
                var value = a.Length > flag.Length ? a[flag.Length..] : i + 1 < args.Count ? args[++i] : null;
                if (value is null || value.StartsWith('=')) continue;   // sysroot-relative: the compiler's own
                var full = Full(value);
                if (file is not null) { if (!File.Exists(full)) return MissingKind.ForcedInclude; continue; }
                if (Directory.Exists(full)) continue;
                missingDirs.Add((full, rootFull is null || Within(full, rootFull)));
            }
            return MissingKind.None;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return MissingKind.Source; }
    }

    // Is full path a at or under directory b?
    static bool Within(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(a);
        b = Path.TrimEndingDirectorySeparator(b);
        return string.Equals(a, b, PathComparer.Comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, PathComparer.Comparison);
    }

    /// <summary>The directories the commands' <c>-o</c> files go to, full paths.</summary>
    internal static HashSet<string> OutputDirs(IEnumerable<CompileCommand> commands)
    {
        var dirs = new HashSet<string>(PathComparer.Default);
        foreach (var cmd in commands)
        {
            var args = cmd.Arguments;
            for (var i = 0; i < args.Count; i++)
            {
                var o = args[i] == "-o" ? (i + 1 < args.Count ? args[i + 1] : null)
                      : args[i].StartsWith("-o", StringComparison.Ordinal) && args[i].Length > 2 ? args[i][2..] : null;
                if (o is null) continue;
                try
                {
                    var full = Path.GetFullPath(Path.IsPathFullyQualified(o) ? o : Path.Combine(cmd.Directory, o));
                    if (Path.GetDirectoryName(full) is { } d) dirs.Add(d);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return dirs;
    }

    public enum Failure { NotStarted, Crash, HeaderNotFound, Option, Error, NoMessage }

    /// <summary>The C standard library's headers, C89 to C23.</summary>
    internal static readonly HashSet<string> CLibraryHeaders = new(StringComparer.Ordinal)
    {
        "assert.h", "complex.h", "ctype.h", "errno.h", "fenv.h", "float.h", "inttypes.h", "iso646.h", "limits.h",
        "locale.h", "math.h", "setjmp.h", "signal.h", "stdalign.h", "stdarg.h", "stdatomic.h", "stdbit.h", "stdbool.h",
        "stdckdint.h", "stddef.h", "stdint.h", "stdio.h", "stdlib.h", "stdnoreturn.h", "string.h", "tgmath.h",
        "threads.h", "time.h", "uchar.h", "wchar.h", "wctype.h",
    };

    static readonly Regex NotFoundLine = new(@"fatal error: '([^']+)' file not found", RegexOptions.CultureInvariant);
    static readonly string[] OptionErrors = { "unknown argument", "unsupported option", "unsupported argument", "unknown target", "invalid value", "invalid argument", "unsupported target" };

    /// <summary>Why a run failed, from the lines it printed to stderr (see <see cref="FailedCrash"/>); the header it
    /// couldn't find, for a fatal one.</summary>
    internal static (Failure Why, string? Header) Classify(IEnumerable<string> stderr)
    {
        bool crash = false, option = false, error = false;
        string? header = null;
        foreach (var line in stderr)
        {
            if (line.Contains("PLEASE submit a bug report", StringComparison.Ordinal) || line.StartsWith("Stack dump:", StringComparison.Ordinal)) crash = true;
            if (header is null && NotFoundLine.Match(line) is { Success: true } m) header = m.Groups[1].Value;
            if (!line.Contains("error:", StringComparison.Ordinal)) continue;
            error = true;
            if (OptionErrors.Any(o => line.Contains(o, StringComparison.Ordinal))) option = true;
        }
        var why = crash ? Failure.Crash : header is not null ? Failure.HeaderNotFound : option ? Failure.Option : error ? Failure.Error : Failure.NoMessage;
        return (why, header);
    }

    private static (bool Ok, bool TimedOut, Failure Why, string? Header) TraceOne(string exe, CompileCommand cmd, HashSet<ulong> names, int timeoutMs, ChildMemoryGate.Lease lease)
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
            if (p is null) return (false, false, Failure.NotStarted, null);
            lease.Track(p);
            p.StandardInput.Close();
            var fatal = false;   // drained so a full stderr pipe can't block it
            var said = new List<string>();   // what's needed to say why it failed: error lines and a crash's
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (line.Contains("fatal error:", StringComparison.Ordinal)) fatal = true;
                if (said.Count < 64 && (line.Contains("error:", StringComparison.Ordinal) || line.Contains("PLEASE submit", StringComparison.Ordinal) || line.StartsWith("Stack dump:", StringComparison.Ordinal)))
                    said.Add(line);
            };
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
            var ok = !timedOut && !lease.Evicted && clean;
            var (why, header) = ok ? (Failure.NoMessage, null) : Classify(said);
            return (ok, timedOut, why, header);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (false, false, Failure.NotStarted, null); }
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
