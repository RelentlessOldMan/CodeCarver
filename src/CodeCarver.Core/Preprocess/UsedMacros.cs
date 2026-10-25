using System.Security.Cryptography;
using System.Text;
using CodeCarver.Core.Frontend;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The macros the build's own compiler used, from what each compile printed with GCC's <c>-E -dU</c>: a <c>#define</c>
/// for every macro it expanded or tested, a <c>#undef</c> for every name it tested undefined. The header carve keeps a
/// big header's define only if some compile used its name (docs/USED-MACROS.md), which needs no model of what paste
/// macros can build: the compiler has already built it.
///
/// A file counts toward coverage only when it is complete (its last line is <c>#codecarver-ok</c>, which the collector
/// writes after the compiler exited 0) and names its source (the first line marker). The carve trusts the names only
/// when every compile command the build log has is matched by such a file and every translation unit the build
/// compiles has a command: a compile the carve didn't hear from could use a define the others didn't.
/// </summary>
public sealed class UsedMacros
{
    public const string OkLine = "#codecarver-ok";
    public const string CwdPrefix = "#codecarver-cwd ";

    /// <summary>Every name some file printed as used or tested, complete or not (a name more is kept, never one less), by
    /// key (<see cref="CodeCarver.Core.Util.NameKey"/>): held as strings, two million names took 280 MB. Two names on one
    /// key keep a define more, never one less.</summary>
    public HashSet<ulong> Names { get; } = new();

    /// <summary>Some file printed <paramref name="name"/> as used or tested.</summary>
    public bool Uses(string name) => Names.Contains(CodeCarver.Core.Util.NameKey.Short(name));

    public int Files { get; private set; }
    /// <summary>Files without the closing <c>#codecarver-ok</c> line: a compile that failed or was cut short.</summary>
    public int Incomplete { get; private set; }
    /// <summary>Complete files that name no source (no line marker: <c>-P</c>, or not <c>-E</c> output).</summary>
    public int NoSource { get; private set; }
    /// <summary>Complete files with no <c>#define</c>/<c>#undef</c> at all: not <c>-dU</c> output (Clang ignores it).</summary>
    public int NoMacros { get; private set; }
    /// <summary>Complete files the same as an earlier one, byte for byte: one compile, counted once.</summary>
    public int Duplicates { get; private set; }

    private readonly List<(string? Cwd, string Source)> _units = new();

    /// <summary>Read every file under the given paths (a file, or a directory read recursively).</summary>
    public static UsedMacros Read(IEnumerable<string> paths)
    {
        var u = new UsedMacros();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            IEnumerable<string> files = Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                : new[] { p };
            foreach (var f in files) u.ReadOne(f, seen);
        }
        return u;
    }

    private void ReadOne(string path, HashSet<string> seen)
    {
        Files++;
        string? cwd = null, source = null, last = null;
        var macros = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[256];
        using (var r = new StreamReader(path, Encoding.Latin1))
        {
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                last = line;
                if (line[0] != '#') continue;
                if (bytes.Length <= line.Length) bytes = new byte[line.Length * 2 + 1];
                var n = Encoding.Latin1.GetBytes(line, bytes);
                bytes[n] = (byte)'\n';
                sha.AppendData(bytes, 0, n + 1);
                if (line.StartsWith(CwdPrefix, StringComparison.Ordinal)) { cwd ??= line[CwdPrefix.Length..].Trim(); continue; }
                if (MacroNameAt(line, out var at, out var length))
                {
                    Names.Add(CodeCarver.Core.Util.NameKey.Short(line.AsSpan(at, length)));
                    macros++;
                    continue;
                }
                if (Marker(line) is { } m)
                {
                    // gcc -fworking-directory prints the directory it ran in as a marker ending in "//".
                    if (m.EndsWith("//", StringComparison.Ordinal)) cwd ??= m[..^2];
                    else if (source is null && !m.StartsWith('<')) source = m;
                }
            }
        }
        if (last != OkLine) { Incomplete++; return; }
        if (source is null) { NoSource++; return; }
        if (macros == 0) { NoMacros++; return; }
        if (!seen.Add(Convert.ToHexString(sha.GetHashAndReset()))) { Duplicates++; return; }
        _units.Add((cwd, source));
    }

    /// <summary>The name of a <c>#define NAME…</c> or <c>#undef NAME</c> line as <c>-dU</c> prints it, or null.</summary>
    internal static string? MacroName(string line) => MacroNameAt(line, out var start, out var length) ? line.Substring(start, length) : null;

    private static bool MacroNameAt(string line, out int start, out int length)
    {
        start = length = 0;
        var i = 1;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        int at;
        if (string.CompareOrdinal(line, i, "define", 0, 6) == 0) at = i + 6;
        else if (string.CompareOrdinal(line, i, "undef", 0, 5) == 0) at = i + 5;
        else return false;
        if (at >= line.Length || line[at] is not (' ' or '\t')) return false;
        while (at < line.Length && line[at] is ' ' or '\t') at++;
        start = at;
        while (at < line.Length && (char.IsAsciiLetterOrDigit(line[at]) || line[at] == '_')) at++;
        length = at - start;
        return length > 0 && !char.IsAsciiDigit(line[start]);
    }

    /// <summary>The file of a line marker (<c># 12 "path" 1 3</c>, or <c>#line 12 "path"</c>), unescaped, or null.</summary>
    internal static string? Marker(string line)
    {
        var i = 1;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        if (string.CompareOrdinal(line, i, "line", 0, 4) == 0) i += 4;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        var digits = i;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == digits) return null;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        if (i >= line.Length || line[i] != '"') return null;
        var sb = new StringBuilder();
        for (i++; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i + 1 < line.Length) c = line[++i];
            sb.Append(c);
        }
        return null;
    }

    /// <summary>How far the files cover the build.</summary>
    public sealed record Coverage(int Commands, int Covered, int UnitsWithoutCommand)
    {
        public bool Complete => Commands > 0 && Covered == Commands && UnitsWithoutCommand == 0;
    }

    /// <summary>
    /// Match the complete files to the build log's commands by source file. A file's source is resolved against the
    /// directory it names (a <c>#codecarver-cwd</c> line, or gcc's working-directory marker) and mapped like the log's
    /// paths; one naming no directory matches by the source path as the log spelled it, when that names one file. Each
    /// command needs a file of its own: a source compiled twice (two configurations) needs two.
    /// </summary>
    /// <param name="root">The carve root, which relative command directories are under.</param>
    /// <param name="map">The carve's pathMap, applied to an absolute path.</param>
    /// <param name="unitsWithoutCommand">Translation units the build compiles that no command covers.</param>
    public Coverage Check(IReadOnlyList<CompileCommand> commands, string root, Func<string, string> map, int unitsWithoutCommand)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var need = new Dictionary<string, int>(cmp);
        var bySpelling = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var c in commands)
        {
            if (Full(c.Directory, c.File, root) is not { } full) continue;
            need[full] = need.GetValueOrDefault(full) + 1;
            var spelled = c.File.Replace('\\', '/');
            if (!bySpelling.TryGetValue(spelled, out var s)) bySpelling[spelled] = s = new HashSet<string>(cmp);
            s.Add(full);
        }
        var have = new Dictionary<string, int>(cmp);
        foreach (var (cwd, source) in _units)
        {
            string? full;
            if (IsAbsolute(source)) full = Normalize(map(source));
            else if (cwd is not null) full = Normalize(map(cwd.TrimEnd('/', '\\') + "/" + source));
            else full = bySpelling.TryGetValue(source.Replace('\\', '/'), out var s) && s.Count == 1 ? s.First() : null;
            if (full is not null) have[full] = have.GetValueOrDefault(full) + 1;
        }
        var covered = need.Sum(kv => Math.Min(kv.Value, have.GetValueOrDefault(kv.Key)));
        return new Coverage(commands.Count, covered, unitsWithoutCommand);
    }

    private static bool IsAbsolute(string p) => p.StartsWith('/') || p.StartsWith('\\') || Path.IsPathFullyQualified(p);

    private static string? Normalize(string p)
    {
        // A Linux path the pathMap didn't map is rooted, not qualified, on Windows: both sides normalize it alike.
        try { return Path.IsPathRooted(p) ? Path.GetFullPath(p) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? Full(string directory, string file, string root)
    {
        try
        {
            var bd = Path.IsPathRooted(directory) ? directory : Path.Combine(root, directory);
            return Normalize(Path.IsPathRooted(file) ? file : Path.Combine(bd, file));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
