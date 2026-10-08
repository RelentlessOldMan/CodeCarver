using System.Text.Json;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Turns a real build log into per-file <see cref="CompileCommand"/>s — the generic, build-system-agnostic
/// config provider (Seam 1). Whatever compiler a build invokes, its command line carries the exact
/// <c>-D</c>/<c>-I</c> flags; this scrapes them so CodeCarver can resolve the preprocessor the way the
/// real build does, without needing a <c>compile_commands.json</c> most builds never emit.
///
/// It recognises the common compiler drivers (gcc/clang/cc/g++/cl and cross variants like
/// arm-none-eabi-gcc), extracts source files + defines + includes (GNU <c>-D/-I</c> and MSVC
/// <c>/D//I</c>), splits multi-source compile lines into one command each, honours a leading
/// <c>cd DIR &amp;&amp;</c>, and skips link-only lines. Deliberately tolerant: an unrecognised line is
/// skipped, never fatal.
///
/// <para>It ALSO accepts a <c>compile_commands.json</c> (a JSON array of <c>{directory,file,command}</c> or
/// <c>{directory,file,arguments[]}</c> entries) — the format CMake and our synthetic generator emit, and
/// the shape a real build's DB has when one exists. <see cref="Parse"/> auto-detects it (leading <c>[</c>)
/// so the same <c>--build-log</c> flag consumes either a text log or a JSON DB. A malformed DB yields
/// nothing rather than throwing, matching the text path's tolerance.</para>
/// </summary>
public static class BuildLogScraper
{
    /// <summary>Optional inputs for <see cref="Parse(string, ScrapeOptions?)"/>.</summary>
    public sealed record ScrapeOptions
    {
        /// <summary>Extra compiler driver names for text logs (e.g. armcc, iccarm) — matched like gcc.</summary>
        public IReadOnlyList<string> CompilerNames { get; init; } = Array.Empty<string>();

        /// <summary>Reads an <c>@response</c> file: (command directory, path as written) → text, or null.</summary>
        public Func<string, string, string?>? ReadResponseFile { get; init; }
    }

    private static readonly string[] KnownCompilers =
        { "gcc", "g++", "cc", "c++", "clang", "clang++", "cl" };

    private static readonly string[] SourceExtensions =
        { ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm", ".s", ".S", ".sx", ".asm" };

    // Toolchain tools that SHARE a compiler prefix but are NOT compilers (gcc-ar, arm-none-eabi-ld,
    // clang-tidy, clang-format). Without this the loose "starts with gcc/clang" match below flags them, and
    // one that happens to carry a source token (clang-tidy foo.c) would be mis-scraped as a compile command.
    private static readonly string[] NotCompilerTools =
        { "ar", "nm", "ranlib", "objcopy", "objdump", "size", "strip", "gcov", "gprof",
          "ld", "as", "gdb", "tidy", "format", "check", "cpp", "cov" };

    public static IReadOnlyList<CompileCommand> Parse(string log) => Parse(log, null);

    public static IReadOnlyList<CompileCommand> Parse(string log, ScrapeOptions? options)
    {
        options ??= new ScrapeOptions();
        if (string.IsNullOrEmpty(log)) return Array.Empty<CompileCommand>();
        if (LooksLikeCompileDb(log)) return ParseCompileDb(log, options);   // compile_commands.json

        var results = new List<CompileCommand>();
        // Track the working directory across lines the way the shell/make would: a standalone `cd DIR`, and
        // GNU make's `Entering directory '...'` / `Leaving directory '...'` (from `make -w`, emitted around
        // every recursive sub-build). Without this, a relative -I in those commands resolves against the wrong
        // base — real `make -w` logs use Entering/Leaving everywhere (eval-#10). A `cd DIR && gcc ...` on the
        // same line is still handled per-line by ExtractLeadingCd and overrides the tracked dir.
        // make -j runs sibling sub-makes at once: their Entering/Leaving lines interleave, so the directory entered
        // last is only a guess for the next command. Every directory entered and not yet left is kept; a Leaving line
        // removes the directory it names (not just the latest), and the others still open become alternatives.
        var baseDir = ".";
        var open = new List<string>();
        foreach (var line in JoinContinuations(log))
        {
            var em = EnteringDir.Match(line);
            if (em.Success) { open.Add(em.Groups[1].Value.Trim()); continue; }
            if (line.Contains("Leaving directory", StringComparison.Ordinal))
            {
                var lm = LeavingDir.Match(line);
                var at = lm.Success ? open.LastIndexOf(lm.Groups[1].Value.Trim()) : -1;
                if (at < 0) at = open.Count - 1;
                if (at >= 0) open.RemoveAt(at);
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0) continue;
            var currentDir = open.Count > 0 ? open[^1] : baseDir;
            var alternatives = open.Count > 1 ? open.Take(open.Count - 1).Distinct().Where(d => d != currentDir).ToList() : new List<string>();

            // A line holding nothing but `cd DIR` updates the tracked directory.
            var simple = SplitCommands(tokens);
            if (simple.Count == 1 && simple[0].Count >= 2 && simple[0][0] == "cd")
            {
                if (open.Count > 0) open[^1] = CombineDir(currentDir, simple[0][1]);
                else baseDir = CombineDir(baseDir, simple[0][1]);
                continue;
            }
            Commands(simple, currentDir, alternatives, depth: 0);
        }
        return results;

        // One log line can hold several shell commands (`gcc a.c && gcc -DFOO b.c`, `cd x; cc ...`): each is its own
        // compile with its own flags (review BL1). A `cd DIR` carries forward to the commands after it on the line.
        void Commands(List<List<string>> simple, string dir, List<string> alternatives, int depth)
        {
            foreach (var cmd in simple)
            {
                if (cmd.Count == 0) continue;
                if (cmd[0] == "cd") { if (cmd.Count >= 2) { dir = CombineDir(dir, cmd[1]); alternatives = new(); } continue; }
                var ci = DriverIndex(cmd, options, allowGeneric: true);
                if (ci < 0)
                {
                    // `sh -c '<commands>'`: the compile is one quoted word. `/bin/sh ../libtool --mode=compile gcc ...`:
                    // a launcher whose arguments are the compile.
                    var launcher = DriverName(cmd[0]);
                    if (!Launchers.Contains(launcher, StringComparer.OrdinalIgnoreCase)) continue;
                    if (Shells.Contains(launcher, StringComparer.OrdinalIgnoreCase))
                    {
                        // The shell's own options come first; -c (alone or bundled, -ec) takes the command string.
                        // `busybox sh -c`, `bash --norc -c`, `bash -o pipefail -c`.
                        var o = launcher.Equals("busybox", StringComparison.OrdinalIgnoreCase) && cmd.Count > 1
                                && Shells.Contains(DriverName(cmd[1]), StringComparer.OrdinalIgnoreCase) ? 2 : 1;
                        while (o < cmd.Count && cmd[o].Length > 1 && cmd[o][0] is '-' or '+')
                        {
                            if (cmd[o] is "-o" or "+o" or "-O" or "+O") { o += 2; continue; }   // an option with an argument
                            if (cmd[o].StartsWith("--", StringComparison.Ordinal) || cmd[o][0] == '+' || !cmd[o][1..].Contains('c')) { o++; continue; }
                            break;
                        }
                        if (o + 1 < cmd.Count && cmd[o].Length > 1 && cmd[o][0] == '-' && cmd[o][1] != '-')
                        {
                            if (depth < 4) Commands(SplitCommands(Tokenize(cmd[o + 1])), dir, alternatives, depth + 1);
                            continue;
                        }
                    }
                    ci = cmd.FindIndex(1, a => IsCompiler(a) || options.CompilerNames.Any(n => DriverName(a).Equals(DriverName(n), StringComparison.OrdinalIgnoreCase)));
                    if (ci < 0 || !cmd.Skip(ci + 1).Any(IsSourceFile)) continue;
                }
                AddCommand(results, cmd[ci], cmd.GetRange(ci + 1, cmd.Count - ci - 1), dir, file: null, options, alternatives);
            }
        }
    }

    // Programs that run a compile given as their arguments, or as a -c string (the shells).
    static readonly string[] Shells = { "sh", "bash", "dash", "ksh", "zsh", "ash", "busybox" };
    static readonly string[] Launchers = Shells.Concat(new[] { "libtool", "glibtool", "slibtool", "jlibtool" }).ToArray();

    static void AddCommand(List<CompileCommand> results, string driver, List<string> args, string dir, string? file,
                           ScrapeOptions options, IReadOnlyList<string>? alternatives = null)
    {
        var incomplete = false;
        args = ExpandResponseFiles(args, dir, options, ref incomplete, depth: 0);
        var sources = file is not null ? new List<string> { file } : args.Where(IsSourceFile).ToList();
        if (sources.Count == 0) return; // link-only or non-compile invocation
        var (defines, includes, forced) = ExtractFlags(args, IsMsvcDriver(driver));
        foreach (var src in sources)
            results.Add(new CompileCommand
            {
                File = src,
                Directory = dir,
                Driver = driver,
                Arguments = args,
                Defines = defines,
                Includes = includes,
                ForcedIncludes = forced,
                Incomplete = incomplete,
                AlternativeDirectories = alternatives ?? Array.Empty<string>(),
            });
    }

    /// <summary>Splice <c>@file</c> response files into the argument list (recursively, depth-capped). An
    /// unreadable one marks the command incomplete (review BL2).</summary>
    static List<string> ExpandResponseFiles(List<string> args, string dir, ScrapeOptions options, ref bool incomplete, int depth)
    {
        if (!args.Any(a => a.Length > 1 && a[0] == '@')) return args;
        var r = new List<string>(args.Count);
        foreach (var a in args)
        {
            if (a.Length < 2 || a[0] != '@') { r.Add(a); continue; }
            var text = depth < 8 ? options.ReadResponseFile?.Invoke(dir, a[1..]) : null;
            if (text is null) { incomplete = true; continue; }
            r.AddRange(ExpandResponseFiles(Tokenize(text.Replace('\n', ' ').Replace('\r', ' ')).Where(t => !IsOp(t)).ToList(),
                                           dir, options, ref incomplete, depth + 1));
        }
        return r;
    }

    // Shell control operators, kept as single tokens by Tokenize when unquoted.
    const char OpMark = '\u0001';
    static bool IsOp(string t) => t.Length > 0 && t[0] == OpMark;

    static List<List<string>> SplitCommands(List<string> tokens)
    {
        var r = new List<List<string>> { new() };
        foreach (var t in tokens)
        {
            if (IsOp(t)) { r.Add(new List<string>()); continue; }
            r[^1].Add(t);
        }
        r.RemoveAll(c => c.Count == 0);
        return r;
    }

    // Programs that run another command (their own options/arguments come first).
    static readonly string[] Wrappers = { "ccache", "sccache", "distcc", "icecc", "buildcache", "time", "nice", "nohup",
                                          "env", "stdbuf", "chrt", "taskset", "xcrun" };
    // First words that are never a compile, even with a source name and -c/-D after them.
    static readonly string[] NeverCompiler = { "echo", "printf", "cp", "mv", "rm", "ln", "cat", "sed", "awk", "grep",
        "python", "python3", "perl", "sh", "bash", "dash", "ksh", "zsh", "ash", "busybox", "make", "gmake", "cmake", "ninja", "ar", "ld", "mkdir", "touch",
        "test", "install", "git", "tar", "zip", "objcopy", "strip", "doxygen", "clang-tidy", "clang-format" };

    /// <summary>Index of the compiler driver in one simple command, or -1. The driver is the first word after
    /// environment assignments, a ninja progress tag and known wrappers (ccache, distcc, ...). A recognised
    /// compiler name, or a configured one, qualifies; with <paramref name="allowGeneric"/> any other program
    /// qualifies when the command names a source file and has -c or a -D/-I flag (vendor compilers: armcc,
    /// iccarm, cl2000, ... — review BL3).</summary>
    static int DriverIndex(List<string> cmd, ScrapeOptions options, bool allowGeneric)
    {
        var i = 0;
        while (i < cmd.Count)
        {
            var t = cmd[i];
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\[\d+/\d+\]$")) { i++; continue; }        // ninja [3/10]
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z_][A-Za-z0-9_]*=")) { i++; continue; } // VAR=x
            var name = DriverName(t);
            if (Wrappers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                i++;
                while (i < cmd.Count && cmd[i].StartsWith('-')) i++;   // the wrapper's own options
                continue;
            }
            break;
        }
        if (i >= cmd.Count) return -1;
        var driver = DriverName(cmd[i]);
        if (IsCompiler(cmd[i]) || options.CompilerNames.Any(n => driver.Equals(DriverName(n), StringComparison.OrdinalIgnoreCase)))
            return i;
        if (!allowGeneric || NeverCompiler.Contains(driver, StringComparer.OrdinalIgnoreCase) || cmd[i].StartsWith('-')) return -1;
        var rest = cmd.Skip(i + 1).ToList();
        var looksLikeCompile = rest.Any(IsSourceFile)
            && rest.Any(a => a is "-c" or "/c" || a.StartsWith("-D", StringComparison.Ordinal) || a.StartsWith("-I", StringComparison.Ordinal));
        return looksLikeCompile ? i : -1;
    }

    static string DriverName(string token)
    {
        var name = FileNameOf(token);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    static bool IsMsvcDriver(string token) => DriverName(token).ToLowerInvariant() is "cl" or "clang-cl" or "icl" or "icx-cl";

    private static readonly System.Text.RegularExpressions.Regex EnteringDir =
        new(@"Entering directory\s+[`'""]?(.+?)[`'""]?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LeavingDir =
        new(@"Leaving directory\s+[`'""]?(.+?)[`'""]?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Combine a tracked base dir with a `cd`/entering target. An absolute target replaces it; a
    /// relative one is appended (kept as a path STRING — the CLI resolves the final Directory against the
    /// carve root, so we must not GetFullPath against this process's cwd here).</summary>
    private static string CombineDir(string baseDir, string target)
    {
        if (string.IsNullOrEmpty(target) || target == ".") return baseDir;
        if (Path.IsPathFullyQualified(target)) return target;
        return baseDir == "." ? target : baseDir.TrimEnd('/', '\\') + "/" + target;
    }

    /// <summary>First non-whitespace char is <c>[</c> — a JSON array, i.e. a compile_commands.json (a text
    /// build log never starts that way). BOM-tolerant.</summary>
    private static bool LooksLikeCompileDb(string text)
    {
        // "[" then "{" or "]" — a ninja progress tag "[3/10] gcc ..." also starts with "[".
        var seenBracket = false;
        foreach (var ch in text)
        {
            if (ch == '﻿' || char.IsWhiteSpace(ch)) continue; // skip BOM + leading whitespace
            if (!seenBracket) { if (ch != '[') return false; seenBracket = true; continue; }
            return ch is '{' or ']';
        }
        return false;
    }

    /// <summary>
    /// Parse a compile_commands.json. Each entry may carry <c>command</c> (a full command string, tokenized
    /// like a log line) or <c>arguments</c> (an argv array); the <c>file</c> field, when present, is the
    /// authoritative translation unit. Same <c>-D</c>/<c>-I</c>/<c>-isystem</c> extraction as the text path.
    /// Tolerant: malformed JSON, a non-array root, or an odd entry is skipped, never fatal.
    /// </summary>
    private static IReadOnlyList<CompileCommand> ParseCompileDb(string json, ScrapeOptions options)
    {
        var results = new List<CompileCommand>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return results; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var dir = GetString(entry, "directory") ?? ".";
                var file = GetString(entry, "file");

                List<string> tokens;
                if (entry.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    tokens = argsEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                                   .Select(x => x.GetString()!).ToList();
                else
                {
                    var cmd = GetString(entry, "command");
                    if (cmd is null) continue;   // entry with neither command nor arguments — skip
                    tokens = Tokenize(cmd);
                }

                // Drop the driver token (and any wrapper before it) like the text path; if none is
                // recognised, keep all tokens so an explicit "file" entry still yields its flags. A DB entry is
                // one command, so the first word is the driver even if its name is not one we know.
                tokens = tokens.Where(t => !IsOp(t)).ToList();
                var ci = DriverIndex(tokens, options, allowGeneric: false);
                if (ci < 0 && tokens.Count > 0 && !tokens[0].StartsWith('-')) ci = 0;
                var driver = ci >= 0 ? tokens[ci] : "";
                var args = ci >= 0 ? tokens.GetRange(ci + 1, tokens.Count - ci - 1) : tokens;
                AddCommand(results, driver, args, dir, file, options);
            }
        }
        return results;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (IReadOnlyList<string> Defines, IReadOnlyList<string> Includes, IReadOnlyList<string> Forced)
        ExtractFlags(List<string> args, bool msvc)
    {
        // Effective defines with LAST-WINS semantics: the compiler applies -D/-U left to right, so
        // `-DFEATURE -UFEATURE` leaves FEATURE UNDEFINED (and `-UX -DX=1` leaves it defined). Model it with a
        // name-keyed map: -D sets, -U removes; the map holds the net state. Ignoring -U (the old behavior)
        // wrongly kept the #ifdef branch of a macro the build explicitly undefined (eval-#9).
        var eff = new Dictionary<string, string?>(StringComparer.Ordinal);
        var includes = new List<string>();
        var forced = new List<string>();
        void Define(string spec)
        {
            if (spec.Length == 0) return;
            var eq = spec.IndexOf('=');
            if (eq < 0) eff[spec] = null; else eff[spec[..eq]] = spec[(eq + 1)..];
        }
        void Undef(string name) { if (name.Length > 0) eff.Remove(name); }
        // MSVC-style /D /U /I only for an MSVC driver: for gcc, /Users/x/a.c or /Include/... are paths (BL3).
        bool Flag(string a, char f) => a.Length >= 2 && (a[0] == '-' || (msvc && a[0] == '/')) && a[1] == f;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            // Preprocessor flags passed through the driver: -Wp,-DFOO,-UBAR and -Xpreprocessor -DFOO.
            if (a.StartsWith("-Wp,", StringComparison.Ordinal))
            {
                foreach (var w in a[4..].Split(','))
                    if (w.StartsWith("-D", StringComparison.Ordinal)) Define(w[2..]);
                    else if (w.StartsWith("-U", StringComparison.Ordinal)) Undef(w[2..]);
                continue;
            }
            if (a == "-Xpreprocessor" && i + 1 < args.Count)
            {
                var w = args[++i];
                if (w.StartsWith("-D", StringComparison.Ordinal)) Define(w[2..]);
                else if (w.StartsWith("-U", StringComparison.Ordinal)) Undef(w[2..]);
                continue;
            }
            if (a == "--define-macro" && i + 1 < args.Count) { Define(args[++i]); continue; }
            if (a.StartsWith("--define-macro=", StringComparison.Ordinal)) { Define(a[15..]); continue; }
            if (a == "--undefine-macro" && i + 1 < args.Count) { Undef(args[++i]); continue; }
            // Forced includes: their macros are part of the TU's configuration (review BL2).
            if (a is "-include" or "-imacros" && i + 1 < args.Count) { forced.Add(args[++i]); continue; }
            if (msvc && (a is "/FI" or "-FI") && i + 1 < args.Count) { forced.Add(args[++i]); continue; }
            if (msvc && (a.StartsWith("/FI", StringComparison.Ordinal) || a.StartsWith("-FI", StringComparison.Ordinal)) && a.Length > 3)
            { forced.Add(a[3..]); continue; }
            if (a is "-include" or "-imacros") continue;

            if (a.Length == 2 && Flag(a, 'D')) { if (i + 1 < args.Count) Define(args[++i]); }
            else if (Flag(a, 'D')) Define(a[2..]);
            else if (a.Length == 2 && Flag(a, 'U')) { if (i + 1 < args.Count) Undef(args[++i]); }
            else if (Flag(a, 'U')) Undef(a[2..]);
            // `-I-` splits the quote and angle search lists (old GCC): not a directory.
            else if (a == "-I-") continue;
            else if (a.Length == 2 && Flag(a, 'I')) { if (i + 1 < args.Count) includes.Add(SysrootRelative(args[++i])); }
            // -isystem / -iquote / -idirafter DIR: the other GCC/Clang include-search forms, used heavily by
            // embedded builds for toolchain / CMSIS / HAL headers. They take the dir as the NEXT token. Feeding
            // these to include resolution matters -- a non-sibling .inc reached via -isystem otherwise falls to
            // the (over-approximate, warning) basename fallback.
            else if (a is "-isystem" or "-iquote" or "-idirafter" or "-isystem-after" or "--include-directory" or "--include-directory-after"
                       or "--include_path" or "--sys_include"
                     || (msvc && a is "/external:I" or "-external:I"))
            { if (i + 1 < args.Count) includes.Add(SysrootRelative(args[++i])); }
            else if (JoinedIncludeDir(a, msvc) is { } joined) includes.Add(SysrootRelative(joined));
            else if (Flag(a, 'I')) includes.Add(SysrootRelative(a[2..]));
        }
        var defines = eff.Select(kv => kv.Value is null ? kv.Key : $"{kv.Key}={kv.Value}").ToList();
        return (defines, includes, forced);
    }

    /// <summary>The dir of an include flag written joined: <c>-isystemDIR</c>, <c>-iquoteDIR</c>, <c>-idirafterDIR</c>,
    /// <c>--include-directory=DIR</c>, other vendors' <c>--include_path=DIR</c> / <c>--sys_include=DIR</c>, and MSVC's
    /// <c>/external:IDIR</c>. Longer prefixes first: <c>-isystem-afterDIR</c> is not <c>-isystem</c> of "-afterDIR".</summary>
    private static string? JoinedIncludeDir(string a, bool msvc)
    {
        foreach (var p in JoinedIncludePrefixes)
            if (a.Length > p.Length && a.StartsWith(p, StringComparison.Ordinal)) return a[p.Length..];
        if (msvc)
            foreach (var p in MsvcJoinedIncludePrefixes)
                if (a.Length > p.Length && a.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return a[p.Length..];
        return null;
    }

    private static readonly string[] JoinedIncludePrefixes =
        { "--include-directory-after=", "--include-directory=", "--include_path=", "--sys_include=",
          "-isystem-after", "-isystem", "-iquote", "-idirafter" };
    private static readonly string[] MsvcJoinedIncludePrefixes = { "/external:I", "-external:I" };

    /// <summary>GCC's <c>-I=DIR</c> / <c>-isystem=DIR</c>: DIR under the sysroot. Without one that is DIR itself.</summary>
    private static string SysrootRelative(string dir) => dir.Length > 1 && dir[0] == '=' ? dir[1..] : dir;

    private static bool IsCompiler(string token)
    {
        if (token.StartsWith('-')) return false; // not '/': Unix compiler paths (/usr/bin/gcc) start with it
        var name = FileNameOf(token);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        // Exclude sibling toolchain tools (gcc-ar, clang-tidy, arm-none-eabi-ld) before the loose prefix match.
        foreach (var t in NotCompilerTools)
            if (name.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-" + t, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (var c in KnownCompilers)
            if (name.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-" + c, StringComparison.OrdinalIgnoreCase)) // arm-none-eabi-gcc, x86_64-w64-mingw32-g++
                return true;

        // Versioned GNU/LLVM drivers: gcc-12, clang-15, g++-11.
        return name.StartsWith("gcc", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("g++", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("clang", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceFile(string token)
    {
        if (token.StartsWith('-')) return false; // MSVC flags like /c won't match a source extension anyway
        foreach (var ext in SourceExtensions)
            if (token.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>Split into logical lines, joining trailing-backslash continuations.</summary>
    private static IEnumerable<string> JoinContinuations(string log)
    {
        var pending = "";
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.EndsWith('\\'))
            {
                pending += line[..^1] + " ";
                continue;
            }
            yield return pending + line;
            pending = "";
        }
        if (pending.Length > 0) yield return pending;
    }

    /// <summary>Whitespace tokenizer honouring single/double quotes, with backslash-escaped quotes. A
    /// compile_commands.json <c>command</c> string carries shell escaping, so <c>-DVER=\"1.0\"</c> must keep
    /// its quotes and <c>-I\"path\"</c> must NOT become a literal-backslash dir (eval-#9). Only <c>\"</c> and
    /// <c>\'</c> are treated as escapes; a bare <c>\</c> (Windows path separators, which live inside quotes
    /// here) is kept literally so <c>C:\foo</c> is unharmed.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            // Shell-escaped quote (\" or \'): consume the backslash and treat the quote as a NORMAL quote —
            // it groups (so -I\"inc dir\" is one dir) and is stripped (so -DVER=\"1.0\" -> VER=1.0, and
            // -I\"path\" -> path, not a literal-backslash/quote dir). Only quotes are unescaped; a bare
            // backslash (Windows separators, inside quotes here) is left literal so C:\foo survives.
            if (ch == '\\' && i + 1 < line.Length && (line[i + 1] is '"' or '\'')) ch = line[++i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
            }
            else if (ch is ';' or '|' || (ch == '&' && i + 1 < line.Length && line[i + 1] == '&'))
            {
                // Unquoted shell control operator (;, |, ||, &&), even when attached to a word (dir&&gcc).
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                if (i + 1 < line.Length && line[i + 1] == ch) i++;
                tokens.Add(OpMark + ch.ToString());
            }
            else
            {
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
