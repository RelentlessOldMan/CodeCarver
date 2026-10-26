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
    /// include it couldn't find (a fatal error), another error, an option the driver rejected and no other error, no
    /// error printed, or pp-trace didn't start. The first that applies counts. A rejected option alone doesn't fail a
    /// run (see <see cref="OptionErrors"/>), so it counts only when nothing else did.</summary>
    public int FailedCrash { get; private set; }
    public int FailedHeaderNotFound { get; private set; }
    public int FailedOption { get; private set; }
    /// <summary>Failed by an error other than a missing include or a rejected option, and of those: past Clang's
    /// error limit (it stopped there, the preprocessor too), an <c>#error</c> that fired (this Clang's macros differ
    /// from the build compiler's), another the preprocessor reported (Clang's "Lexical or Preprocessor Issue", such
    /// as an <c>#if</c> it couldn't evaluate), or another still. An error only the parse after preprocessing reports
    /// doesn't fail a run (see <see cref="SucceededPastParseErrors"/>).</summary>
    public int FailedError => FailedErrorLimit + FailedHashError + FailedPreprocessorError + FailedOtherError;
    public int FailedErrorLimit { get; private set; }
    public int FailedHashError { get; private set; }
    public int FailedPreprocessorError { get; private set; }
    public int FailedOtherError { get; private set; }
    /// <summary>Of <see cref="Succeeded"/>, runs that exited 1 although every error they printed came from the parse
    /// after preprocessing (Clang's "Semantic Issue", "Parse Issue", ...), such as a builtin, attribute or asm only
    /// the build's compiler knows, or from an option the driver rejected. The preprocessor ran to the end of the
    /// source, as Clang's error limit is off, so the names are all there.</summary>
    public int SucceededPastParseErrors { get; private set; }
    public int FailedNoMessage { get; private set; }
    public int FailedNotStarted { get; private set; }
    /// <summary>Of <see cref="FailedHeaderNotFound"/>, those whose header is one of the C standard library's
    /// (<c>stdio.h</c>, <c>string.h</c>, ...): the build's C library, usually in its toolchain's folders, isn't
    /// where the command says. And the distinct headers not found, by name.</summary>
    public int FailedHeaderNotFoundCLibrary { get; private set; }
    public int HeadersNotFound { get; private set; }
    /// <summary>Of <see cref="HeadersNotFound"/>: those a file of the tree ends in (the tree has it, in a directory the
    /// command doesn't search), those some file outside the carve root includes (a toolchain's header), and those a
    /// command failed on that ran without an include directory inside the root where the build writes its output (a
    /// header the build makes, see <see cref="MissingDirsInRootFromBuild"/>), or without one outside the root.</summary>
    public int HeadersNotFoundInTree { get; private set; }
    public int HeadersNotFoundIncludedOutside { get; private set; }
    public int HeadersNotFoundWithoutBuildDir { get; private set; }
    public int HeadersNotFoundWithoutOutsideDir { get; private set; }
    /// <summary>Runs, clean or not, that printed an option the driver rejected. Under pp-trace that is an error but
    /// not a fatal one: the run goes on, and exits 0 when nothing else went wrong.</summary>
    public int OptionErrors { get; private set; }
    /// <summary>Of <c>ppTraceDropOptions</c>: the commands that had one, the arguments taken out, and the patterns
    /// that matched none (a typo, or a flag this log doesn't have).</summary>
    public int CommandsWithDropped { get; private set; }
    public int OptionsDropped { get; private set; }
    public int DropPatternsUnused { get; private set; }
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
    /// <paramref name="dropOptions"/> (<c>ppTraceDropOptions</c>) are taken out of every command first, and
    /// <paramref name="extraArgs"/> (<c>ppTraceArgs</c>) are added after its flags (see <see cref="Drop"/>).
    /// <paramref name="treeFiles"/>, the tree's files relative to <paramref name="root"/>, say which headers not found
    /// it has (<see cref="HeadersNotFoundInTree"/>).
    public static PpTrace Run(string exe, IReadOnlyList<CompileCommand> commands, int timeoutMs = 1_800_000, int? parallel = null, long? memoryBudget = null,
                              string? root = null, IReadOnlyList<string>? dropOptions = null, IReadOnlyList<string>? extraArgs = null,
                              IEnumerable<string>? treeFiles = null)
    {
        var drop = (dropOptions ?? Array.Empty<string>()).Select(DropPattern.Parse).ToList();
        var dropUsed = new int[drop.Count];
        var extra = extraArgs ?? Array.Empty<string>();
        var most = parallel ?? Math.Max(1, Environment.ProcessorCount - 1);
        using var gate = new ChildMemoryGate(memoryBudget ?? ChildMemoryGate.BudgetForRun(), most);
        var traced = commands.Where(c => Preprocessed(c.File)).ToList();
        var r = new PpTrace { Commands = traced.Count, NotPreprocessed = commands.Count - traced.Count, Version = ReadVersion(exe) };
        int succeeded = 0, missing = 0, refused = 0, incomplete = 0, timedOut = 0, outside = 0, inRoot = 0;
        var missingBy = new int[4];
        var missingInRoot = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(PathComparer.Default);
        var failedBy = new int[Enum.GetValues<Failure>().Length];
        int cLibrary = 0, optionErrors = 0, withDropped = 0, dropped = 0, pastParse = 0;
        var notFound = new System.Collections.Concurrent.ConcurrentDictionary<string, HeaderSeen>(StringComparer.Ordinal);
        var outDirs = OutputDirs(commands);
        var rootFull = root is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var namesLock = new object();
        Parallel.ForEach(traced, new ParallelOptions { MaxDegreeOfParallelism = most }, cmd =>
        {
            if (cmd.Incomplete || cmd.AlternativeDirectories.Count > 0) { Interlocked.Increment(ref incomplete); return; }
            if (drop.Count > 0)
            {
                var (kept, n) = Drop(cmd.Arguments, drop, dropUsed);
                if (n > 0) { Interlocked.Increment(ref withDropped); Interlocked.Add(ref dropped, n); cmd = cmd with { Arguments = kept }; }
            }
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
            bool ok, late, optionError, pastErrors;
            Failure failure;
            string? header, includer;
            while (true)
            {
                using var lease = gate.Admit();
                (ok, late, failure, header, includer, optionError, pastErrors) = TraceOne(exe, cmd, extra, names, timeoutMs, lease);
                if (!lease.Evicted) break;
                names.Clear();   // stopped for memory: run again when there is room
            }
            if (late) Interlocked.Increment(ref timedOut);
            if (optionError) Interlocked.Increment(ref optionErrors);
            if (!ok && !late)
            {
                Interlocked.Increment(ref failedBy[(int)failure]);
                if (header is not null)
                {
                    var seen = HeaderSeen.Any;
                    if (includer is not null && rootFull is not null && !Within(FullIn(cmd.Directory, includer) ?? rootFull, rootFull)) seen |= HeaderSeen.IncludedOutside;
                    if (dirs.Any(d => d.Inside && outDirs.Any(o => Within(o, d.Dir) || Within(d.Dir, o)))) seen |= HeaderSeen.WithoutBuildDir;
                    if (dirs.Any(d => !d.Inside)) seen |= HeaderSeen.WithoutOutsideDir;
                    notFound.AddOrUpdate(header, seen, (_, was) => was | seen);
                    if (CLibraryHeaders.Contains(header)) Interlocked.Increment(ref cLibrary);
                }
            }
            if (!ok) return;
            Interlocked.Increment(ref succeeded);
            if (pastErrors) Interlocked.Increment(ref pastParse);
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
        r.MissingDirsInRootFromBuild = missingInRoot.Keys.Count(d => outDirs.Any(o => Within(o, d) || Within(d, o)));
        r.Refused = refused;
        r.Incomplete = incomplete;
        r.TimedOut = timedOut;
        r.FailedCrash = failedBy[(int)Failure.Crash];
        r.FailedHeaderNotFound = failedBy[(int)Failure.HeaderNotFound];
        r.FailedOption = failedBy[(int)Failure.Option];
        r.FailedErrorLimit = failedBy[(int)Failure.ErrorLimit];
        r.FailedHashError = failedBy[(int)Failure.HashError];
        r.FailedPreprocessorError = failedBy[(int)Failure.PreprocessorError];
        r.FailedOtherError = failedBy[(int)Failure.Error];
        r.SucceededPastParseErrors = pastParse;
        r.FailedNoMessage = failedBy[(int)Failure.NoMessage];
        r.FailedNotStarted = failedBy[(int)Failure.NotStarted];
        r.FailedHeaderNotFoundCLibrary = cLibrary;
        r.HeadersNotFound = notFound.Count;
        r.HeadersNotFoundIncludedOutside = notFound.Values.Count(v => v.HasFlag(HeaderSeen.IncludedOutside));
        r.HeadersNotFoundWithoutBuildDir = notFound.Values.Count(v => v.HasFlag(HeaderSeen.WithoutBuildDir));
        r.HeadersNotFoundWithoutOutsideDir = notFound.Values.Count(v => v.HasFlag(HeaderSeen.WithoutOutsideDir));
        if (!notFound.IsEmpty && treeFiles is not null)
        {
            var spelled = notFound.Keys.Select(h => h.Replace('\\', '/')).ToList();
            var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in treeFiles)
                foreach (var h in spelled)
                    if (rel.EndsWith(h, StringComparison.OrdinalIgnoreCase) && (rel.Length == h.Length || rel[rel.Length - h.Length - 1] is '/' or '\\'))
                        inTree.Add(h);
            r.HeadersNotFoundInTree = inTree.Count;
        }
        r.OptionErrors = optionErrors;
        r.CommandsWithDropped = withDropped;
        r.OptionsDropped = dropped;
        r.DropPatternsUnused = dropUsed.Count(u => u == 0);
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
    /// its target when only the name says it, the command's flags without outputs or other sources,
    /// <paramref name="extra"/> (<c>ppTraceArgs</c>), then the source. After the command's own flags, an added
    /// <c>-isystem</c> is searched after the build's own directories. Between the two: no error limit (past it Clang
    /// stops, preprocessing too) and each error's category, which says whether the preprocessor or the parse after
    /// it reported it (see <see cref="ErrorStage"/>); after the command's own flags, these win.</summary>
    internal static List<string> CompileArguments(CompileCommand cmd, IReadOnlyList<string>? extra = null)
    {
        var driver = cmd.Driver is { } d ? d.Replace('\\', '/').Split('/')[^1] : "clang";
        if (driver.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) driver = driver[..^4];
        var args = new List<string> { driver };
        var flags = CompilerPreprocess.WithoutOutputs(cmd.Arguments);
        if (TargetOf(driver) is { } t && !flags.Any(a => a is "-target" || a.StartsWith("--target=", StringComparison.Ordinal)))
            args.Add("--target=" + t);
        args.AddRange(flags.Where(a => !BuildLogScraper.IsSourceFile(a)));
        args.Add("-ferror-limit=0");
        args.Add("-fdiagnostics-show-category=name");
        if (extra is not null) args.AddRange(extra);
        args.Add(cmd.File);
        return args;
    }

    /// <summary>One <c>ppTraceDropOptions</c> entry: a pattern for one argument (<c>*</c> matches any text), or two
    /// separated by a space, an option and its value in the next argument (<c>"-Xkq *"</c>).</summary>
    internal sealed record DropPattern(Regex Option, Regex? Value)
    {
        public static DropPattern Parse(string pattern)
        {
            var parts = pattern.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            static Regex Glob(string g) => new("^" + string.Join(".*", g.Split('*').Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant);
            return new DropPattern(Glob(parts.Length > 0 ? parts[0] : ""), parts.Length > 1 ? Glob(parts[1]) : null);
        }
    }

    /// <summary><paramref name="args"/> without what <paramref name="patterns"/> match, and how many arguments went;
    /// <paramref name="used"/> counts the matches of each pattern. The first pattern that matches an argument applies.</summary>
    internal static (List<string> Kept, int Dropped) Drop(IReadOnlyList<string> args, IReadOnlyList<DropPattern> patterns, int[] used)
    {
        var kept = new List<string>(args.Count);
        var dropped = 0;
        for (var i = 0; i < args.Count; i++)
        {
            var hit = -1;
            for (var k = 0; k < patterns.Count && hit < 0; k++)
            {
                var p = patterns[k];
                if (!p.Option.IsMatch(args[i])) continue;
                if (p.Value is not null && !(i + 1 < args.Count && p.Value.IsMatch(args[i + 1]))) continue;
                hit = k;
            }
            if (hit < 0) { kept.Add(args[i]); continue; }
            Interlocked.Increment(ref used[hit]);
            var n = patterns[hit].Value is null ? 1 : 2;
            dropped += n;
            i += n - 1;
        }
        return (kept, dropped);
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

    [Flags]
    enum HeaderSeen { Any = 0, IncludedOutside = 1, WithoutBuildDir = 2, WithoutOutsideDir = 4 }

    static string? FullIn(string dir, string path)
    {
        try { return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(dir, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
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

    /// <summary>Why a run failed. <see cref="Error"/> is an error none of the others names.</summary>
    public enum Failure { NotStarted, Crash, HeaderNotFound, Option, Error, NoMessage, ErrorLimit, HashError, PreprocessorError }

    /// <summary>What a line of stderr is: not an error, an option the driver rejected, an error the parse after
    /// preprocessing reported, one the preprocessor did, a fatal one, or another.</summary>
    public enum Stage { None, Option, Parse, Preprocessor, Fatal, Other }

    /// <summary>Clang's categories (<c>-fdiagnostics-show-category=name</c>) of errors the parse reports after the
    /// preprocessor gave it the tokens. Not "Lexical or Preprocessor Issue" (an <c>#if</c>, a paste, an include),
    /// "User-Defined Issue" (<c>#error</c>) or "Modules Issue": those change what was preprocessed.</summary>
    static readonly HashSet<string> ParseCategories = new(StringComparer.Ordinal)
    {
        "Semantic Issue", "Parse Issue", "Inline Assembly Issue", "Value Conversion Issue", "Format String Issue",
        "Nullability Issue", "Unused Entity Issue", "Deprecations", "Documentation Issue", "Lambda Issue",
        "Coroutines Issue", "Concepts Issue", "Generics Issue",
    };

    // The category ends an error line: "[Semantic Issue]", or after its flags: "[-Werror,-Wkq,Semantic Issue]".
    static readonly Regex CategoryTail = new(@"\[(?:-[^,\]]*,)*(?<cat>[^,\]\-][^,\]]*)\]\s*$", RegexOptions.CultureInvariant);

    internal static Stage ErrorStage(string line)
    {
        if (!line.Contains("error:", StringComparison.Ordinal)) return Stage.None;
        if (line.Contains("fatal error:", StringComparison.Ordinal)) return Stage.Fatal;
        if (IsOptionError(line)) return Stage.Option;
        if (CategoryTail.Match(line) is not { Success: true } m) return Stage.Other;
        var cat = m.Groups["cat"].Value;
        return ParseCategories.Contains(cat) ? Stage.Parse
             : cat is "Lexical or Preprocessor Issue" or "User-Defined Issue" ? Stage.Preprocessor : Stage.Other;
    }

    /// <summary>The C standard library's headers, C89 to C23.</summary>
    internal static readonly HashSet<string> CLibraryHeaders = new(StringComparer.Ordinal)
    {
        "assert.h", "complex.h", "ctype.h", "errno.h", "fenv.h", "float.h", "inttypes.h", "iso646.h", "limits.h",
        "locale.h", "math.h", "setjmp.h", "signal.h", "stdalign.h", "stdarg.h", "stdatomic.h", "stdbit.h", "stdbool.h",
        "stdckdint.h", "stddef.h", "stdint.h", "stdio.h", "stdlib.h", "stdnoreturn.h", "string.h", "tgmath.h",
        "threads.h", "time.h", "uchar.h", "wchar.h", "wctype.h",
    };

    // The includer is the path before ":line:col: ", where Clang gives one.
    static readonly Regex NotFoundLine = new(@"^(?:(?<file>.+?):\d+:\d+: )?.*?fatal error: '(?<h>[^']+)' file not found", RegexOptions.CultureInvariant);
    // The source line Clang shows under an error, with or without its "  12 | " margin.
    static readonly Regex HashErrorSnippet = new(@"^\s*(?:\d+\s*\|)?\s*#\s*error\b", RegexOptions.CultureInvariant);
    const string ErrorLimitText = "too many errors emitted";
    static readonly string[] OptionErrorTexts = { "unknown argument", "unsupported option", "unsupported argument", "unknown target", "invalid value", "invalid argument", "unsupported target" };

    /// <summary>Why a run failed, from the lines it printed to stderr (see <see cref="FailedCrash"/>); the header it
    /// couldn't find, for a fatal one.</summary>
    internal static bool IsOptionError(string line) =>
        line.Contains("error:", StringComparison.Ordinal) && OptionErrorTexts.Any(o => line.Contains(o, StringComparison.Ordinal));

    /// The lines are the error lines and the line after each, where Clang shows the source.
    internal static (Failure Why, string? Header, string? Includer) Classify(IEnumerable<string> stderr)
    {
        bool crash = false, option = false, other = false, hash = false, limit = false, pp = false, afterError = false;
        string? header = null, includer = null;
        foreach (var line in stderr)
        {
            if (line.Contains("PLEASE submit a bug report", StringComparison.Ordinal) || line.StartsWith("Stack dump:", StringComparison.Ordinal)) crash = true;
            if (header is null && NotFoundLine.Match(line) is { Success: true } m)
            {
                header = m.Groups["h"].Value;
                includer = m.Groups["file"].Success ? m.Groups["file"].Value : null;
            }
            if (afterError && HashErrorSnippet.IsMatch(line)) hash = true;
            afterError = false;
            if (line.Contains(ErrorLimitText, StringComparison.Ordinal)) { limit = true; continue; }
            var stage = ErrorStage(line);
            if (stage == Stage.None) continue;
            afterError = true;
            if (stage == Stage.Option) option = true;
            else if (stage == Stage.Preprocessor) pp = true;
            else if (!NotFoundLine.IsMatch(line)) other = true;
        }
        var why = crash ? Failure.Crash : header is not null ? Failure.HeaderNotFound : limit ? Failure.ErrorLimit : hash ? Failure.HashError
                : pp ? Failure.PreprocessorError : other ? Failure.Error : option ? Failure.Option : Failure.NoMessage;
        return (why, header, includer);
    }

    private static (bool Ok, bool TimedOut, Failure Why, string? Header, string? Includer, bool OptionError, bool PastParseErrors) TraceOne(string exe, CompileCommand cmd, IReadOnlyList<string> extra, HashSet<ulong> names, int timeoutMs, ChildMemoryGate.Lease lease)
    {
        // The command goes in a compile database of its own: no command-line quoting or length limit to meet.
        var db = Path.Combine(Path.GetTempPath(), "codecarver-ppt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(db);
            var source = Path.GetFullPath(Path.Combine(cmd.Directory, cmd.File));
            File.WriteAllText(Path.Combine(db, "compile_commands.json"), JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object> { ["directory"] = cmd.Directory, ["file"] = source, ["arguments"] = CompileArguments(cmd, extra) },
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
            if (p is null) return (false, false, Failure.NotStarted, null, null, false, false);
            lease.Track(p);
            p.StandardInput.Close();
            // Drained so a full stderr pipe can't block it. Unsafe: a crash, or an error neither the parse after
            // preprocessing nor a rejected option gave.
            bool fatal = false, optionError = false, parseError = false, unsafeError = false;
            var said = new List<string>();   // what's needed to say why it failed: error lines, the source line under each, and a crash's
            var afterError = false;
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                var stage = ErrorStage(line);
                var crash = line.Contains("PLEASE submit", StringComparison.Ordinal) || line.StartsWith("Stack dump:", StringComparison.Ordinal);
                if (stage == Stage.Fatal) fatal = true;
                if (stage == Stage.Option) optionError = true;
                if (stage == Stage.Parse) parseError = true;
                var isUnsafe = crash || stage is Stage.Fatal or Stage.Preprocessor or Stage.Other;
                if (isUnsafe) unsafeError = true;
                // Past the first 128 lines only those that say why it failed: the parse's errors can be thousands.
                var keep = (stage != Stage.None || afterError || crash) && (said.Count < 128 || (said.Count < 512 && (isUnsafe || afterError)));
                if (keep) said.Add(line);
                afterError = keep && stage != Stage.None;
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
            // Exit 1 and only the parse's errors: nothing stopped the preprocessor before the end of the source.
            var pastParse = p.ExitCode == 1 && !fatal && !unsafeError && parseError;
            var clean = p.ExitCode == 0 || pastParse || (AssemblyWithCpp(cmd.File) && p.ExitCode == 1 && !fatal);
            var ok = !timedOut && !lease.Evicted && clean;
            var (why, header, includer) = ok ? (Failure.NoMessage, null, null) : Classify(said);
            return (ok, timedOut, why, header, includer, optionError, ok && pastParse);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (false, false, Failure.NotStarted, null, null, false, false); }
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
