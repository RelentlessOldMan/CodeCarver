namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The set of preprocessor macros in effect — seeded from a build's <c>-D</c> flags and updated by the
/// <c>#define</c>/<c>#undef</c> directives a file itself contains as it is scanned. This is what makes
/// <c>#ifdef</c> resolution deterministic: the config IS the answer to which branch is live.
///
/// Every name is in one of three states: DEFINED (a concrete value), UNKNOWN (might or might not be defined —
/// both branches stay live, even under closed-world), or ABSENT (closed-world treats it as undefined). A name
/// is UNKNOWN when it was marked so, and also — unless this table defines it or saw a certain <c>#undef</c> —
/// when it is reserved to the implementation (<c>__x</c>, <c>_X</c>: compiler built-ins that no probe matched
/// to this TU), is C++'s <c>true</c>/<c>false</c>, or is in the <see cref="Ambient"/> set (#defined somewhere
/// in the tree, e.g. a config header this file includes). Closed-world only ever resolves names the build told
/// us about and nothing in the tree can define (review PP1/PP2).
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, string> _macros;
    // Names that are explicitly UNKNOWN (neither definitely-defined nor definitely-undefined) — e.g. a macro
    // that varies across a file's compile commands. The scanner treats these as unknown even under
    // closed-world, so their #ifdef branches are all kept (sound). Distinct from "absent", which closed-world
    // treats as undefined.
    private readonly HashSet<string> _unknown;
    // Names a certain #undef removed: definitely undefined from here on, whatever the ambient set says.
    private readonly HashSet<string> _undefined;

    public MacroTable()
    {
        _macros = new Dictionary<string, string>(StringComparer.Ordinal);
        _unknown = new HashSet<string>(StringComparer.Ordinal);
        _undefined = new HashSet<string>(StringComparer.Ordinal);
    }

    private MacroTable(Dictionary<string, string> macros, HashSet<string> unknown, HashSet<string> undefined,
                       IReadOnlySet<string>? ambient)
    {
        _macros = macros;
        _unknown = unknown;
        _undefined = undefined;
        Ambient = ambient;
    }

    /// <summary>Macro names #defined or #undef'd anywhere in the scanned tree (shared, read-only). Such a name,
    /// when this table has no concrete definition for it, is UNKNOWN rather than absent: a header this file
    /// includes may define it.</summary>
    public IReadOnlySet<string>? Ambient { get; set; }

    /// <summary>Macro names some file in the tree <c>#undef</c>s (shared, read-only; a subset of <see cref="Ambient"/>).</summary>
    public IReadOnlySet<string>? UndefinedSomewhere { get; set; }

    /// <summary>
    /// An <c>#include</c> was just passed (review W2). The included file may <c>#undef</c> what this one defined, or
    /// define what it <c>#undef</c>'d: such a name is unknown from here on. Names no file undefines, or that no file
    /// defines, keep their value.
    /// </summary>
    public void AfterInclude()
    {
        if (_undefined.Count > 0 && Ambient is { Count: > 0 } ambient)
            _undefined.RemoveWhere(ambient.Contains);   // absent again: IsUnknown sees the ambient name
        if (_macros.Count == 0 || UndefinedSomewhere is not { Count: > 0 } undefs) return;
        if (undefs.Count < _macros.Count)
        {
            foreach (var n in undefs) if (_macros.Remove(n)) _unknown.Add(n);
        }
        else
            foreach (var n in _macros.Keys.Where(undefs.Contains).ToList()) { _macros.Remove(n); _unknown.Add(n); }
    }

    /// <summary>Every absent name is unknown (the build log does not cover this file, or its define set may be
    /// incomplete) — closed-world resolution never applies to such a file (owner decision D-B).</summary>
    public bool OpenWorld { get; set; }

    /// <summary>Build from <c>-D</c>-style specs: "NAME" (defined as 1) or "NAME=VALUE".</summary>
    public static MacroTable FromDefines(IEnumerable<string> defines)
    {
        var t = new MacroTable();
        foreach (var d in defines) t.Define(d);
        return t;
    }

    /// <summary>Apply a "NAME" or "NAME=VALUE" spec (as from <c>-D</c> or <c>#define</c>). A function-like
    /// "F(x)=x" is stored under its name <c>F</c>, so <c>#ifdef F</c> sees it.</summary>
    public void Define(string spec)
    {
        var eq = spec.IndexOf('=');
        var lhs = eq < 0 ? spec : spec[..eq];
        var paren = lhs.IndexOf('(');
        if (paren >= 0) lhs = lhs[..paren];
        Set(lhs.Trim(), eq < 0 ? "1" : spec[(eq + 1)..].Trim());
    }

    public void Set(string name, string value)
    {
        if (name.Length == 0) return;
        _macros[name] = value;
        _unknown.Remove(name); // a concrete definition wins over "unknown"
        _undefined.Remove(name);
    }

    /// <summary>Remove a definition (a plain <c>#undef</c> whose certainty is not tracked).</summary>
    public void Undef(string name) => _macros.Remove(name);

    /// <summary>A certain <c>#undef</c>: the name is definitely undefined from here on.</summary>
    public void UndefCertain(string name)
    {
        if (name.Length == 0) return;
        _macros.Remove(name);
        _unknown.Remove(name);
        _undefined.Add(name);
    }

    /// <summary>Mark a name as UNKNOWN (varies / can't be resolved) so its branches are kept even under
    /// closed-world. No-op if the name is already concretely defined.</summary>
    public void MarkUnknown(string name)
    {
        if (name.Length > 0 && !_macros.ContainsKey(name)) _unknown.Add(name);
    }

    /// <summary>Force a name to UNKNOWN even if it is defined — a <c>#define</c>/<c>#undef</c> under a
    /// condition that may or may not hold (review PP3).</summary>
    public void ForceUnknown(string name)
    {
        if (name.Length == 0) return;
        _macros.Remove(name);
        _undefined.Remove(name);
        _unknown.Add(name);
    }

    public bool IsDefined(string name) => _macros.ContainsKey(name);

    public bool IsUnknown(string name)
    {
        if (_macros.ContainsKey(name)) return false;
        if (_unknown.Contains(name)) return true;
        if (_undefined.Contains(name)) return false;
        return OpenWorld || IsReserved(name) || name is "true" or "false" || NotFromTheBuild.Contains(name)
               || InttypesMacro.IsMatch(name) || (Ambient?.Contains(name) ?? false);
    }

    /// <summary>Macros a file gets from the compiler or the C library, not from the build's flags or the tree:
    /// pre-standard compiler built-ins without a leading underscore (gcc/clang in GNU modes define <c>linux</c>,
    /// <c>unix</c>, <c>i386</c>), and the limits/sizes/constants of the standard headers (<c>INT_MAX</c>, <c>EOF</c>).
    /// The headers live outside the tree and no probe reports them, so they are never absent.</summary>
    public static readonly IReadOnlySet<string> NotFromTheBuild = new HashSet<string>(StringComparer.Ordinal)
    {
        // non-reserved compiler built-ins
        "linux", "unix", "i386", "i486", "i586", "i686", "sun", "sparc", "mips", "vax", "hppa", "m68k", "mc68000",
        "ns32000", "pyr", "sequent", "u3b2", "MIPSEB", "MIPSEL", "arm", "thumb", "ppc", "powerpc",
        "WIN32", "WIN64", "WINNT",   // MinGW (review W1)
        // <limits.h>
        "CHAR_BIT", "SCHAR_MIN", "SCHAR_MAX", "UCHAR_MAX", "CHAR_MIN", "CHAR_MAX", "MB_LEN_MAX", "SHRT_MIN", "SHRT_MAX",
        "USHRT_MAX", "INT_MIN", "INT_MAX", "UINT_MAX", "LONG_MIN", "LONG_MAX", "ULONG_MAX", "LLONG_MIN", "LLONG_MAX",
        "ULLONG_MAX", "LONG_BIT", "WORD_BIT", "PATH_MAX", "NAME_MAX", "PIPE_BUF", "IOV_MAX", "LINE_MAX", "OPEN_MAX",
        "ARG_MAX", "PAGE_SIZE", "PAGESIZE", "SSIZE_MAX", "HOST_NAME_MAX",
        // <stdint.h> / <stddef.h> / <wchar.h>
        "INT8_MIN", "INT8_MAX", "UINT8_MAX", "INT16_MIN", "INT16_MAX", "UINT16_MAX", "INT32_MIN", "INT32_MAX",
        "UINT32_MAX", "INT64_MIN", "INT64_MAX", "UINT64_MAX", "INTPTR_MIN", "INTPTR_MAX", "UINTPTR_MAX", "INTMAX_MIN",
        "INTMAX_MAX", "UINTMAX_MAX", "PTRDIFF_MIN", "PTRDIFF_MAX", "SIZE_MAX", "SIG_ATOMIC_MIN", "SIG_ATOMIC_MAX",
        "WCHAR_MIN", "WCHAR_MAX", "WINT_MIN", "WINT_MAX", "INT8_C", "INT16_C", "INT32_C", "INT64_C", "UINT8_C",
        "UINT16_C", "UINT32_C", "UINT64_C", "INTMAX_C", "UINTMAX_C", "NULL", "offsetof", "WEOF",
        "INT_LEAST8_MAX", "INT_LEAST16_MAX", "INT_LEAST32_MAX", "INT_LEAST64_MAX", "UINT_LEAST8_MAX",
        "UINT_LEAST16_MAX", "UINT_LEAST32_MAX", "UINT_LEAST64_MAX", "INT_FAST8_MAX", "INT_FAST16_MAX",
        "INT_FAST32_MAX", "INT_FAST64_MAX", "UINT_FAST8_MAX", "UINT_FAST16_MAX", "UINT_FAST32_MAX", "UINT_FAST64_MAX",
        // <float.h>
        "FLT_RADIX", "FLT_MANT_DIG", "DBL_MANT_DIG", "LDBL_MANT_DIG", "FLT_DIG", "DBL_DIG", "LDBL_DIG", "FLT_MAX",
        "DBL_MAX", "LDBL_MAX", "FLT_MIN", "DBL_MIN", "LDBL_MIN", "FLT_EPSILON", "DBL_EPSILON", "LDBL_EPSILON",
        "FLT_EVAL_METHOD", "DECIMAL_DIG", "FLT_MAX_EXP", "DBL_MAX_EXP", "LDBL_MAX_EXP",
        // <stdio.h> / <stdlib.h> / <time.h> / <signal.h> / <errno.h> / <setjmp.h> / <assert.h> / <stdbool.h>
        "EOF", "BUFSIZ", "FILENAME_MAX", "FOPEN_MAX", "L_tmpnam", "TMP_MAX", "SEEK_SET", "SEEK_CUR", "SEEK_END",
        "stdin", "stdout", "stderr", "EXIT_SUCCESS", "EXIT_FAILURE", "RAND_MAX", "MB_CUR_MAX", "CLOCKS_PER_SEC",
        "SIGABRT", "SIGFPE", "SIGILL", "SIGINT", "SIGSEGV", "SIGTERM", "SIG_DFL", "SIG_ERR", "SIG_IGN",
        "EDOM", "ERANGE", "EILSEQ", "EINVAL", "ENOMEM", "ENOENT", "EAGAIN", "EINTR", "errno", "setjmp", "assert",
        "static_assert", "bool", "alignas", "alignof", "noreturn", "complex", "imaginary", "I",
        "HUGE_VAL", "HUGE_VALF", "INFINITY", "NAN", "FP_NAN", "FP_INFINITE", "FP_ZERO", "FP_NORMAL", "FP_SUBNORMAL",
        "MATH_ERRNO", "MATH_ERREXCEPT", "math_errhandling", "M_PI", "M_E",
    };

    /// <summary>
    /// Standard headers whose non-reserved macros are all in <see cref="NotFromTheBuild"/> (or that define none): a
    /// file that includes one from outside the tree is not hiding a macro. Any other angled header the tree doesn't
    /// have (<c>&lt;fcntl.h&gt;</c>, <c>&lt;signal.h&gt;</c>, <c>&lt;windows.h&gt;</c>) may define any name (review W1).
    /// </summary>
    public static bool IsListedStandardHeader(string include)
    {
        var name = include.Replace('\\', '/');
        if (name.StartsWith('c') && !name.Contains('.') && ListedStandardHeaders.Contains(name[1..] + ".h")) return true;
        return ListedStandardHeaders.Contains(name);
    }

    /// <summary><c>&lt;inttypes.h&gt;</c>'s format macros (<c>PRIu32</c>, <c>SCNxFAST16</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex InttypesMacro =
        new(@"^(?:PRI|SCN)[diouxX](?:8|16|32|64|LEAST(?:8|16|32|64)|FAST(?:8|16|32|64)|MAX|PTR)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ListedStandardHeaders = new(StringComparer.Ordinal)
    {
        "assert.h", "complex.h", "ctype.h", "float.h", "inttypes.h", "iso646.h", "limits.h",
        "setjmp.h", "stdalign.h", "stdarg.h", "stdbool.h", "stddef.h", "stdint.h", "stdio.h", "stdlib.h",
        "stdnoreturn.h", "string.h", "uchar.h", "wchar.h", "wctype.h",
        // C++ library headers
        "algorithm", "any", "array", "atomic", "bit", "bitset", "chrono", "codecvt", "compare", "concepts",
        "condition_variable", "deque", "exception", "execution", "expected", "filesystem", "format", "forward_list",
        "fstream", "functional", "future", "initializer_list", "iomanip", "ios", "iosfwd", "iostream", "istream",
        "iterator", "limits", "list", "map", "memory", "memory_resource", "mutex", "new", "numbers", "numeric",
        "optional", "ostream", "queue", "random", "ranges", "ratio", "regex", "scoped_allocator", "set",
        "shared_mutex", "source_location", "span", "sstream", "stack", "stdexcept", "streambuf", "string",
        "string_view", "system_error", "thread", "tuple", "type_traits", "typeindex", "typeinfo", "unordered_map",
        "unordered_set", "utility", "valarray", "variant", "vector", "version",
    };

    /// <summary>Reserved to the implementation: <c>__x</c> or <c>_</c> + uppercase — compiler built-ins.</summary>
    public static bool IsReserved(string name) =>
        name.Length >= 2 && name[0] == '_' && (name[1] == '_' || char.IsAsciiLetterUpper(name[1]));

    /// <summary>Concretely defined names (snapshot).</summary>
    public IReadOnlyList<string> Names => _macros.Keys.ToList();

    public string? Value(string name) => _macros.TryGetValue(name, out var v) ? v : null;

    public MacroTable Clone() => new(new Dictionary<string, string>(_macros, StringComparer.Ordinal),
                                     new HashSet<string>(_unknown, StringComparer.Ordinal),
                                     new HashSet<string>(_undefined, StringComparer.Ordinal), Ambient) { OpenWorld = OpenWorld, UndefinedSomewhere = UndefinedSomewhere };
}
