namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The set of preprocessor macros in effect — seeded from a build's <c>-D</c> flags and updated by the
/// <c>#define</c>/<c>#undef</c> directives a file itself contains as it is scanned. This is what makes
/// <c>#ifdef</c> resolution deterministic: the config IS the answer to which branch is live.
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, string> _macros;
    // Names that are explicitly UNKNOWN (neither definitely-defined nor definitely-undefined) — e.g. a macro
    // that varies across a file's compile commands. The scanner treats these as unknown even under
    // closed-world, so their #ifdef branches are all kept (sound). Distinct from "absent", which closed-world
    // treats as undefined.
    private readonly HashSet<string> _unknown;

    public MacroTable()
    {
        _macros = new Dictionary<string, string>(StringComparer.Ordinal);
        _unknown = new HashSet<string>(StringComparer.Ordinal);
    }

    private MacroTable(Dictionary<string, string> macros, HashSet<string> unknown)
    {
        _macros = macros;
        _unknown = unknown;
    }

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
        if (name.Length == 0) return;
        _macros[name] = value;
        _unknown.Remove(name); // a concrete definition wins over "unknown"
    }

    public void Undef(string name) => _macros.Remove(name);

    /// <summary>Mark a name as UNKNOWN (varies / can't be resolved) so its branches are kept even under
    /// closed-world. No-op if the name is already concretely defined.</summary>
    public void MarkUnknown(string name)
    {
        if (name.Length > 0 && !_macros.ContainsKey(name)) _unknown.Add(name);
    }

    public bool IsDefined(string name) => _macros.ContainsKey(name);

    public bool IsUnknown(string name) => _unknown.Contains(name);

    public string? Value(string name) => _macros.TryGetValue(name, out var v) ? v : null;

    public MacroTable Clone() => new(new Dictionary<string, string>(_macros, StringComparer.Ordinal),
                                     new HashSet<string>(_unknown, StringComparer.Ordinal));
}
