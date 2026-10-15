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
    private readonly NameSet _set;       // looks a word up without making a string of it
    private readonly bool[] _lengths;
    private readonly Regex? _regex;      // only for a set the one-pass search can't answer exactly

    public NameScan(IEnumerable<string> names, Shape shape)
    {
        _shape = shape;
        _names = new HashSet<string>(names, StringComparer.Ordinal);
        _lengths = new bool[(_names.Count == 0 ? 0 : _names.Max(n => n.Length)) + 1];
        foreach (var n in _names) _lengths[n.Length] = true;
        _set = new NameSet(_names);
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

    public IEnumerable<NameMatch> Matches(string text) => Matches(text, 0, text.Length);

    /// <summary>The matches in <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>], found as if
    /// that range were the whole text.</summary>
    public IEnumerable<NameMatch> Matches(string text, int start, int end)
    {
        if (_regex is not null)
        {
            foreach (Match m in _regex.Matches(text.Substring(start, end - start)))
                yield return new NameMatch(start + m.Index, m.Length, m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value);
            yield break;
        }
        for (var i = start; Next(text, ref i, end, out var m);) yield return m;
    }

    /// <summary>Whether <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>] holds a match, as
    /// <see cref="Matches(string, int, int)"/> finds them. Allocates nothing: for asking of many short ranges.</summary>
    public bool IsMatch(string text, int start, int end)
    {
        if (_regex is not null) return _regex.IsMatch(text.AsSpan(start, end - start));
        var i = start;
        return Next(text, ref i, end, out _);
    }

    // The next match from i, stopping at end; i moves past it.
    private bool Next(string text, ref int i, int end, out NameMatch match)
    {
        while (i < end)
        {
            if (!InRun(text[i])) { i++; continue; }
            var start = i;
            while (i < end && InRun(text[i])) i++;
            var len = i - start;
            if (len >= _lengths.Length || !_lengths[len]) continue;
            if (_set.Find(text.AsSpan(start, len)) is not { } name) continue;
            if (_shape == Shape.Word) { match = new NameMatch(start, len, name); return true; }
            var k = i;
            while (k < end && Space[text[k]]) k++;
            var call = k < end && text[k] == '(';
            if (_shape == Shape.NotCall)
            {
                if (!call) { match = new NameMatch(start, len, name); return true; }
                continue;
            }
            if (!call) continue;
            match = new NameMatch(start, k + 1 - start, name);
            i = k + 1;
            return true;
        }
        match = default;
        return false;
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
