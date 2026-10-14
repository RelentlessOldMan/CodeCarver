using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Util;

/// <summary>One match of a <see cref="NameScan"/>: the whole match (for a call shape, through its <c>(</c>) and the
/// name in it.</summary>
public readonly record struct NameMatch(int Index, int Length, string Name);

/// <summary>
/// Finds any of a set of names in a text, as a regex alternation of them would (<c>\b(?:A|B|...)\b</c> and its kin),
/// in one pass over the text whatever the size of the set. The regex tried every name at every position: on a tree
/// with thousands of macros of a kind, each file paid for all of them (4 MB of code against 1,000 names: 4.9 s, 10,000:
/// 28 s, 50,000: the regex overflowed the stack). Here each identifier in the text is looked up once.
/// The answer is the regex's: characters are classed by the regex's own <c>\w</c>, <c>\b</c> and <c>\s</c>, and a set
/// with a name that is not one run of word characters is handed to the regex itself.
/// </summary>
public sealed class NameScan
{
    public enum Shape
    {
        /// <summary><c>\b(?:names)\b</c></summary>
        Word,
        /// <summary><c>\b(names)\s*\(</c>: the match runs through the <c>(</c>.</summary>
        Call,
        /// <summary><c>\b(?:names)\b(?!\s*\()</c></summary>
        NotCall,
        /// <summary><c>(?&lt;![\w$])(names)\s*\(</c>: as <see cref="Call"/>, with <c>$</c> part of a name.</summary>
        PasteCall,
    }

    private static readonly bool[] WordChar = Table(@"^\w$");
    private static readonly bool[] BoundaryChar = Table(@"\b");     // a one-character string has \b iff it is a word char for \b
    private static readonly bool[] Space = Table(@"^\s$");

    private static bool[] Table(string pattern)
    {
        var rx = new Regex(pattern, RegexOptions.CultureInvariant);
        var t = new bool[char.MaxValue + 1];
        for (var c = 0; c <= char.MaxValue; c++) t[c] = rx.IsMatch(((char)c).ToString());
        return t;
    }

    private readonly Shape _shape;
    private readonly HashSet<string> _names;
    private readonly bool[] _lengths;
    private readonly Regex? _regex;      // only for a set the one-pass search can't answer exactly

    public NameScan(IEnumerable<string> names, Shape shape)
    {
        _shape = shape;
        _names = new HashSet<string>(names, StringComparer.Ordinal);
        _lengths = new bool[(_names.Count == 0 ? 0 : _names.Max(n => n.Length)) + 1];
        foreach (var n in _names) _lengths[n.Length] = true;
        if (_names.Any(n => n.Length == 0 || !n.All(InRun)))
            _regex = new Regex(Pattern(_names, shape), RegexOptions.Compiled);
    }

    /// <summary>The regex this scan answers as.</summary>
    public static string Pattern(IEnumerable<string> names, Shape shape)
    {
        var alt = string.Join("|", names.Select(Regex.Escape));
        return shape switch
        {
            Shape.Word => @"\b(?:" + alt + @")\b",
            Shape.Call => @"\b(" + alt + @")\s*\(",
            Shape.NotCall => @"\b(?:" + alt + @")\b(?!\s*\()",
            _ => @"(?<![\w$])(" + alt + @")\s*\(",
        };
    }

    public int Count => _names.Count;

    private bool InRun(char c) => _shape == Shape.PasteCall ? WordChar[c] || c == '$' : BoundaryChar[c];

    public bool IsMatch(string text) => Matches(text).Any();

    public IEnumerable<NameMatch> Matches(string text)
    {
        if (_regex is not null)
        {
            foreach (Match m in _regex.Matches(text))
                yield return new NameMatch(m.Index, m.Length, m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value);
            yield break;
        }
        var i = 0;
        while (i < text.Length)
        {
            if (!InRun(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && InRun(text[i])) i++;
            var len = i - start;
            if (len >= _lengths.Length || !_lengths[len]) continue;
            var name = text.Substring(start, len);
            if (!_names.Contains(name)) continue;
            if (_shape == Shape.Word) { yield return new NameMatch(start, len, name); continue; }
            var k = i;
            while (k < text.Length && Space[text[k]]) k++;
            var call = k < text.Length && text[k] == '(';
            if (_shape == Shape.NotCall)
            {
                if (!call) yield return new NameMatch(start, len, name);
                continue;
            }
            if (!call) continue;
            yield return new NameMatch(start, k + 1 - start, name);
            i = k + 1;
        }
    }

    public string Replace(string text, Func<NameMatch, string> with)
    {
        StringBuilder? sb = null;
        var at = 0;
        foreach (var m in Matches(text))
        {
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, at, m.Index - at).Append(with(m));
            at = m.Index + m.Length;
        }
        return sb is null ? text : sb.Append(text, at, text.Length - at).ToString();
    }
}
