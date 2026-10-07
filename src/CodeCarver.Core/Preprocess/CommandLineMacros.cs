using System.Text.RegularExpressions;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Command-line macros that change what a file DEFINES: <c>-Dsecret_rite=true_rite</c> makes
/// <c>int secret_rite(void) {...}</c> the symbol true_rite, and <c>'-DHIDE(n)=hid_##n'</c> makes
/// <c>int HIDE(den)(void) {...}</c> the symbol hid_den. Read from build-log compile commands and from the tree's own
/// build scripts (so a carve without a build log still sees them). Used only to ADD names a file may define, never
/// to remove one: an object-like macro whose value is a single identifier (a rename), and every function-like macro.
/// </summary>
public static class CommandLineMacros
{
    static readonly Regex ScriptDefine = new(
        @"(?:^|[\s'""])-D\s*['""]?(?<spec>[A-Za-z_]\w*(?:\([^)\s]*\))?=[^\s'""]*)", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex Spec = new(@"^(?<name>[A-Za-z_]\w*)(?:\((?<params>[^)]*)\))?=(?<body>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    /// <summary>The <c>-D</c> specs written in a build script's text (<c>NAME=VALUE</c>, <c>F(a)=body</c>).</summary>
    public static IEnumerable<string> SpecsInScript(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("-D", StringComparison.Ordinal)) yield break;
        foreach (Match m in ScriptDefine.Matches(text)) yield return m.Groups["spec"].Value;
    }

    /// <summary>The naming-relevant macros among <paramref name="specs"/>. A name given different values by different
    /// compiles keeps every value: each is a name the file may define.</summary>
    public static Dictionary<string, List<(List<string>? Params, string Body)>> FromSpecs(IEnumerable<string> specs)
    {
        var r = new Dictionary<string, List<(List<string>? Params, string Body)>>(StringComparer.Ordinal);
        void Add(string name, List<string>? ps, string body)
        {
            if (!r.TryGetValue(name, out var l)) r[name] = l = new();
            if (!l.Any(v => v.Body == body && (v.Params is null) == (ps is null))) l.Add((ps, body));
        }
        foreach (var s in specs)
        {
            var m = Spec.Match(s.Trim());
            if (!m.Success) continue;
            var body = m.Groups["body"].Value.Trim();
            if (m.Groups["params"].Success)
                Add(m.Groups["name"].Value, m.Groups["params"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(), body);
            else if (Identifier.IsMatch(body) && body != m.Groups["name"].Value)
                Add(m.Groups["name"].Value, null, body);
        }
        return r;
    }

    /// <summary>True when any of <paramref name="macros"/> is function-like.</summary>
    public static bool AnyFunctionLike(IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? macros)
        => macros is not null && macros.Values.Any(l => l.Any(v => v.Params is not null));

    /// <summary>The symbols an object-like rename of <paramref name="name"/> makes it.</summary>
    public static IEnumerable<string> RenamesOf(IReadOnlyDictionary<string, List<(List<string>? Params, string Body)>>? macros, string name)
        => macros is not null && macros.TryGetValue(name, out var l) ? l.Where(v => v.Params is null).Select(v => v.Body) : Enumerable.Empty<string>();
}
