using System.Text.RegularExpressions;

namespace CodeCarver.Core.Diagnostics;

public enum BuildErrorKind
{
    /// <summary>The link found no definition: a dropped file defined it.</summary>
    UndefinedReference,
    /// <summary>An identifier, macro or function with no declaration in scope: a dropped header or #define.</summary>
    Undeclared,
    /// <summary>A type name the compiler doesn't know: a dropped typedef or header.</summary>
    UnknownType,
    /// <summary>An #include the compiler can't find: a dropped header.</summary>
    MissingHeader,
    Other,
}

/// <summary>One compiler or linker error: where (when it says), what kind, and the name it is about.</summary>
public sealed record BuildError(string? File, int Line, BuildErrorKind Kind, string? Name, string Message);

/// <summary>
/// Reads the errors out of a build's output, for gcc, clang, MSVC, armcc/armclang (Keil) and IAR compilers and
/// GNU ld, lld, link.exe, armlink and the IAR linker. Warnings and notes are skipped; the same error at the same
/// place is reported once.
/// </summary>
public static class BuildErrors
{
    // gcc/clang: path:line[:col]: [fatal ]error: message
    static readonly Regex Gnu = new(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?<line>\d+)(?::\d+)?:\s*(?:fatal\s+)?error:\s*(?<msg>.*)$", RegexOptions.Compiled);
    // GNU ld: path:(.text+0x9): undefined reference to `name'   or   path:line: undefined reference ...
    static readonly Regex GnuLd = new(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?:(?<line>\d+)|\([^)]*\)):\s*(?<msg>undefined reference to .*)$", RegexOptions.Compiled);
    // MSVC: path(line[,col]): [fatal ]error Cnnnn: message
    static readonly Regex Msvc = new(@"^(?<file>.+?)\((?<line>\d+)(?:,\d+)?\)\s*:\s*(?:fatal\s+)?error\s+\w+\s*:\s*(?<msg>.*)$", RegexOptions.Compiled);
    // armcc: "path", line N: Error: ...    IAR: "path",N  Error[Pe020]: ...
    static readonly Regex ArmIar = new(@"^""(?<file>[^""]+)"",\s*(?:line\s+)?(?<line>\d+)\s*:?\s*(?:Fatal\s+)?[Ee]rror(?:\[\w+\])?:\s*(?:#\d+:\s*)?(?<msg>.*)$", RegexOptions.Compiled);
    // Linkers with no source position.
    static readonly Regex Lld = new(@"error:\s*undefined symbol:\s*(?<name>\S+)", RegexOptions.Compiled);
    static readonly Regex LldRef = new(@"^>>>\s+referenced by\s+(?<file>[^:\s]+):(?<line>\d+)", RegexOptions.Compiled);
    static readonly Regex Lnk = new(@"error LNK\d+:\s*unresolved external symbol\s+(?<name>[^\s(]+)", RegexOptions.Compiled);
    static readonly Regex Armlink = new(@"L6218E:\s*Undefined symbol\s+(?<name>[^\s(]+)", RegexOptions.Compiled);
    static readonly Regex IarLink = new(@"Error\[Li005\]:\s*no definition for\s+""(?<name>[^""]+)""", RegexOptions.Compiled);

    static readonly Regex Quoted = new(@"[`'‘""](?<q>[^`'’""]+)[`'’""]", RegexOptions.Compiled);
    static readonly Regex Identifier = new(@"^[A-Za-z_$][\w$]*$", RegexOptions.Compiled);

    public static List<BuildError> Parse(string log)
    {
        var result = new List<BuildError>();
        var seen = new HashSet<(string?, int, string?, BuildErrorKind)>();
        void Add(BuildError e)
        {
            if (seen.Add((e.File, e.Line, e.Name, e.Kind))) result.Add(e);
        }
        var lines = log.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            Match m;
            if ((m = Lld.Match(line)).Success)
            {
                string? file = null; var ln = 0;
                if (i + 1 < lines.Length && LldRef.Match(lines[i + 1].Trim()) is { Success: true } r)
                { file = r.Groups["file"].Value; ln = int.Parse(r.Groups["line"].Value); }
                Add(new BuildError(file, ln, BuildErrorKind.UndefinedReference, m.Groups["name"].Value, line));
                continue;
            }
            if ((m = Lnk.Match(line)).Success || (m = Armlink.Match(line)).Success || (m = IarLink.Match(line)).Success)
            {
                var name = m.Groups["name"].Value;
                // MSVC decorates C names on x86 with a leading underscore.
                if (line.Contains("LNK", StringComparison.Ordinal) && name.StartsWith('_') && !name.StartsWith("__", StringComparison.Ordinal)) name = name[1..];
                Add(new BuildError(null, 0, BuildErrorKind.UndefinedReference, name, line));
                continue;
            }
            if ((m = GnuLd.Match(line)).Success || (m = ArmIar.Match(line)).Success || (m = Msvc.Match(line)).Success || (m = Gnu.Match(line)).Success)
            {
                var msg = m.Groups["msg"].Value;
                var ln = m.Groups["line"].Success && m.Groups["line"].Value.Length > 0 ? int.Parse(m.Groups["line"].Value) : 0;
                var (kind, name) = Classify(msg);
                Add(new BuildError(m.Groups["file"].Value.Trim(), ln, kind, name, line));
            }
        }
        return result;
    }

    static (BuildErrorKind, string?) Classify(string msg)
    {
        var lower = msg.ToLowerInvariant();
        // A missing header: gcc puts the name before ": No such file", MSVC and IAR quote it.
        if (lower.Contains("no such file") || lower.Contains("cannot open include file") || lower.Contains("cannot open source file")
            || lower.Contains("file not found"))
        {
            var q = Quoted.Match(msg);
            if (q.Success) return (BuildErrorKind.MissingHeader, q.Groups["q"].Value);
            var colon = msg.IndexOf(':');
            return (BuildErrorKind.MissingHeader, colon > 0 ? msg[..colon].Trim() : null);
        }
        var kind = lower.Contains("undefined reference") ? BuildErrorKind.UndefinedReference
            : lower.Contains("unknown type name") || lower.Contains("is not a type") || lower.Contains("does not name a type")
              ? BuildErrorKind.UnknownType
            : lower.Contains("undeclared") || lower.Contains("implicit declaration") || lower.Contains("is undefined")
              || lower.Contains("was not declared") || lower.Contains("not declared in this scope") ? BuildErrorKind.Undeclared
            : BuildErrorKind.Other;
        string? name = null;
        foreach (Match q in Quoted.Matches(msg))
            if (Identifier.IsMatch(q.Groups["q"].Value)) { name = q.Groups["q"].Value; break; }
        return (kind, name);
    }
}
