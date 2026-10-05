namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The set of preprocessor macros in effect — seeded from a build's <c>-D</c> flags and updated by the
/// <c>#define</c>/<c>#undef</c> directives a file itself contains as it is scanned. This is what makes
/// <c>#ifdef</c> resolution deterministic: the config IS the answer to which branch is live.
///
/// Every name is in one of three states: DEFINED (a concrete value), UNKNOWN (might or might not be defined —
/// both branches stay live, even under closed-world), or ABSENT (closed-world treats it as undefined). A name
/// is UNKNOWN when it was marked so, and also — unless this table defines it or saw a certain <c>#undef</c> —
/// when it is reserved to the implementation (<c>__x</c>, <c>_X</c>: compiler built-ins that no probe matched
/// to this TU), is C++'s <c>true</c>/<c>false</c>, or is in the <see cref="Ambient"/> set (#defined somewhere
/// in the tree, e.g. a config header this file includes). Closed-world only ever resolves names the build told
/// us about and nothing in the tree can define (review PP1/PP2).
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, string> _macros;
    // Names that are explicitly UNKNOWN (neither definitely-defined nor definitely-undefined) — e.g. a macro
    // that varies across a file's compile commands. The scanner treats these as unknown even under
    // closed-world, so their #ifdef branches are all kept (sound). Distinct from "absent", which closed-world
    // treats as undefined.
    private readonly HashSet<string> _unknown;
    // Names a certain #undef removed: definitely undefined from here on, whatever the ambient set says.
    private readonly HashSet<string> _undefined;

    public MacroTable()
    {
        _macros = new Dictionary<string, string>(StringComparer.Ordinal);
        _unknown = new HashSet<string>(StringComparer.Ordinal);
        _undefined = new HashSet<string>(StringComparer.Ordinal);
    }

    private MacroTable(Dictionary<string, string> macros, HashSet<string> unknown, HashSet<string> undefined,
                       IReadOnlySet<string>? ambient)
    {
        _macros = macros;
        _unknown = unknown;
        _undefined = undefined;
        Ambient = ambient;
    }

    /// <summary>Macro names #defined or #undef'd anywhere in the scanned tree (shared, read-only). Such a name,
    /// when this table has no concrete definition for it, is UNKNOWN rather than absent: a header this file
    /// includes may define it.</summary>
    public IReadOnlySet<string>? Ambient { get; set; }

    /// <summary>Build from <c>-D</c>-style specs: "NAME" (defined as 1) or "NAME=VALUE".</summary>
    public static MacroTable FromDefines(IEnumerable<string> defines)
    {
        var t = new MacroTable();
        foreach (var d in defines) t.Define(d);
        return t;
    }

    /// <summary>Apply a "NAME" or "NAME=VALUE" spec (as from <c>-D</c> or <c>#define</c>). A function-like
    /// "F(x)=x" is stored under its name <c>F</c>, so <c>#ifdef F</c> sees it.</summary>
    public void Define(string spec)
    {
        var eq = spec.IndexOf('=');
        var lhs = eq < 0 ? spec : spec[..eq];
        var paren = lhs.IndexOf('(');
        if (paren >= 0) lhs = lhs[..paren];
        Set(lhs.Trim(), eq < 0 ? "1" : spec[(eq + 1)..].Trim());
    }

    public void Set(string name, string value)
    {
        if (name.Length == 0) return;
        _macros[name] = value;
        _unknown.Remove(name); // a concrete definition wins over "unknown"
        _undefined.Remove(name);
    }

    /// <summary>Remove a definition (a plain <c>#undef</c> whose certainty is not tracked).</summary>
    public void Undef(string name) => _macros.Remove(name);

    /// <summary>A certain <c>#undef</c>: the name is definitely undefined from here on.</summary>
    public void UndefCertain(string name)
    {
        if (name.Length == 0) return;
        _macros.Remove(name);
        _unknown.Remove(name);
        _undefined.Add(name);
    }

    /// <summary>Mark a name as UNKNOWN (varies / can't be resolved) so its branches are kept even under
    /// closed-world. No-op if the name is already concretely defined.</summary>
    public void MarkUnknown(string name)
    {
        if (name.Length > 0 && !_macros.ContainsKey(name)) _unknown.Add(name);
    }

    /// <summary>Force a name to UNKNOWN even if it is defined — a <c>#define</c>/<c>#undef</c> under a
    /// condition that may or may not hold (review PP3).</summary>
    public void ForceUnknown(string name)
    {
        if (name.Length == 0) return;
        _macros.Remove(name);
        _undefined.Remove(name);
        _unknown.Add(name);
    }

    public bool IsDefined(string name) => _macros.ContainsKey(name);

    public bool IsUnknown(string name)
    {
        if (_macros.ContainsKey(name)) return false;
        if (_unknown.Contains(name)) return true;
        if (_undefined.Contains(name)) return false;
        return IsReserved(name) || name is "true" or "false" || (Ambient?.Contains(name) ?? false);
    }

    /// <summary>Reserved to the implementation: <c>__x</c> or <c>_</c> + uppercase — compiler built-ins.</summary>
    public static bool IsReserved(string name) =>
        name.Length >= 2 && name[0] == '_' && (name[1] == '_' || char.IsAsciiLetterUpper(name[1]));

    /// <summary>Concretely defined names (snapshot).</summary>
    public IReadOnlyList<string> Names => _macros.Keys.ToList();

    public string? Value(string name) => _macros.TryGetValue(name, out var v) ? v : null;

    public MacroTable Clone() => new(new Dictionary<string, string>(_macros, StringComparer.Ordinal),
                                     new HashSet<string>(_unknown, StringComparer.Ordinal),
                                     new HashSet<string>(_undefined, StringComparer.Ordinal), Ambient);
}
