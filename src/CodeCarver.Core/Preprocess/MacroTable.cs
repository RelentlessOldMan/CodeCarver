namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The set of preprocessor macros in effect — seeded from a build's <c>-D</c> flags and updated by the
/// <c>#define</c>/<c>#undef</c> directives a file itself contains as it is scanned. This is what makes
/// <c>#ifdef</c> resolution deterministic: the config IS the answer to which branch is live.
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, string> _macros;

    public MacroTable() => _macros = new Dictionary<string, string>(StringComparer.Ordinal);

    private MacroTable(Dictionary<string, string> macros) => _macros = macros;

    /// <summary>Build from <c>-D</c>-style specs: "NAME" (defined as 1) or "NAME=VALUE".</summary>
    public static MacroTable FromDefines(IEnumerable<string> defines)
    {
        var t = new MacroTable();
        foreach (var d in defines) t.Define(d);
        return t;
    }

    /// <summary>Apply a "NAME" or "NAME=VALUE" spec (as from <c>-D</c> or <c>#define</c>).</summary>
    public void Define(string spec)
    {
        var eq = spec.IndexOf('=');
        if (eq < 0) Set(spec.Trim(), "1");
        else Set(spec[..eq].Trim(), spec[(eq + 1)..].Trim());
    }

    public void Set(string name, string value)
    {
        if (name.Length > 0) _macros[name] = value;
    }

    public void Undef(string name) => _macros.Remove(name);

    public bool IsDefined(string name) => _macros.ContainsKey(name);

    public string? Value(string name) => _macros.TryGetValue(name, out var v) ? v : null;

    public MacroTable Clone() => new(new Dictionary<string, string>(_macros, StringComparer.Ordinal));
}
