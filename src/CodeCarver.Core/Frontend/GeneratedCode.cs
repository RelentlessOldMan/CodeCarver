using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// Calls made by code the BUILD writes: a source template (<c>table.c.in</c>, filled in by sed or configure) or a
/// generator script that prints C (a here-document, a Python string) into the build directory, outside the tree.
/// The generated file is gone by carve time and lives outside the root anyway, so the call to a tree function it
/// makes is visible only here. Those names are rooted, and verify counts them as uses.
/// <para>A template is C with placeholders, so all of it is read. A script is read only inside its string literals
/// and here-documents: the script's own calls (<c>open(</c>, <c>print(</c>) are not C.</para>
/// </summary>
public static class GeneratedCode
{
    static readonly Regex Call = new(@"(?<![\w$.>])([A-Za-z_][\w$]*)\s*\(", RegexOptions.Compiled);
    static readonly Regex HereDoc = new(@"<<-?\s*(?:'(\w+)'|""(\w+)""|\\?(\w+))", RegexOptions.Compiled);
    static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
        { "if", "while", "for", "switch", "return", "sizeof", "defined", "_Alignof", "alignof", "typeof", "__typeof__",
          "_Generic", "_Static_assert", "__attribute__", "__declspec", "__asm__", "asm", "do", "else", "case" };

    static readonly string[] TemplateSuffixes = { ".in", ".tmpl", ".template", ".tpl", ".j2", ".jinja", ".jinja2", ".mako" };
    static readonly string[] CodeExtensions = { ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx", ".inc", ".inl" };

    /// <summary>A C/C++ source template: <c>name.c.in</c>, <c>config.h.in</c>, <c>table.c.j2</c>.</summary>
    public static bool IsTemplate(string rel)
    {
        var name = Path.GetFileName(rel).ToLowerInvariant();
        foreach (var t in TemplateSuffixes)
            if (name.EndsWith(t, StringComparison.Ordinal))
            {
                var inner = name[..^t.Length];
                return CodeExtensions.Any(e => inner.EndsWith(e, StringComparison.Ordinal));
            }
        return false;
    }

    /// <summary>The functions <paramref name="text"/> calls in the code it generates, with the 1-based line.</summary>
    public static List<(string Name, int Line)> Calls(string text, bool template)
    {
        var found = new List<(string, int)>();
        if (string.IsNullOrEmpty(text) || !text.Contains('(')) return found;
        var lines = text.Split('\n');
        string? hereEnd = null;
        string? triple = null;   // an open """ or ''' (Python)
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (template) { Add(line, i); continue; }
            if (hereEnd is not null)
            {
                if (line.Trim() == hereEnd) hereEnd = null;
                else Add(line, i);
                continue;
            }
            var code = new System.Text.StringBuilder();
            var j = 0;
            if (triple is not null)
            {
                var end = line.IndexOf(triple, StringComparison.Ordinal);
                if (end < 0) { Add(line, i); continue; }
                Add(line[..end], i);
                j = end + 3;
                triple = null;
            }
            for (; j < line.Length; j++)
            {
                var c = line[j];
                if (c == '#' && (j == 0 || char.IsWhiteSpace(line[j - 1]))) break;   // a shell/Python/make comment
                if (c is '"' or '\'')
                {
                    if (j + 2 < line.Length && line[j + 1] == c && line[j + 2] == c)
                    {
                        var q = new string(c, 3);
                        var end = line.IndexOf(q, j + 3, StringComparison.Ordinal);
                        if (end < 0) { Add(line[(j + 3)..], i); triple = q; break; }
                        Add(line[(j + 3)..end], i);
                        j = end + 2;
                        continue;
                    }
                    var k = j + 1;
                    while (k < line.Length && line[k] != c) k += line[k] == '\\' && c == '"' ? 2 : 1;
                    Add(line[(j + 1)..Math.Min(k, line.Length)], i);
                    j = k;
                    continue;
                }
                code.Append(c);
            }
            var hd = HereDoc.Match(code.ToString());
            if (hd.Success && triple is null)
                hereEnd = hd.Groups[1].Success ? hd.Groups[1].Value : hd.Groups[2].Success ? hd.Groups[2].Value : hd.Groups[3].Value;
        }
        return found;

        void Add(string s, int line)
        {
            if (!s.Contains('(')) return;
            foreach (Match m in Call.Matches(s))
                if (!NotCalls.Contains(m.Groups[1].Value)) found.Add((m.Groups[1].Value, line + 1));
        }
    }
}
