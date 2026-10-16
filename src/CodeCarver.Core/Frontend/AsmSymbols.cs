using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Symbols top-level asm in C text defines: <c>.globl name</c> together with a <c>name:</c> label in the same text,
/// whether the asm is split one line per string literal or written as one literal with <c>\n</c> escapes (review S2).
/// Shared by the front end (a definition node) and the emitted-link check.
/// </summary>
public static class AsmSymbols
{
    // What may come before an assembler token: line start, white space, a quote, `;`, or an escape (`\n`, `\t`).
    private const string Before = @"(?:^|[\s"";]|\\[nt])";
    private static readonly Regex Global = new(
        Before + @"\.(?:globl|global|weak|weakref)\s+(?<names>[A-Za-z_.$][\w.$]*(?:\s*,\s*[A-Za-z_.$][\w.$]*)*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Each name made global and labelled, with the index of its <c>.globl</c>.</summary>
    public static IEnumerable<(string Name, int Index)> DefinedLabels(string text)
    {
        if (!text.Contains(".gl", StringComparison.Ordinal) && !text.Contains(".weak", StringComparison.Ordinal)) yield break;
        foreach (Match m in Global.Matches(text))
            foreach (var n in m.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries))
                if (Regex.IsMatch(text, Before + Regex.Escape(n) + @"\s*:(?!:)", RegexOptions.Multiline | RegexOptions.CultureInvariant))
                    yield return (n, m.Groups["names"].Index);
    }
}
