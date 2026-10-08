using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Sound by construction for what it covers. A header's blocks are decided only when every compile command ran: a
/// compile that couldn't be preprocessed might have taken a block the others skipped. A source file's blocks are
/// decided when every command compiling it ran. Anything else keeps the scanner's answer.
/// </summary>
public sealed class CompilerPreprocess
{
    /// <summary>Per in-root file (relative, '/'), the 1-based lines a compile let through.</summary>
    private readonly Dictionary<string, HashSet<int>> _live;
    private readonly HashSet<string> _decided;

    public int Commands { get; }
    public int Succeeded { get; }
    public int Failed => Commands - Succeeded;
    /// <summary>Files whose conditional blocks the compiler decided.</summary>
    public int FilesDecided => _decided.Count;

    private CompilerPreprocess(Dictionary<string, HashSet<int>> live, HashSet<string> decided, int commands, int succeeded)
    {
        _live = live;
        _decided = decided;
        Commands = commands;
        Succeeded = succeeded;
    }

    /// <summary>
    /// Preprocess every command. <paramref name="driverFor"/> names the executable to run a command with (null: it
    /// can't be run here, which counts as a failure). <paramref name="sourceOf"/> maps a command to its source file's
    /// path relative to <paramref name="root"/> (null when outside it).
    /// </summary>
    public static CompilerPreprocess Run(IReadOnlyList<CompileCommand> commands, string root,
                                         Func<CompileCommand, string?> driverFor, Func<CompileCommand, string?> sourceOf,
                                         int timeoutMs = 300_000)
    {
        var rootPrefix = PathComparer.DirectoryPrefix(Path.GetFullPath(root));
        var live = new ConcurrentDictionary<string, HashSet<int>>(PathComparer.Default);
        var okBySource = new ConcurrentDictionary<string, bool>(PathComparer.Default);   // false once any of its commands failed
        var succeeded = 0;
        Parallel.ForEach(commands, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, cmd =>
        {
            var src = sourceOf(cmd);
            var local = new Dictionary<string, HashSet<int>>(PathComparer.Default);
            var ok = driverFor(cmd) is { } exe && Preprocess(exe, cmd, rootPrefix, local, timeoutMs)
                     && (src is null || local.ContainsKey(src));
            if (src is not null) okBySource.AddOrUpdate(src, ok, (_, was) => was && ok);
            if (!ok) return;
            Interlocked.Increment(ref succeeded);
            foreach (var (rel, lines) in local)
            {
                var set = live.GetOrAdd(rel, _ => new HashSet<int>());
                lock (set) set.UnionWith(lines);
            }
        });

        var allRan = succeeded == commands.Count;
        var decided = new HashSet<string>(PathComparer.Default);
        foreach (var rel in live.Keys)
            if (okBySource.TryGetValue(rel, out var ok) ? ok : allRan) decided.Add(rel);
        // A source file every command of which ran, even with no line through (all of it #if'd out).
        foreach (var (rel, ok) in okBySource) if (ok) decided.Add(rel);
        return new CompilerPreprocess(new Dictionary<string, HashSet<int>>(live, PathComparer.Default), decided, commands.Count, succeeded);
    }

    /// <summary>The dead-line map (1-based, true = dead) of <paramref name="rel"/> as the compiles decided it, or null
    /// when they didn't (see the class summary).</summary>
    public bool[]? DeadLines(string rel, string text)
    {
        if (!_decided.Contains(rel)) return null;
        return DeadLineMap(text, _live.TryGetValue(rel, out var l) ? l : new HashSet<int>());
    }

    /// <summary>Is <paramref name="rel"/> decided by the compiles?</summary>
    public bool Decides(string rel) => _decided.Contains(rel);

    // Flags that write files or turn the compile into something else; -E and -dD replace them.
    private static bool DropFlag(string a, out bool takesValue)
    {
        takesValue = a is "-o" or "-MF" or "-MT" or "-MQ";
        if (takesValue) return true;
        return a is "-c" or "-S" or "-E" or "-M" or "-MM" or "-MD" or "-MMD" or "-MP" or "-MG" or "-P" or "-C" or "-CC"
               || (a.StartsWith("-o", StringComparison.Ordinal) && a.Length > 2)
               || a.StartsWith("-MF", StringComparison.Ordinal) || a.StartsWith("-MT", StringComparison.Ordinal)
               || a.StartsWith("-MQ", StringComparison.Ordinal)
               || (a.StartsWith("-Wp,", StringComparison.Ordinal) && a.Contains("-M", StringComparison.Ordinal))
               || a.StartsWith("-save-temps", StringComparison.Ordinal) || a.StartsWith("-fdump-", StringComparison.Ordinal)
               || a.StartsWith("-Werror", StringComparison.Ordinal) || a.StartsWith("-d", StringComparison.Ordinal);
    }

    private static readonly Regex Marker = new(@"^#(?:line)?\s+(\d+)\s+""((?:[^""\\]|\\.)*)""(.*)$", RegexOptions.Compiled);

    /// <summary>Run one command with -E -dD and record, per in-root file, the lines that came through. False when the
    /// compiler failed, timed out, or printed no line marker (not a GCC/Clang-style preprocessor).</summary>
    private static bool Preprocess(string exe, CompileCommand cmd, string rootPrefix, Dictionary<string, HashSet<int>> live, int timeoutMs)
    {
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
            for (var i = 0; i < cmd.Arguments.Count; i++)
            {
                if (DropFlag(cmd.Arguments[i], out var takesValue)) { if (takesValue) i++; continue; }
                psi.ArgumentList.Add(cmd.Arguments[i]);
            }
            psi.ArgumentList.Add("-E");
            psi.ArgumentList.Add("-dD");

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Close();
            var errTask = p.StandardError.ReadToEndAsync();   // drained so a full stderr pipe can't block the compiler
            var readTask = Task.Run(() => Read(p.StandardOutput, dir, rootPrefix, live));
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            p.WaitForExit();
            var markers = readTask.Result;
            _ = errTask.Result;
            return p.ExitCode == 0 && markers;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>Walk -E output: a marker sets the file and line, each further output line is the next source line. A
    /// non-blank line came through; returning from an #include (flag 2) means the #include line itself did.</summary>
    private static bool Read(StreamReader output, string cmdDir, string rootPrefix, Dictionary<string, HashSet<int>> live)
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
                {
                    var full = Path.GetFullPath(Path.IsPathFullyQualified(p) ? p : Path.Combine(cmdDir, p));
                    if (full.StartsWith(rootPrefix, PathComparer.Comparison))
                        r = full[rootPrefix.Length..].Replace('\\', '/');
                }
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
    /// nested in it, came through. A block that holds an <c>#include</c> stays live without evidence (a second
    /// inclusion of a guarded header prints nothing). A file with <c>#line</c> directives (generated code) renumbers its
    /// markers, and an <c>#if</c> left open can't be read: both null.
    /// </summary>
    public static bool[]? DeadLineMap(string text, IReadOnlySet<int> live)
    {
        if (text.Contains('#') && LineDirective.IsMatch(text)) return null;
        var lines = text.Split('\n');
        var groupOf = new int[lines.Length + 1];          // innermost block of each line (0 = the file itself)
        var parent = new List<int> { -1 };
        var hasInclude = new List<bool> { false };
        var stack = new Stack<int>();
        stack.Push(0);
        var inComment = false;
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
                    stack.Push(NewGroup(stack.Peek()));
                }
                else if (kw is "elif" or "elifdef" or "elifndef" or "else")
                {
                    if (stack.Count < 2) return null;
                    stack.Pop();
                    for (var k = i; k <= last; k++) groupOf[k + 1] = stack.Peek();
                    stack.Push(NewGroup(stack.Peek()));
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
                    if (kw is "include" or "include_next" or "import") hasInclude[stack.Peek()] = true;
                }
                for (var k = i; k <= last; k++) inComment = EndsInComment(lines[k], inComment);
                i = last;
                continue;
            }
            groupOf[i + 1] = stack.Peek();
            inComment = EndsInComment(lines[i], inComment);
        }
        if (stack.Count != 1) return null;

        var isLive = new bool[parent.Count];
        isLive[0] = true;
        for (var g = 1; g < parent.Count; g++) if (hasInclude[g]) isLive[g] = true;
        foreach (var ln in live)
            if (ln >= 1 && ln < groupOf.Length) isLive[groupOf[ln]] = true;
        // A live block's enclosing blocks are live.
        for (var g = parent.Count - 1; g > 0; g--)
            if (isLive[g]) for (var p = parent[g]; p > 0 && !isLive[p]; p = parent[p]) isLive[p] = true;
        var dead = new bool[lines.Length + 1];
        for (var ln = 1; ln <= lines.Length; ln++) dead[ln] = !LiveChain(groupOf[ln]);
        return dead;

        int NewGroup(int of) { parent.Add(of); hasInclude.Add(false); return parent.Count - 1; }
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

    private static bool EndsInComment(string line, bool inComment)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inComment) { if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { inComment = false; i++; } continue; }
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return false;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inComment = true; i++; }
        }
        return inComment;
    }
}
