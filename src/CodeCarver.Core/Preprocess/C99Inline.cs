using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// C99 inline: a header's <c>inline int f(int x) { ... }</c> (no <c>static</c>, no <c>extern</c>) is an inline
/// definition and emits NO symbol. The translation unit that declares <c>f</c> with <c>extern</c>, or without
/// <c>inline</c> (<c>extern inline int f(int);</c>), is the one that emits it, and nothing in it calls anything. A GNU
/// <c>extern inline</c> body (gnu89, or <c>__attribute__((gnu_inline))</c>) never emits one: an out-of-line definition
/// elsewhere does.
/// </summary>
public static class C99Inline
{
    static readonly Regex InlineDef = new(
        @"^[ \t]*(?<pre>(?:[A-Za-z_]\w*[ \t\*]+)*?(?:inline|__inline|__inline__)\b[\w \t\*]*?)\b(?<name>[A-Za-z_]\w*)[ \t]*\([^;{}]*\)\s*\{",
        RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex NotC99 = new(@"\b(?:static|extern|gnu_inline|always_inline)\b", RegexOptions.Compiled);
    static readonly Regex FileScopeDecl = new(
        @"^[ \t]*(?<pre>(?:[A-Za-z_]\w*[ \t\*]+)+)(?<name>[A-Za-z_]\w*)[ \t]*\([^;{}]*\)[ \t]*;", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex NotADecl = new(@"\b(?:return|else|case|goto|do|sizeof|typedef)\b", RegexOptions.Compiled);
    static readonly Regex InlineWord = new(@"\b(?:inline|__inline|__inline__)\b", RegexOptions.Compiled);
    static readonly Regex ExternWord = new(@"\bextern\b", RegexOptions.Compiled);

    /// <summary>Names a header defines as C99 inline definitions (no symbol of their own).</summary>
    public static IEnumerable<string> HeaderDefinitions(string text)
    {
        if (!text.Contains("inline", StringComparison.Ordinal)) yield break;
        foreach (Match m in InlineDef.Matches(text))
            if (!NotC99.IsMatch(m.Groups["pre"].Value)) yield return m.Groups["name"].Value;
    }

    /// <summary>Declarations in a translation unit that emit one of <paramref name="inlineFns"/>: (name, text index).</summary>
    public static IEnumerable<(string Name, int Index)> Emitters(string text, IReadOnlySet<string> inlineFns)
    {
        if (inlineFns.Count == 0) yield break;
        foreach (Match m in FileScopeDecl.Matches(text))
        {
            var name = m.Groups["name"].Value;
            var pre = m.Groups["pre"].Value;
            if (!inlineFns.Contains(name) || NotADecl.IsMatch(pre)) continue;
            if (InlineWord.IsMatch(pre) && !ExternWord.IsMatch(pre)) continue;   // another inline declaration: no symbol
            yield return (name, m.Groups["name"].Index);
        }
    }
}
