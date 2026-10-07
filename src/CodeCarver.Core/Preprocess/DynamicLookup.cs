using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Functions found BY NAME at run time: <c>dlsym(RTLD_DEFAULT, "dl_target")</c>, <c>GetProcAddress(h, "Init")</c>.
/// Nothing names the function in code, and a carve that drops it still links: the lookup just fails at run time. In
/// a file that does such a lookup, every string literal spelling an identifier counts as a reference to it (more
/// kept, never less).
/// </summary>
public static class DynamicLookup
{
    static readonly Regex Lookup = new(
        @"\b(?:dlsym|dlvsym|dlfunc|GetProcAddress|lt_dlsym|g_module_symbol|SDL_LoadFunction|uv_dlsym|PR_FindSymbol|apr_dso_sym)\b",
        RegexOptions.Compiled);
    static readonly Regex NameString = new(@"""([A-Za-z_]\w*)""", RegexOptions.Compiled);

    /// <summary>The identifiers named by string literals in <paramref name="text"/>, with their text index, when the
    /// text does a by-name symbol lookup; otherwise nothing.</summary>
    public static IEnumerable<(string Name, int Index)> Names(string text)
    {
        if (string.IsNullOrEmpty(text) || !Lookup.IsMatch(text)) yield break;
        foreach (Match m in NameString.Matches(text)) yield return (m.Groups[1].Value, m.Groups[1].Index);
    }
}
