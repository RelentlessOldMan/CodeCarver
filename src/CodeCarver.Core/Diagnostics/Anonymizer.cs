using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Diagnostics;

/// <summary>
/// Rewrites C/C++ source, paths and build flags so they keep the SHAPE the carve depends on and none of the names.
/// One instance holds one consistent mapping, so a bundle of files anonymized together still includes, calls and
/// pastes the same way:
/// <list type="bullet">
/// <item>Every word of an identifier (split at underscores and case changes) becomes an opaque word of the same case
///   class, the same everywhere: <c>uart_init</c> is <c>k1_k2</c>, <c>uart_send</c> is <c>k1_k3</c>, so a name pasted
///   from <c>uart_ ## init</c> still matches. Language keywords and compiler, library and attribute names stay.</item>
/// <item>Comments become spaces. String contents are rewritten the same way (a string can name a function:
///   <c>dlsym(h, "name")</c>); a string that looks like a file name is mapped as a path.</item>
/// <item>Numbers above 16 become ordered stand-ins (17, 18, ...), so <c>#if VERSION &gt;= 300</c> compares the same.</item>
/// <item>Path segments become <c>p1</c>, <c>p2</c>, ... (extension kept), case-insensitively, the same in every
///   path and every <c>#include</c>.</item>
/// </list>
/// Lines and line breaks are kept, so a line number in the original is the same line in the result.
/// Call <see cref="Learn"/> on every text first: generated words never collide with an original word, and the number
/// order is computed over everything learned. <see cref="Leaks"/> is the guarantee: it lists any original word that
/// survived.
/// </summary>
public sealed class Anonymizer
{
    // Kept as-is: language, compiler, preprocessor, attribute and standard library names. Nothing project-specific.
    static readonly HashSet<string> Keep = new(StringComparer.Ordinal)
    {
        // C / C++ keywords
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float",
        "for", "goto", "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed", "sizeof", "static",
        "struct", "switch", "typedef", "union", "unsigned", "void", "volatile", "while", "_Alignas", "_Alignof", "_Atomic",
        "_Bool", "_Complex", "_Generic", "_Imaginary", "_Noreturn", "_Static_assert", "_Thread_local", "_Pragma", "alignas",
        "alignof", "and", "and_eq", "asm", "bitand", "bitor", "bool", "catch", "char8_t", "char16_t", "char32_t", "class",
        "compl", "concept", "consteval", "constexpr", "constinit", "const_cast", "co_await", "co_return", "co_yield",
        "decltype", "delete", "dynamic_cast", "explicit", "export", "false", "friend", "mutable", "namespace", "new",
        "noexcept", "not", "not_eq", "nullptr", "operator", "or", "or_eq", "private", "protected", "public",
        "reinterpret_cast", "requires", "static_assert", "static_cast", "template", "this", "thread_local", "throw", "true",
        "try", "typeid", "typename", "using", "virtual", "wchar_t", "xor", "xor_eq", "override", "final", "import", "module",
        // preprocessor
        "define", "undef", "include", "include_next", "ifdef", "ifndef", "elif", "elifdef", "elifndef", "endif", "line",
        "error", "warning", "pragma", "ident", "sccs", "defined", "__has_include", "__has_include_next", "__has_attribute",
        "__has_builtin", "__has_feature", "__has_cpp_attribute", "__VA_ARGS__", "__VA_OPT__", "__FILE__", "__LINE__",
        "__DATE__", "__TIME__", "__COUNTER__", "__func__", "__FUNCTION__", "__PRETTY_FUNCTION__", "__STDC__",
        "__STDC_VERSION__", "__STDC_HOSTED__", "__cplusplus", "__GNUC__", "__GNUC_MINOR__", "__clang__", "_MSC_VER",
        "__ASSEMBLER__", "__OPTIMIZE__", "NDEBUG", "_WIN32", "_WIN64", "__linux__", "__unix__", "__APPLE__", "__arm__",
        "__aarch64__", "__x86_64__", "__i386__", "__thumb__", "__ARM_ARCH", "__BYTE_ORDER__", "__ORDER_LITTLE_ENDIAN__",
        "once", "pack", "push", "pop", "GCC", "diagnostic", "ignored", "system_header", "optimize", "message", "region",
        "endregion", "STDC", "FP_CONTRACT", "ON", "OFF",
        // compiler extensions and attributes
        "__attribute__", "__attribute", "__asm__", "__asm", "__inline", "__inline__", "__forceinline", "__volatile__",
        "__const", "__const__", "__restrict", "__restrict__", "__extension__", "__typeof__", "__typeof", "typeof",
        "__alignof__", "__thread", "__declspec", "__cdecl", "__stdcall", "__fastcall", "__thiscall", "__vectorcall",
        "__clrcall", "__ptr32", "__ptr64", "__unaligned", "_unaligned", "__based", "__int8", "__int16", "__int32",
        "__int64", "__int128", "__signed__", "__label__", "__real__", "__imag__", "__auto_type", "noreturn",
        "weak", "weakref", "alias", "ifunc", "section", "used", "unused", "constructor", "destructor", "noinline",
        "always_inline", "gnu_inline", "visibility", "hidden", "packed", "aligned", "naked", "interrupt", "isr",
        "deprecated", "format", "printf", "scanf", "nonnull", "pure", "cold", "hot", "externally_visible", "cleanup",
        "retain", "nothrow", "leaf", "malloc", "may_alias", "mode", "transparent_union", "vector_size", "fallthrough",
        "nodiscard", "maybe_unused", "likely", "unlikely", "no_instrument_function", "no_reorder", "selectany",
        "dllexport", "dllimport", "symver", "target", "target_clones", "returns_twice", "warn_unused_result",
        "redefine_extname", "WINAPI", "CALLBACK", "APIENTRY",
        // link flags and linker scripts
        "Wl", "Xlinker", "defsym", "undefined", "require", "entry", "wrap", "ENTRY", "EXTERN", "PROVIDE", "PROVIDE_HIDDEN",
        "KEEP", "SECTIONS", "MEMORY", "INCLUDE", "INPUT", "GROUP", "OUTPUT", "OUTPUT_FORMAT", "OUTPUT_ARCH", "ASSERT",
        "ADDR", "ALIGN", "ABSOLUTE", "DEFINED", "LOADADDR", "ORIGIN", "LENGTH", "SIZEOF", "SIZEOF_HEADERS", "MAX", "MIN",
        "NEXT", "LOG2CEIL", "CONSTANT", "MAXPAGESIZE", "COMMONPAGESIZE", "SEGMENT_START", "BLOCK", "ALIGNOF",
        // standard types, constants and the library functions the carve treats specially
        "size_t", "ssize_t", "ptrdiff_t", "intptr_t", "uintptr_t", "intmax_t", "uintmax_t", "int8_t", "int16_t", "int32_t",
        "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t", "int_least8_t", "int_fast8_t", "off_t", "time_t",
        "va_list", "va_start", "va_arg", "va_end", "va_copy", "__builtin_va_list", "FILE", "NULL", "EOF", "stdin", "stdout",
        "stderr", "main", "wmain", "WinMain", "DllMain", "exit", "abort", "atexit", "free", "calloc", "realloc", "memcpy",
        "memmove", "memset", "memcmp", "strlen", "strcmp", "strncmp", "strcpy", "strncpy", "strcat", "strchr", "strstr",
        "sprintf", "snprintf", "fprintf", "puts", "putchar", "getchar", "fopen", "fclose", "fread", "fwrite", "assert",
        "offsetof", "errno", "true", "false", "std", "dlsym", "dlvsym", "dlopen", "dlclose", "dlerror", "dlfunc",
        "GetProcAddress", "LoadLibrary", "LoadLibraryA", "LoadLibraryW", "RTLD_DEFAULT", "RTLD_NEXT", "RTLD_LAZY",
        "RTLD_NOW", "lt_dlsym", "g_module_symbol",
    };

    // Angle-bracket headers that are the C/C++/POSIX/Windows system's own: kept as-is.
    static readonly HashSet<string> SystemHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "assert.h", "complex.h", "ctype.h", "errno.h", "fenv.h", "float.h", "inttypes.h", "iso646.h", "limits.h",
        "locale.h", "math.h", "setjmp.h", "signal.h", "stdalign.h", "stdarg.h", "stdatomic.h", "stdbool.h", "stddef.h",
        "stdint.h", "stdio.h", "stdlib.h", "stdnoreturn.h", "string.h", "tgmath.h", "threads.h", "time.h", "uchar.h",
        "wchar.h", "wctype.h", "unistd.h", "dlfcn.h", "fcntl.h", "pthread.h", "windows.h", "malloc.h", "memory.h",
        "strings.h", "alloca.h", "sys/types.h", "sys/stat.h", "sys/time.h", "sys/mman.h", "sys/ioctl.h",
        "iostream", "vector", "string", "map", "set", "memory", "cstdio", "cstdint", "cstring", "cstdlib", "cstddef",
        "cassert", "cmath", "climits", "algorithm", "functional", "utility", "array", "list", "deque", "unordered_map",
        "unordered_set", "tuple", "type_traits", "atomic", "mutex", "thread", "chrono", "iomanip", "sstream", "fstream",
        "new", "exception", "stdexcept", "initializer_list", "limits", "numeric", "optional", "variant", "cstdarg",
    };

    // The words of the system header names (stdio, sys, ...): an include of one keeps them.
    static readonly HashSet<string> SystemStems = new(SystemHeaders.SelectMany(h => h.Split('/', '.')), StringComparer.Ordinal);

    static readonly Regex ShortExtension = new(@"^\.[A-Za-z0-9+_]{1,5}$", RegexOptions.CultureInvariant);
    static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
    static readonly Regex PathLike = new(@"^[\w.\-/\\]+\.(?:h|hh|hpp|hxx|h\+\+|inc|inl|ipp|tcc|def|c|cc|cpp|cxx|c\+\+|s|S|asm|ld|lds|x|mk|cmm|txt)$",
                                         RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _segments = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _originalWords = new(StringComparer.Ordinal);
    readonly HashSet<string> _originalSegments = new(StringComparer.OrdinalIgnoreCase);
    readonly SortedSet<ulong> _numbers = new();
    Dictionary<ulong, int>? _numberRank;
    int _nextWord, _nextSegment;

    /// <summary>Records every word, path segment and number in <paramref name="text"/> (source, a path, a flag).</summary>
    public void Learn(string text)
    {
        foreach (Match m in Word.Matches(text))
        {
            _originalWords.Add(m.Value);
            foreach (var w in Words(m.Value)) _originalWords.Add(w);
        }
        foreach (var seg in text.Split('/', '\\', '"', '<', '>', ' ', '\t', '\n', '\r', '='))
            if (seg.Length > 0) _originalSegments.Add(Stem(seg));
        foreach (Match m in Regex.Matches(text, @"(?<![\w.])(?:0[xX][0-9A-Fa-f']+|0[bB][01']+|\d[\d']*)[uUlLzZ]*(?![\w.])"))
            if (IntValue(m.Value) is { } v && v > 16) { _numbers.Add(v); _numberRank = null; }
    }

    /// <summary>The words of the original input that are still in <paramref name="text"/> (should be none). Kept
    /// names (keywords, standard library) and one-letter names don't count.</summary>
    public IReadOnlyList<string> Leaks(string text)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Word.Matches(text))
        {
            var id = m.Value;
            if (Allowed(id)) continue;
            if (_originalWords.Contains(id)) { found.Add(id); continue; }
            foreach (var w in Words(id))
                if (!Allowed(w) && !IsDigits(w) && _originalWords.Contains(w)) found.Add(w);
        }
        return found.ToList();
    }

    // Words the output may hold: kept names, generated words, short words, system header names, the extensions kept on
    // file names (.c, .inc), and the __wrap_/__real_ prefixes.
    bool Allowed(string w) => Kept(w) || w.Length <= 2 || _generated.Contains(w) || SystemStems.Contains(w)
                              || _extensions.Contains(w) || w is "wrap" or "real";

    readonly HashSet<string> _generated = new(StringComparer.Ordinal);
    readonly HashSet<string> _extensions = new(StringComparer.Ordinal);

    /// <summary>Every original name or path segment and what it became, for the local (never shared) key.</summary>
    public IEnumerable<(string Original, string Anonymized)> Map() =>
        _words.Select(kv => (kv.Key, kv.Value)).Concat(_segments.Select(kv => (kv.Key + "  (path)", kv.Value)))
              .OrderBy(p => p.Item2, StringComparer.Ordinal);

    static bool IsDigits(string s) => s.All(char.IsAsciiDigit);
    static bool Kept(string id) => id.Length <= 1 || Keep.Contains(id) || id.StartsWith("__builtin_", StringComparison.Ordinal)
                                   || id.StartsWith("__sync_", StringComparison.Ordinal) || id.StartsWith("__atomic_", StringComparison.Ordinal);

    /// <summary>An identifier, word by word.</summary>
    public string Name(string id)
    {
        if (Kept(id)) return id;
        foreach (var prefix in new[] { "__wrap_", "__real_" })
            if (id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length) return prefix + Name(id[prefix.Length..]);
        var sb = new StringBuilder(id.Length + 4);
        var i = 0;
        while (i < id.Length)
        {
            if (id[i] == '_') { sb.Append('_'); i++; continue; }
            var start = i;
            i = WordEnd(id, i);
            var w = id[start..i];
            sb.Append(IsDigits(w) ? w : MapWord(w));
        }
        return sb.ToString();
    }

    // A word: letters and digits up to an underscore or a case change (lower->Upper, or UPPER->Upper+lower).
    static int WordEnd(string id, int i)
    {
        var j = i + 1;
        while (j < id.Length && id[j] != '_')
        {
            var p = id[j - 1];
            var c = id[j];
            if (char.IsAsciiLetterUpper(c) && (char.IsAsciiLetterLower(p) || char.IsAsciiDigit(p))) break;
            if (char.IsAsciiLetterUpper(c) && char.IsAsciiLetterUpper(p) && j + 1 < id.Length && char.IsAsciiLetterLower(id[j + 1])) break;
            j++;
        }
        return j;
    }

    static IEnumerable<string> Words(string id)
    {
        var i = 0;
        while (i < id.Length)
        {
            if (id[i] == '_') { i++; continue; }
            var s = i;
            i = WordEnd(id, i);
            yield return id[s..i];
        }
    }

    string MapWord(string w)
    {
        if (_words.TryGetValue(w, out var t)) return t;
        // Same case class: UPPER stays upper (macro-looking names stay macro-looking), Capitalized stays
        // capitalized, anything else becomes lower case.
        var upper = w.Any(char.IsAsciiLetterUpper) && !w.Any(char.IsAsciiLetterLower);
        var capital = !upper && char.IsAsciiLetterUpper(w[0]);
        do
        {
            _nextWord++;
            t = upper ? "K" + _nextWord : capital ? "Kq" + _nextWord : "k" + _nextWord;
        } while (_originalWords.Contains(t) || Keep.Contains(t));
        _generated.Add(t);
        return _words[w] = t;
    }

    static string Stem(string seg)
    {
        var dot = seg.LastIndexOf('.');
        return dot > 0 ? seg[..dot] : seg;
    }

    /// <summary>A relative or absolute path, segment by segment (the extension is kept). Separators are kept as given.</summary>
    public string Path(string path)
    {
        var sb = new StringBuilder(path.Length);
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] is not ('/' or '\\')) continue;
            sb.Append(Segment(path[start..i]));
            if (i < path.Length) sb.Append(path[i]);
            start = i + 1;
        }
        return sb.ToString();
    }

    string Segment(string seg)
    {
        if (seg.Length == 0 || seg is "." or "..") return seg;
        if (seg.Length == 2 && seg[1] == ':') return "C:";   // a drive letter
        // A short extension is kept (.c, .h, .cpp, .S); anything longer after a dot is part of the name.
        var dot = seg.LastIndexOf('.');
        if (dot > 0 && !ShortExtension.IsMatch(seg[dot..])) dot = -1;
        var stem = dot > 0 ? seg[..dot] : seg;
        var ext = dot > 0 ? seg[dot..] : "";
        if (ext.Length > 1) _extensions.Add(ext[1..]);
        if (!_segments.TryGetValue(stem, out var t))
        {
            do { _nextSegment++; t = "p" + _nextSegment; } while (_originalSegments.Contains(t) || _originalWords.Contains(t));
            _generated.Add(t);
            _segments[stem] = t;
        }
        return t + ext;
    }

    /// <summary>An include target: a system header stays, anything else is mapped as a path.</summary>
    public string Include(string target, bool angle) => angle && SystemHeaders.Contains(target.Replace('\\', '/')) ? target : Path(target);

    string Number(string tok)
    {
        if (IntValue(tok) is not { } v) return Regex.IsMatch(tok, @"^\d") ? "1.5" + Suffix(tok, true) : tok;
        if (v <= 16) return tok;
        if (_numberRank is null)
        {
            _numberRank = new Dictionary<ulong, int>();
            var r = 0;
            foreach (var n in _numbers) _numberRank[n] = ++r;
        }
        if (!_numberRank.TryGetValue(v, out var rank)) { _numbers.Add(v); _numberRank = null; return Number(tok); }
        return (16 + rank).ToString(CultureInfo.InvariantCulture) + Suffix(tok, false);
    }

    static string Suffix(string tok, bool @float)
    {
        var i = tok.Length;
        while (i > 0 && (@float ? tok[i - 1] is 'f' or 'F' or 'l' or 'L' : tok[i - 1] is 'u' or 'U' or 'l' or 'L' or 'z' or 'Z')) i--;
        return tok[i..];
    }

    static ulong? IntValue(string tok)
    {
        var s = tok.Replace("'", "", StringComparison.Ordinal).TrimEnd('u', 'U', 'l', 'L', 'z', 'Z');
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return s.Length > 2 && s[2..].All(c => c is '0' or '1') ? Convert.ToUInt64(s[2..], 2) : null;
            if (s.Length > 1 && s[0] == '0') return s.All(c => c is >= '0' and <= '7') ? Convert.ToUInt64(s, 8) : null;
            return s.Length > 0 && s.All(char.IsAsciiDigit) && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch (OverflowException) { return null; }
    }

    /// <summary>A build flag value or other free text: identifiers and numbers mapped, the rest kept.</summary>
    public string Text(string text) => Code(text, blankComments: false);

    /// <summary>C/C++ source: comments blanked, identifiers, strings, numbers and include targets mapped.</summary>
    public string Code(string text) => Code(text, blankComments: true);

    string Code(string text, bool blankComments)
    {
        var sb = new StringBuilder(text.Length + text.Length / 8);
        var i = 0;
        var lineStart = true;
        var inDirective = false;
        var expectInclude = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                // A directive continues past a backslash-newline.
                var k = sb.Length - 1;
                if (k >= 0 && sb[k] == '\r') k--;
                if (!(k >= 0 && sb[k] == '\\')) { inDirective = false; expectInclude = false; }
                sb.Append(c); i++; lineStart = true; continue;
            }
            if (c is ' ' or '\t' or '\r' or '\f' or '\v') { sb.Append(c); i++; continue; }
            if (lineStart && c == '#') { inDirective = true; lineStart = false; sb.Append(c); i++; continue; }
            lineStart = false;
            if (blankComments && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    // A backslash-newline continues a // comment.
                    if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
                    { sb.Append(' '); i++; if (text[i] == '\r') { sb.Append('\r'); i++; } sb.Append('\n'); i++; continue; }
                    sb.Append(text[i] == '\r' ? '\r' : ' ');
                    i++;
                }
                continue;
            }
            if (blankComments && c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] is '\n' or '\r' ? text[i] : ' ');
                    i++;
                }
                if (i < text.Length) { sb.Append("  "); i += 2; }
                continue;
            }
            if (expectInclude && (c == '"' || c == '<'))
            {
                var close = c == '"' ? '"' : '>';
                var end = text.IndexOf(close, i + 1);
                var nl = text.IndexOf('\n', i + 1);
                if (end > i && (nl < 0 || end < nl))
                {
                    sb.Append(c).Append(Include(text[(i + 1)..end], angle: c == '<')).Append(close);
                    i = end + 1;
                    expectInclude = false;
                    continue;
                }
            }
            if (c is '"' or '\'')
            {
                i = Literal(text, i, sb, c);
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var s = i;
                i++;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or '\''
                       || (text[i] is '+' or '-' && text[i - 1] is 'e' or 'E' or 'p' or 'P' && !text[s..i].StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
                    i++;
                sb.Append(Number(text[s..i]));
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_' || c > 0x7F)
            {
                var s = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_' || text[i] > 0x7F)) i++;
                var id = text[s..i];
                // String and raw-string prefixes: L"..", u8"..", R"d(..)d".
                if (i < text.Length && text[i] == '"' && id is "L" or "u" or "U" or "u8" or "R" or "LR" or "uR" or "UR" or "u8R")
                {
                    sb.Append(id);
                    i = id.EndsWith('R') ? RawLiteral(text, i, sb) : Literal(text, i, sb, '"');
                    continue;
                }
                if (id.Any(ch => ch > 0x7F)) id = new string(id.Select(ch => ch > 0x7F ? 'u' : ch).ToArray()) + "_nonascii";
                sb.Append(Name(id));
                if (inDirective && id is "include" or "include_next" or "import") expectInclude = true;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // A "..." or '...' literal from its opening quote; returns the index after it. Contents are rewritten word by word
    // (a string can name a function); a string that looks like a file name is mapped as a path.
    int Literal(string text, int i, StringBuilder sb, char q)
    {
        var s = i + 1;
        var j = s;
        while (j < text.Length && text[j] != q && text[j] != '\n')
        {
            if (text[j] == '\\' && j + 1 < text.Length) j++;
            j++;
        }
        var body = text[s..Math.Min(j, text.Length)];
        sb.Append(q);
        if (q == '"' && PathLike.IsMatch(body)) sb.Append(Path(body));
        else if (q == '\'' && body.Length <= 2) sb.Append(body);
        else if (q == '\'') sb.Append(new string(body.Select(ch => char.IsAsciiLetterOrDigit(ch) ? 'x' : ch).ToArray()));
        else sb.Append(StringBody(body));
        if (j < text.Length && text[j] == q) { sb.Append(q); j++; }
        return j;
    }

    int RawLiteral(string text, int i, StringBuilder sb)
    {
        var open = text.IndexOf('(', i + 1);
        if (open < 0 || open - i - 1 > 16) return Literal(text, i, sb, '"');
        var delim = text[(i + 1)..open];
        var closeTok = ")" + delim + "\"";
        var end = text.IndexOf(closeTok, open + 1, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        sb.Append('"').Append(delim).Append('(').Append(StringBody(text[(open + 1)..end]));
        if (end < text.Length) sb.Append(closeTok);
        return Math.Min(text.Length, end + closeTok.Length);
    }

    // Inside a string: escapes kept, words mapped like identifiers, numbers mapped, everything else kept.
    string StringBody(string body)
    {
        var sb = new StringBuilder(body.Length);
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                // \n, \t, \x41, \012: the letter after the backslash is an escape, not a word.
                sb.Append(c).Append(body[i + 1]);
                i += 2;
                if (body[i - 1] is 'x' or 'X') while (i < body.Length && char.IsAsciiHexDigit(body[i])) sb.Append(body[i++]);
                continue;
            }
            if (c == '%' && i + 1 < body.Length)
            {
                // A printf conversion: %d, %08lx, %s.
                var j = i + 1;
                while (j < body.Length && (char.IsAsciiDigit(body[j]) || body[j] is '-' or '+' or ' ' or '#' or '.' or '*' or 'l' or 'h' or 'z' or 'j' or 't' or 'L')) j++;
                if (j < body.Length && char.IsAsciiLetter(body[j])) { sb.Append(body, i, j + 1 - i); i = j + 1; continue; }
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var s = i;
                while (i < body.Length && (char.IsAsciiLetterOrDigit(body[i]) || body[i] == '_')) i++;
                sb.Append(Name(body[s..i]));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var s = i;
                while (i < body.Length && (char.IsAsciiLetterOrDigit(body[i]) || body[i] == '_')) i++;
                sb.Append(Number(body[s..i]));
                continue;
            }
            sb.Append(c > 0x7F ? '?' : c);
            i++;
        }
        return sb.ToString();
    }
}
