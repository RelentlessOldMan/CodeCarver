using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The build's own preprocessor decides which <c>#if</c> branches are live: every compile command is run again with
/// <c>-E -dD</c> in place of <c>-c</c>, and its line markers say which lines of each file (the source and every header
/// it includes) came through. A conditional block is live when any line of it came through in any compile; one no
/// compile let through is dead. Macros a config header defines, <c>#undef</c>s, include order: the compiler has
/// already applied them all, where the conditional scanner can only guess.
///
/// It decides a file only where the compiles it saw are all the compiles there are:
/// <list type="bullet">
/// <item>every command ran (a command that couldn't — incomplete, an unknown or refused driver, a missing include
///   path, a failure — might have taken a block the others skipped), and every translation unit the carve has is
///   compiled by one, so a header's blocks are decided; or</item>
/// <item>short of that, a source file every command of which ran and that no other compile includes.</item>
/// </list>
/// A block that opens inside an unclosed parenthesis (an argument of a multi-line macro call, whose whole expansion
/// prints on the call's first line) is never decided dead. Anything else keeps the conditional scanner's answer.
/// </summary>
public sealed class CompilerPreprocess
{
    /// <summary>Per in-root file (relative, '/'), the 1-based lines a compile let through.</summary>
    private readonly Dictionary<string, HashSet<int>> _live;
    private readonly HashSet<string> _decided;

    public int Commands { get; }
    public int Succeeded { get; }
    public int Failed => Commands - Succeeded;
    /// <summary>Commands not run because an include path or the source they name does not exist here.</summary>
    public int MissingPaths { get; }
    /// <summary>Commands not run because the driver is not a GCC/Clang-style compiler or a flag would load or run code.</summary>
    public int Refused { get; }
    /// <summary>Files whose conditional blocks the compiler decided.</summary>
    public int FilesDecided => _decided.Count;

    private CompilerPreprocess(Dictionary<string, HashSet<int>> live, HashSet<string> decided, int commands, int succeeded,
                               int missingPaths, int refused)
    {
        _live = live;
        _decided = decided;
        Commands = commands;
        Succeeded = succeeded;
        MissingPaths = missingPaths;
        Refused = refused;
    }

    /// <summary>
    /// Preprocess every command. <paramref name="driverFor"/> names the executable to run a command with (null: it
    /// can't or mustn't be run here, a failure). <paramref name="sourceOf"/> maps a command to its source file's path
    /// relative to <paramref name="root"/> (null when outside it). <paramref name="units"/> are the translation units
    /// the carve has: headers are decided only when each is compiled by a command that ran.
    /// </summary>
    public static CompilerPreprocess Run(IReadOnlyList<CompileCommand> commands, string root,
                                         Func<CompileCommand, string?> driverFor, Func<CompileCommand, string?> sourceOf,
                                         IEnumerable<string>? units = null, int timeoutMs = 300_000)
    {
        var rootFull = Path.GetFullPath(root);
        var paths = new PathResolver(rootFull);
        var live = new ConcurrentDictionary<string, HashSet<int>>(PathComparer.Default);
        var okBySource = new ConcurrentDictionary<string, bool>(PathComparer.Default);   // false once any of its commands failed
        var included = new ConcurrentDictionary<string, byte>(PathComparer.Default);     // reached by a compile of another file
        int succeeded = 0, missing = 0, refused = 0;
        Parallel.ForEach(commands, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, cmd =>
        {
            var src = sourceOf(cmd);
            var local = new Dictionary<string, HashSet<int>>(PathComparer.Default);
            var ok = false;
            if (cmd.Incomplete || cmd.AlternativeDirectories.Count > 0) { }
            else if (Refuse(cmd.Arguments)) Interlocked.Increment(ref refused);
            else if (MissingPath(cmd)) Interlocked.Increment(ref missing);
            else if (driverFor(cmd) is not { } exe) Interlocked.Increment(ref refused);
            else ok = Preprocess(exe, cmd, paths, local, timeoutMs) && (src is null || local.ContainsKey(src));
            if (src is not null) okBySource.AddOrUpdate(src, ok, (_, was) => was && ok);
            if (!ok) return;
            Interlocked.Increment(ref succeeded);
            foreach (var (rel, lines) in local)
            {
                if (src is null || !PathComparer.Default.Equals(rel, src)) included.TryAdd(rel, 0);
                var set = live.GetOrAdd(rel, _ => new HashSet<int>());
                lock (set) set.UnionWith(lines);
            }
        });

        var allRan = succeeded == commands.Count
                     && (units ?? Array.Empty<string>()).All(u => okBySource.TryGetValue(u, out var uok) && uok);
        var decided = new HashSet<string>(PathComparer.Default);
        var ownOnly = new HashSet<string>(PathComparer.Default);
        foreach (var rel in live.Keys.Concat(okBySource.Keys))
        {
            if (allRan) decided.Add(rel);
            // Its own commands all ran, no compile that ran includes it, and it is a translation unit (a header compiled
            // on its own, a precompiled header, is included by others).
            else if (okBySource.TryGetValue(rel, out var ok) && ok && !included.ContainsKey(rel) && IsUnit(rel))
            {
                decided.Add(rel);
                ownOnly.Add(rel);
            }
        }
        return new CompilerPreprocess(new Dictionary<string, HashSet<int>>(live, PathComparer.Default), decided,
                                      commands.Count, succeeded, missing, refused) { _ownOnly = ownOnly };
    }

    private HashSet<string> _ownOnly = new();

    /// <summary>Source files another file #includes (a unity build): a compile that didn't run may include one, so one
    /// decided only by its own commands isn't decided. Consulted when asked, so it can be filled after the run.</summary>
    public Func<string, bool>? IncludedByAnother { get; set; }

    private static bool IsUnit(string rel)
        => new[] { ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm" }.Any(e => rel.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The dead-line map (1-based, true = dead) of <paramref name="rel"/> as the compiles decided it, or null
    /// when they didn't (see the class summary). <paramref name="text"/> must be the file's original text.</summary>
    public bool[]? DeadLines(string rel, string text)
    {
        if (!Decides(rel)) return null;
        return DeadLineMap(text, _live.TryGetValue(rel, out var l) ? l : new HashSet<int>());
    }

    /// <summary>Is <paramref name="rel"/> decided by the compiles?</summary>
    public bool Decides(string rel)
        => _decided.Contains(rel) && !(_ownOnly.Contains(rel) && IncludedByAnother?.Invoke(rel) == true);

    // ---- drivers and flags --------------------------------------------------------------------------------------

    private static readonly Regex GccFamily = new(@"^(?:[\w.+]+-)*(?:gcc|g\+\+|cc|c\+\+|clang|clang\+\+)(?:-\d+(?:\.\d+)*)?$",
                                                  RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A GCC/Clang-style driver by its base name (<c>gcc</c>, <c>arm-none-eabi-g++</c>, <c>clang-17</c>): the
    /// only drivers whose <c>-E</c> output is read, and the only ones run.</summary>
    public static bool IsGccFamily(string driver)
    {
        var name = driver.Replace('\\', '/').Split('/')[^1];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return GccFamily.IsMatch(name);
    }

    /// <summary>Flags that load or run other code (a wrapper, specs, plugins, another tool directory): a carve never
    /// runs a command carrying one.</summary>
    public static bool Refuse(IReadOnlyList<string> args)
    {
        foreach (var a in args)
            if (a is "-wrapper" or "-specs" or "--specs" or "-load" or "-plugin" || a.StartsWith("-B", StringComparison.Ordinal)
                || a.StartsWith("-specs=", StringComparison.Ordinal) || a.StartsWith("--specs=", StringComparison.Ordinal)
                || a.StartsWith("-fplugin", StringComparison.Ordinal) || a.StartsWith("-fpass-plugin", StringComparison.Ordinal)
                || a.StartsWith("-iplugindir", StringComparison.Ordinal) || a.StartsWith("-wrapper", StringComparison.Ordinal))
                return true;
        return false;
    }

    // Path-bearing flags: separate (value is the next argument) and joined (value follows the flag).
    private static readonly string[] PathFlags = { "-isystem", "-iquote", "-idirafter", "-include", "-imacros", "-I" };

    /// <summary>The arguments with every path-like value rewritten by <paramref name="map"/>: plain arguments (the
    /// source, separate flag values) and the joined values of include flags. <paramref name="map"/> returns a path it
    /// doesn't map unchanged.</summary>
    public static IReadOnlyList<string> MapArguments(IReadOnlyList<string> args, Func<string, string> map)
    {
        var r = new List<string>(args.Count);
        foreach (var a in args)
        {
            if (a.Length > 0 && a[0] != '-' && a[0] != '@') { r.Add(map(a)); continue; }
            var flag = PathFlags.FirstOrDefault(f => a.Length > f.Length && a.StartsWith(f, StringComparison.Ordinal));
            r.Add(flag is null ? a : flag + map(a[flag.Length..]));
        }
        return r;
    }

    /// <summary>Does an include directory, forced include or the source the command names not exist here? gcc skips a
    /// missing -I silently, and the same name then resolves somewhere else: a different configuration.</summary>
    private static bool MissingPath(CompileCommand cmd)
    {
        string Full(string p) => Path.IsPathFullyQualified(p) ? p : Path.Combine(cmd.Directory, p);
        try
        {
            if (!File.Exists(Full(cmd.File))) return true;
            var args = cmd.Arguments;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                var flag = PathFlags.FirstOrDefault(f => a.StartsWith(f, StringComparison.Ordinal));
                if (flag is null) continue;
                var value = a.Length > flag.Length ? a[flag.Length..] : i + 1 < args.Count ? args[++i] : null;
                if (value is null || value.StartsWith('=')) continue;   // sysroot-relative: the compiler's own
                var full = Full(value);
                if (flag is "-include" or "-imacros" ? !File.Exists(full) : !Directory.Exists(full)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }

    // Flags that write files or turn the compile into something else; -E and -dD replace them. Out: how many of the
    // following arguments are its value.
    private static bool DropFlag(string a, out int values)
    {
        values = a is "-o" or "-MF" or "-MT" or "-MQ" or "-MJ" or "-dumpbase" or "-dumpdir" or "-dumpbase-ext" ? 1 : 0;
        if (values > 0) return true;
        return a is "-c" or "-S" or "-E" or "-M" or "-MM" or "-MD" or "-MMD" or "-MP" or "-MG" or "-P" or "-C" or "-CC"
                    or "-dM" or "-dD" or "-dN" or "-dI" or "-dU" or "--write-dependencies" or "--write-user-dependencies"
               || (a.StartsWith("-o", StringComparison.Ordinal) && a.Length > 2)
               || a.StartsWith("-MF", StringComparison.Ordinal) || a.StartsWith("-MT", StringComparison.Ordinal)
               || a.StartsWith("-MQ", StringComparison.Ordinal) || a.StartsWith("-MJ", StringComparison.Ordinal)
               || a.StartsWith("-save-temps", StringComparison.Ordinal) || a.StartsWith("--save-temps", StringComparison.Ordinal)
               || a.StartsWith("-fdump-", StringComparison.Ordinal) || a.StartsWith("-Werror", StringComparison.Ordinal);
    }

    /// <summary><c>-Wp,a,b,c</c> without its dependency-output pieces (<c>-MD,file</c>, <c>-MF,file</c>, ...), or null
    /// when nothing is left.</summary>
    private static string? WpWithoutDeps(string a)
    {
        var parts = a[4..].Split(',');
        var keep = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p is "-MD" or "-MMD" or "-MF" or "-MT" or "-MQ") { i++; continue; }
            if (p is "-M" or "-MM" or "-MP" or "-MG") continue;
            keep.Add(p);
        }
        return keep.Count == 0 ? null : "-Wp," + string.Join(',', keep);
    }

    /// <summary>The command's arguments for <c>-E -dD</c>.</summary>
    public static List<string> PreprocessArguments(IReadOnlyList<string> args)
    {
        var r = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("-Wp,", StringComparison.Ordinal)) { if (WpWithoutDeps(a) is { } w) r.Add(w); continue; }
            if (DropFlag(a, out var values)) { i += values; continue; }
            r.Add(a);
        }
        r.Add("-E");
        r.Add("-dD");
        return r;
    }

    private const int MaxCommandLine = 8000;

    /// <summary>A GCC response file holding <paramref name="args"/>: each quoted, backslashes and quotes escaped.</summary>
    private static string ResponseFile(IEnumerable<string> args)
        => string.Join('\n', args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

    /// <summary>Run one command with -E -dD and record, per in-root file, the lines that came through. False when the
    /// compiler failed, timed out, or printed no line marker (not a GCC/Clang-style preprocessor).</summary>
    private static bool Preprocess(string exe, CompileCommand cmd, PathResolver paths, Dictionary<string, HashSet<int>> live, int timeoutMs)
    {
        string? rsp = null;
        try
        {
            var dir = cmd.Directory;
            if (!Directory.Exists(dir)) return false;
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            var args = PreprocessArguments(cmd.Arguments);
            if (args.Sum(a => a.Length + 3) > MaxCommandLine)
            {
                // A long line is why builds use response files; CreateProcess caps the line at ~32K characters.
                rsp = Path.Combine(Path.GetTempPath(), "codecarver-pp-" + Guid.NewGuid().ToString("N") + ".rsp");
                File.WriteAllText(rsp, ResponseFile(args));
                psi.ArgumentList.Add("@" + rsp);
            }
            else
                foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Close();
            p.ErrorDataReceived += (_, _) => { };   // drained so a full stderr pipe can't block the compiler
            p.BeginErrorReadLine();
            // Read on this thread; a timer kills a hung compiler, which closes the pipe and ends the read.
            var timedOut = false;
            using var timer = new Timer(_ =>
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }, null, timeoutMs, Timeout.Infinite);
            var markers = Read(p.StandardOutput, dir, paths, live);
            p.WaitForExit();
            return !timedOut && p.ExitCode == 0 && markers;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        finally
        {
            if (rsp is not null) try { File.Delete(rsp); } catch { /* best effort */ }
        }
    }

    private static readonly Regex Marker = new(@"^#(?:line)?\s+(\d+)\s+""((?:[^""\\]|\\.)*)""(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// In-root paths for the files a marker names, by where they really are: one file reached through a junction, a
    /// symlinked include directory or a subst drive is one file, under the spelling the carve walks.
    /// </summary>
    private sealed class PathResolver
    {
        private readonly string _rootPrefix;
        private readonly string? _realRootPrefix;
        private readonly ConcurrentDictionary<string, string?> _realDir = new(PathComparer.Default);

        public PathResolver(string rootFull)
        {
            _rootPrefix = PathComparer.DirectoryPrefix(rootFull);
            _realRootPrefix = RealPath.Directory(rootFull) is { } rr ? PathComparer.DirectoryPrefix(rr) : null;
        }

        public string? Rel(string full)
        {
            var dir = Path.GetDirectoryName(full);
            var real = dir is null ? null : _realDir.GetOrAdd(dir, d => RealPath.Directory(d));
            if (real is not null && _realRootPrefix is not null)
            {
                var file = PathComparer.DirectoryPrefix(real) + Path.GetFileName(full);
                return file.StartsWith(_realRootPrefix, PathComparer.Comparison) ? file[_realRootPrefix.Length..].Replace('\\', '/') : null;
            }
            return full.StartsWith(_rootPrefix, PathComparer.Comparison) ? full[_rootPrefix.Length..].Replace('\\', '/') : null;
        }
    }

    /// <summary>Walk -E output: a marker sets the file and line, each further output line is the next source line. A
    /// non-blank line came through; returning from an #include (flag 2) means the #include line itself did.</summary>
    private static bool Read(StreamReader output, string cmdDir, PathResolver paths, Dictionary<string, HashSet<int>> live)
    {
        var any = false;
        HashSet<int>? cur = null;
        var line = 0;
        var relOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Rel(string spelled)
        {
            if (relOf.TryGetValue(spelled, out var r)) return r;
            r = null;
            try
            {
                var p = spelled.Replace("\\\\", "\\").Replace("\\\"", "\"");
                if (!p.StartsWith('<'))
                    r = paths.Rel(Path.GetFullPath(Path.IsPathFullyQualified(p) ? p : Path.Combine(cmdDir, p)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            return relOf[spelled] = r;
        }
        HashSet<int>? SetFor(string? rel)
        {
            if (rel is null) return null;
            if (!live.TryGetValue(rel, out var s)) live[rel] = s = new HashSet<int>();
            return s;
        }

        for (var text = output.ReadLine(); text is not null; text = output.ReadLine())
        {
            if (text.Length > 0 && text[0] == '#' && Marker.Match(text) is { Success: true } m)
            {
                any = true;
                line = int.Parse(m.Groups[1].Value);
                cur = SetFor(Rel(m.Groups[2].Value));
                // `# 13 "a.c" 2`: back in a.c after the #include on line 12.
                if (cur is not null && line > 1 && Regex.IsMatch(m.Groups[3].Value, @"(^|\s)2(\s|$)")) cur.Add(line - 1);
                continue;
            }
            if (cur is not null && !string.IsNullOrWhiteSpace(text)) cur.Add(line);
            line++;
        }
        return any;
    }

    private static readonly Regex LineDirective = new(@"^[ \t]*#[ \t]*(?:line\b|\d)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Dead lines from what came through: every conditional block (the lines between one directive of an
    /// <c>#if</c>/<c>#elif</c>/<c>#else</c>/<c>#endif</c> chain and the next) is live when a line in it, or in a block
    /// nested in it, came through. Live without evidence: a block that holds an <c>#include</c> (a second inclusion of
    /// a guarded header prints nothing), and a block that opens inside an unclosed parenthesis (a multi-line macro
    /// call's arguments print blank: its expansion is all on the call's first line). A file with <c>#line</c>
    /// directives (generated code) renumbers its markers, and an <c>#if</c> left open can't be read: both null.
    /// </summary>
    public static bool[]? DeadLineMap(string text, IReadOnlySet<int> live)
    {
        if (text.Contains('#') && LineDirective.IsMatch(text)) return null;
        var lines = text.Split('\n');
        var groupOf = new int[lines.Length + 1];          // innermost block of each line (0 = the file itself)
        var parent = new List<int> { -1 };
        var forced = new List<bool> { false };            // live without evidence
        var stack = new Stack<int>();
        stack.Push(0);
        var inComment = false;
        var depth = 0;                                     // open parentheses in code so far (every branch counted)
        for (var i = 0; i < lines.Length; i++)
        {
            var startsInComment = inComment;
            var t = lines[i].TrimStart();
            var last = i;
            if (!startsInComment && t.StartsWith('#'))
            {
                while (lines[last].TrimEnd('\r').EndsWith('\\') && last + 1 < lines.Length) last++;
                var kw = Keyword(t);
                if (kw is "if" or "ifdef" or "ifndef")
                {
                    for (var k = i; k <= last; k++) groupOf[k + 1] = stack.Peek();
                    stack.Push(NewGroup(stack.Peek(), depth > 0));
                }
                else if (kw is "elif" or "elifdef" or "elifndef" or "else")
                {
                    if (stack.Count < 2) return null;
                    stack.Pop();
                    for (var k = i; k <= last; k++) groupOf[k + 1] = stack.Peek();
                    stack.Push(NewGroup(stack.Peek(), depth > 0));
                }
                else if (kw == "endif")
                {
                    if (stack.Count < 2) return null;
                    stack.Pop();
                    for (var k = i; k <= last; k++) groupOf[k + 1] = stack.Peek();
                }
                else
                {
                    for (var k = i; k <= last; k++) groupOf[k + 1] = stack.Peek();
                    if (kw is "include" or "include_next" or "import") forced[stack.Peek()] = true;
                }
                for (var k = i; k <= last; k++) inComment = EndsInComment(lines[k], inComment);
                i = last;
                continue;
            }
            groupOf[i + 1] = stack.Peek();
            (inComment, depth) = Scan(lines[i], inComment, depth);
        }
        if (stack.Count != 1) return null;

        var isLive = new bool[parent.Count];
        isLive[0] = true;
        for (var g = 1; g < parent.Count; g++) if (forced[g]) isLive[g] = true;
        foreach (var ln in live)
            if (ln >= 1 && ln < groupOf.Length) isLive[groupOf[ln]] = true;
        // A live block's enclosing blocks are live.
        for (var g = parent.Count - 1; g > 0; g--)
            if (isLive[g]) for (var p = parent[g]; p > 0 && !isLive[p]; p = parent[p]) isLive[p] = true;
        var dead = new bool[lines.Length + 1];
        for (var ln = 1; ln <= lines.Length; ln++) dead[ln] = !LiveChain(groupOf[ln]);
        return dead;

        int NewGroup(int of, bool inCall) { parent.Add(of); forced.Add(inCall); return parent.Count - 1; }
        bool LiveChain(int g)
        {
            for (; g > 0; g = parent[g]) if (!isLive[g]) return false;
            return true;
        }
    }

    private static string Keyword(string trimmed)
    {
        var s = trimmed[1..].TrimStart();
        var i = 0;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return s[..i];
    }

    private static bool EndsInComment(string line, bool inComment) => Scan(line, inComment, 0).InComment;

    /// <summary>Block-comment state after <paramref name="line"/>, and the parenthesis depth with its code counted
    /// (comments, strings and character literals skipped; never below zero).</summary>
    private static (bool InComment, int Depth) Scan(string line, bool inComment, int depth)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inComment) { if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { inComment = false; i++; } continue; }
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inComment = true; i++; }
            else if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
        }
        return (inComment, depth);
    }
}
