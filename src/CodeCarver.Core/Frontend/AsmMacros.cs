using System.Text;
using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// The names assembly builds out of pieces: a gas <c>.macro</c> pasting an argument (<c>.word \name\()_IRQHandler</c>
/// called as <c>def_vec USART1</c>) and, in a <c>.S</c> run through the C preprocessor, a <c>#define</c> pasting with
/// <c>##</c> (<c>.word H(UART)</c> with <c>#define H(x) x##_Handler</c>). Neither name is in the text, so a scan of its
/// identifiers misses the handler. Fed lines in order (definitions before uses, as the assembler and preprocessor
/// need them), it gives the text each invocation expands to. Over-approximate: every invocation is expanded, live or
/// not, and a name it can't resolve is just left as it was.
/// </summary>
public sealed class AsmMacros
{
    const int MaxDepth = 8;
    static readonly Regex Ident = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);
    static readonly Regex MacroHead = new(@"^\s*\.macro\s+([A-Za-z_.$][\w.$]*)\s*,?\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex Endm = new(@"^\s*\.endm\b", RegexOptions.Compiled);
    static readonly Regex Define = new(@"^\s*#\s*define\s+([A-Za-z_]\w*)(\(([^)]*)\))?\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex Paste = new(@"\s*##\s*", RegexOptions.Compiled);

    sealed record GasMacro(string[] Params, string[] Defaults, List<string> Body);
    readonly Dictionary<string, GasMacro> _gas = new(StringComparer.OrdinalIgnoreCase);   // gas macro names ignore case
    readonly Dictionary<string, (string[] Params, string Body)> _cppFn = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _cppObj = new(StringComparer.Ordinal);
    GasMacro? _open;
    int _openDepth;
    string _pending = "";   // a #define continued with a backslash

    public bool Any => _gas.Count > 0 || _cppFn.Count > 0 || _cppObj.Count > 0;

    /// <summary>Takes the definitions in <paramref name="text"/> and returns what its invocations expand to.</summary>
    public string Feed(string text, bool expand = true)
    {
        var sb = expand ? new StringBuilder() : null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (_pending.Length > 0 || IsDefine(line))
            {
                if (line.EndsWith('\\')) { _pending += line[..^1] + " "; continue; }
                line = _pending + line;
                _pending = "";
                var d = Define.Match(line);
                if (d.Success)
                {
                    var body = StripComment(d.Groups[4].Value);
                    if (d.Groups[2].Success)
                        _cppFn[d.Groups[1].Value] = (d.Groups[3].Value.Split(',').Select(p => p.Trim()).ToArray(), body);
                    else _cppObj[d.Groups[1].Value] = body;
                }
                continue;
            }
            if (_open is { } m)
            {
                if (MacroHead.IsMatch(line)) _openDepth++;
                else if (Endm.IsMatch(line) && _openDepth-- == 0) { _open = null; continue; }
                m.Body.Add(line);
                continue;
            }
            var h = MacroHead.Match(line);
            if (h.Success)
            {
                var ps = SplitArgs(h.Groups[2].Value);
                var names = new string[ps.Count];
                var defs = new string[ps.Count];
                for (var i = 0; i < ps.Count; i++)
                {
                    var p = ps[i];
                    var eq = p.IndexOf('=');
                    defs[i] = eq >= 0 ? p[(eq + 1)..].Trim() : "";
                    var n = eq >= 0 ? p[..eq] : p;
                    var colon = n.IndexOf(':');
                    names[i] = (colon >= 0 ? n[..colon] : n).Trim();
                }
                _open = new GasMacro(names, defs, new List<string>());
                _openDepth = 0;
                _gas[h.Groups[1].Value] = _open;
                continue;
            }
            if (sb is not null)
                foreach (var statement in line.Split(';'))   // ';' separates statements on one line
                    Expand(statement, 0, sb);
        }
        return sb?.ToString() ?? "";
    }

    /// <summary>The bodies of object-like #defines among <paramref name="names"/>, pasted, transitively.</summary>
    public IEnumerable<string> ObjectBodies(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(names.Where(_cppObj.ContainsKey));
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (!seen.Add(n)) continue;
            var body = Paste.Replace(_cppObj[n], "");
            yield return body;
            foreach (Match m in Ident.Matches(body))
                if (_cppObj.ContainsKey(m.Value)) queue.Enqueue(m.Value);
        }
    }

    static bool IsDefine(string line)
    {
        var t = line.AsSpan().TrimStart();
        return t.Length > 1 && t[0] == '#' && t[1..].TrimStart().StartsWith("define");
    }

    static string StripComment(string s)
    {
        var i = s.IndexOf("/*", StringComparison.Ordinal);
        var j = s.IndexOf("//", StringComparison.Ordinal);
        var k = i < 0 ? j : j < 0 ? i : Math.Min(i, j);
        return k < 0 ? s : s[..k];
    }

    void Expand(string line, int depth, StringBuilder sb)
    {
        if (depth >= MaxDepth) return;
        if (_cppFn.Count > 0) ExpandCpp(line, depth, sb);
        if (_gas.Count == 0) return;
        // An invocation is the line's first word, after any label; its arguments are the rest.
        var s = line.TrimStart();
        var colon = s.IndexOf(':');
        if (colon > 0 && Ident.Match(s).Length == colon) s = s[(colon + 1)..].TrimStart();
        var w = Regex.Match(s, @"^[A-Za-z_.$][\w.$]*");
        if (!w.Success || !_gas.TryGetValue(w.Value, out var mac)) return;
        var args = SplitArgs(s[w.Length..]);
        var values = (string[])mac.Defaults.Clone();
        var pos = 0;
        foreach (var a in args)
        {
            var eq = Regex.Match(a, @"^\s*([A-Za-z_]\w*)\s*=(.*)$");
            var k = eq.Success ? Array.IndexOf(mac.Params, eq.Groups[1].Value) : -1;
            if (k >= 0) values[k] = eq.Groups[2].Value.Trim();
            else if (pos < values.Length) values[pos++] = a.Trim();
            else if (values.Length > 0) values[^1] += "," + a;   // :vararg takes the rest
        }
        var order = Enumerable.Range(0, mac.Params.Length).OrderByDescending(i => mac.Params[i].Length).ToArray();
        foreach (var bodyLine in mac.Body)
        {
            var b = bodyLine;
            foreach (var i in order)
                if (mac.Params[i].Length > 0) b = b.Replace("\\" + mac.Params[i], values[i]);
            b = b.Replace("\\()", "").Replace("\\@", "0");
            sb.Append(b).Append('\n');
            Expand(b, depth + 1, sb);
        }
    }

    void ExpandCpp(string line, int depth, StringBuilder sb)
    {
        foreach (Match m in Ident.Matches(line))
        {
            if (!_cppFn.TryGetValue(m.Value, out var mac)) continue;
            var i = m.Index + m.Length;
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            if (i >= line.Length || line[i] != '(') continue;
            var args = new List<string>();
            var depthP = 0;
            var start = i + 1;
            for (i++; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '(') depthP++;
                else if (c == ')' && depthP-- == 0) { args.Add(line[start..i]); break; }
                else if (c == ',' && depthP == 0) { args.Add(line[start..i]); start = i + 1; }
            }
            var body = mac.Body;
            for (var p = 0; p < mac.Params.Length && p < args.Count; p++)
                if (mac.Params[p].Length > 0)
                    body = Regex.Replace(body, @"\b" + Regex.Escape(mac.Params[p]) + @"\b", args[p].Trim().Replace("$", "$$"));
            body = Paste.Replace(body, "");
            sb.Append(body).Append('\n');
            Expand(body, depth + 1, sb);
        }
    }

    static List<string> SplitArgs(string s)
    {
        s = s.Trim();
        var cut = s.IndexOfAny(new[] { '@', ';' });
        if (cut >= 0) s = s[..cut];
        var cmt = s.IndexOf("/*", StringComparison.Ordinal);
        if (cmt >= 0) s = s[..cmt];
        if (s.Length == 0) return new List<string>();
        return (s.Contains(',') ? s.Split(',') : s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(a => a.Trim()).ToList();
    }
}
