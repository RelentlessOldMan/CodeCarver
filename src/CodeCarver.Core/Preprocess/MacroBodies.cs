using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The function-like macro bodies a tree defines, split into parameters and tokens once for every reader of them
/// (<see cref="DefinerMacros"/>, <see cref="PasteMacros"/>): both split the same bodies the same way, and a tree of
/// register headers has hundreds of thousands. Bodies are split on several threads; the list keeps the order of
/// <c>bodies</c> and of each macro's own bodies.
/// </summary>
public static class MacroBodies
{

    public static List<(string Name, List<string> Params, List<string> Tokens)> Parse(
        IReadOnlyDictionary<string, (bool FnLike, List<string> Bodies)> bodies)
    {
        var raw = new List<(string Name, string Body)>();
        foreach (var (name, (fnLike, list)) in bodies)
            if (fnLike) foreach (var b in list) raw.Add((name, b));
        // Not a file loop: counted files would count every macro (a link check showed 1,420,390 of 4,655 files).
        var split = new (List<string> Params, List<string> Tokens)?[raw.Count];
        Util.ReadAhead.For(raw.Count, i => split[i] = Split(raw[i].Body));
        var parsed = new List<(string, List<string>, List<string>)>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
            if (split[i] is { } s) parsed.Add((raw[i].Name, s.Params, s.Tokens));
        return parsed;
    }

    private static (List<string> Params, List<string> Tokens)? Split(string raw)
    {
        var flat = Regex.Replace(raw, @"\\\r?\n", " ");
        var close = flat.IndexOf(')');
        if (close < 0) return null;
        var ps = flat[..close].Split(',').Select(p => p.Trim()).ToList();
        // Comments and string literals blanked: `/* protects n state */` must not read as a declarator.
        var toks = Util.CodeTokens.MacroTokens(SourceText.CodeOnly(flat[(close + 1)..]));
        return toks.Count > 0 ? (ps, toks) : null;
    }
}
