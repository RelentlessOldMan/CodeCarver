using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Symbols the LINK needs whatever the C code calls: named on a link line or in a linker script, never in C.
/// <c>-Wl,--defsym=omen_call=omen_real</c> makes ld resolve <c>omen_real</c> even if nothing calls
/// <c>omen_call</c> (and fails without it); <c>--undefined</c> / <c>--require-defined</c> / <c>-u</c> pull a symbol
/// in; <c>--entry</c> / <c>-e</c> / <c>ENTRY()</c> name the image's entry; a linker script's <c>EXTERN()</c> and
/// <c>PROVIDE(a = b)</c> / <c>a = b;</c> do the same. They are read from build logs and from the tree's own build
/// scripts (makefiles, shell scripts, CMake, linker scripts), so a carve without a build log still sees them.
/// </summary>
public static class LinkFlags
{
    static readonly Regex Defsym = new(@"--defsym[=\s,]+([A-Za-z_.$][\w.$]*)\s*=\s*([^\s,;'""]+)", RegexOptions.Compiled);
    static readonly Regex Named = new(@"(?:--undefined|--require-defined|--entry|--wrap)[=\s,]+([A-Za-z_.$][\w.$]*)", RegexOptions.Compiled);
    static readonly Regex WlShort = new(@"-Wl,(?:[^\s,]+,)*-(?:u|e),([A-Za-z_.$][\w.$]*)", RegexOptions.Compiled);
    static readonly Regex XlinkerShort = new(@"-Xlinker\s+-(?:u|e)\s+-Xlinker\s+([A-Za-z_.$][\w.$]*)", RegexOptions.Compiled);
    static readonly Regex ScriptEntry = new(@"\b(?:ENTRY|EXTERN)\s*\(([^)]*)\)", RegexOptions.Compiled);
    static readonly Regex ScriptAssign = new(@"(?:\bPROVIDE(?:_HIDDEN)?\s*\(\s*[A-Za-z_.$][\w.$]*\s*=\s*([^;)]*)\)|^\s*[A-Za-z_.$][\w.$]*\s*=\s*([^;]*);)", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex Ident = new(@"[A-Za-z_$][\w$]*", RegexOptions.Compiled);
    static readonly HashSet<string> ScriptWords = new(StringComparer.Ordinal)
        { "ADDR", "ALIGN", "ABSOLUTE", "DEFINED", "LOADADDR", "ORIGIN", "LENGTH", "SIZEOF", "SIZEOF_HEADERS", "MAX", "MIN",
          "NEXT", "LOG2CEIL", "CONSTANT", "MAXPAGESIZE", "COMMONPAGESIZE", "SEGMENT_START", "DATA_SEGMENT_ALIGN",
          "DATA_SEGMENT_END", "DATA_SEGMENT_RELRO_END", "BLOCK", "ALIGNOF" };

    /// <summary>Is this a file whose text may carry link flags (build script, makefile, linker script)?</summary>
    public static bool IsBuildScript(string rel)
    {
        var name = Path.GetFileName(rel);
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return IsLinkerScript(rel)
            || ext is ".sh" or ".bash" or ".mk" or ".mak" or ".make" or ".cmake" or ".bat" or ".cmd" or ".ps1" or ".py" or ".ninja"
                or ".rsp" or ".bzl" or ".bazel" or ".gn" or ".gni" or ".scons"
            || name.StartsWith("Makefile", StringComparison.OrdinalIgnoreCase) || name.Equals("GNUmakefile", StringComparison.Ordinal)
            || name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || name is "SConstruct" or "SConscript"
            || name is "meson.build" or "BUILD" or "BUILD.gn" or "build.ninja";
    }

    public static bool IsLinkerScript(string rel) => Path.GetExtension(rel).ToLowerInvariant() is ".ld" or ".lds" or ".ldscript";

    /// <summary>The symbols <paramref name="text"/> requires at link time, with the 1-based line naming each.</summary>
    public static List<(string Name, int Line)> RequiredSymbols(string text, bool linkerScript)
    {
        var found = new List<(string, int)>();
        if (string.IsNullOrEmpty(text)) return found;
        int LineOf(int index) { var n = 1; for (var i = 0; i < index; i++) if (text[i] == '\n') n++; return n; }
        void Expr(string expr, int index)
        {
            foreach (Match id in Ident.Matches(expr))
                if (!ScriptWords.Contains(id.Value) && !(id.Index > 0 && char.IsDigit(expr[id.Index - 1])))
                    found.Add((id.Value, LineOf(index)));
        }
        if (text.Contains("defsym", StringComparison.Ordinal))
            foreach (Match m in Defsym.Matches(text)) Expr(m.Groups[2].Value, m.Index);
        if (text.Contains("--", StringComparison.Ordinal))
            foreach (Match m in Named.Matches(text)) found.Add((m.Groups[1].Value, LineOf(m.Index)));
        if (text.Contains("-Wl,", StringComparison.Ordinal))
            foreach (Match m in WlShort.Matches(text)) found.Add((m.Groups[1].Value, LineOf(m.Index)));
        if (text.Contains("-Xlinker", StringComparison.Ordinal))
            foreach (Match m in XlinkerShort.Matches(text)) found.Add((m.Groups[1].Value, LineOf(m.Index)));
        if (linkerScript)
        {
            foreach (Match m in ScriptEntry.Matches(text))
                foreach (Match id in Ident.Matches(m.Groups[1].Value)) found.Add((id.Value, LineOf(m.Index)));
            foreach (Match m in ScriptAssign.Matches(text))
                Expr(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, m.Index);
        }
        return found;
    }
}
