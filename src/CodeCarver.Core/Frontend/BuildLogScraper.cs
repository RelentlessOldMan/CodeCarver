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
/// </summary>
public static class BuildLogScraper
{
    private static readonly string[] KnownCompilers =
        { "gcc", "g++", "cc", "c++", "clang", "clang++", "cl" };

    private static readonly string[] SourceExtensions =
        { ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm" };

    // Toolchain tools that SHARE a compiler prefix but are NOT compilers (gcc-ar, arm-none-eabi-ld,
    // clang-tidy, clang-format). Without this the loose "starts with gcc/clang" match below flags them, and
    // one that happens to carry a source token (clang-tidy foo.c) would be mis-scraped as a compile command.
    private static readonly string[] NotCompilerTools =
        { "ar", "nm", "ranlib", "objcopy", "objdump", "size", "strip", "gcov", "gprof",
          "ld", "as", "gdb", "tidy", "format", "check", "cpp", "cov" };

    public static IReadOnlyList<CompileCommand> Parse(string log)
    {
        var results = new List<CompileCommand>();
        foreach (var line in JoinContinuations(log))
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0) continue;

            var dir = ExtractLeadingCd(tokens, out var rest);
            var ci = IndexOfCompiler(rest);
            if (ci < 0) continue;

            var args = rest.GetRange(ci + 1, rest.Count - ci - 1);
            var sources = args.Where(IsSourceFile).ToList();
            if (sources.Count == 0) continue; // link-only or non-compile invocation

            var (defines, includes) = ExtractFlags(args);
            foreach (var src in sources)
                results.Add(new CompileCommand
                {
                    File = src,
                    Directory = dir,
                    Arguments = args,
                    Defines = defines,
                    Includes = includes,
                });
        }
        return results;
    }

    private static (IReadOnlyList<string> Defines, IReadOnlyList<string> Includes) ExtractFlags(List<string> args)
    {
        var defines = new List<string>();
        var includes = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-D" or "/D") { if (i + 1 < args.Count) defines.Add(args[++i]); }
            else if (a.StartsWith("-D", StringComparison.Ordinal) || a.StartsWith("/D", StringComparison.Ordinal))
                defines.Add(a[2..]);
            else if (a is "-I" or "/I") { if (i + 1 < args.Count) includes.Add(args[++i]); }
            // -isystem / -iquote / -idirafter DIR: the other GCC/Clang include-search forms, used heavily by
            // embedded builds for toolchain / CMSIS / HAL headers. They take the dir as the NEXT token. Feeding
            // these to include resolution matters -- a non-sibling .inc reached via -isystem otherwise falls to
            // the (over-approximate, warning) basename fallback.
            else if (a is "-isystem" or "-iquote" or "-idirafter") { if (i + 1 < args.Count) includes.Add(args[++i]); }
            else if (a.StartsWith("-I", StringComparison.Ordinal) || a.StartsWith("/I", StringComparison.Ordinal))
                includes.Add(a[2..]);
        }
        return (defines, includes);
    }

    /// <summary>Index of the compiler driver token in a command, or -1. Handles paths and cross/versioned
    /// names (arm-none-eabi-gcc, gcc-12, /usr/bin/clang++, cl.exe).</summary>
    private static int IndexOfCompiler(List<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
            if (IsCompiler(tokens[i]))
                return i;
        return -1;
    }

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

    /// <summary>If the command begins with <c>cd DIR &amp;&amp;</c> (or <c>cd DIR;</c>), return DIR and put the
    /// remaining tokens in <paramref name="rest"/>; otherwise return "." and the tokens unchanged.</summary>
    private static string ExtractLeadingCd(List<string> tokens, out List<string> rest)
    {
        if (tokens.Count >= 3 && tokens[0].Equals("cd", StringComparison.Ordinal))
        {
            var sep = tokens[2];
            if (sep is "&&" or ";")
            {
                rest = tokens.GetRange(3, tokens.Count - 3);
                return tokens[1];
            }
        }
        rest = tokens;
        return ".";
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

    /// <summary>Whitespace tokenizer honouring single/double quotes.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var ch in line)
        {
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
            else
            {
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
