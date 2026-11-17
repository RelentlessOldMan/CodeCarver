using System.Text;

namespace CodeCarver.Core.Frontend;

/// <summary>The member names of an <c>ar</c> archive (GNU, BSD and thin; MSVC .lib is the same format), read from the
/// member headers alone: the bodies are skipped, not read.</summary>
public static class ArchiveMembers
{
    /// <summary>The members' file names, or null when <paramref name="path"/> can't be read or isn't an archive.</summary>
    public static List<string>? Names(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            var magic = new byte[8];
            if (s.Read(magic, 0, 8) != 8) return null;
            var m = Encoding.ASCII.GetString(magic);
            var thin = m == "!<thin>\n";
            if (!thin && m != "!<arch>\n") return null;
            var names = new List<string>();
            string? longNames = null;
            var head = new byte[60];
            while (s.Position < s.Length)
            {
                if (s.Read(head, 0, 60) != 60) break;
                var h = Encoding.ASCII.GetString(head);
                if (h[58] != '`' || h[59] != '\n') return null;
                if (!long.TryParse(h.AsSpan(48, 10).Trim(), out var size) || size < 0) return null;
                var name = h[..16].TrimEnd();
                var body = s.Position;
                // A thin archive holds no member bodies, only its symbol and name tables.
                var stored = !thin || name is "/" or "//" or "/SYM64/";
                if (name == "//")
                {
                    var t = new byte[size];
                    if (s.Read(t, 0, t.Length) != t.Length) return null;
                    longNames = Encoding.UTF8.GetString(t);
                }
                else if (name.StartsWith("#1/", StringComparison.Ordinal) && int.TryParse(name.AsSpan(3), out var n) && n > 0 && n <= size)
                {
                    var t = new byte[n];
                    if (s.Read(t, 0, n) != n) return null;
                    names.Add(Encoding.UTF8.GetString(t).TrimEnd('\0'));
                }
                else if (name is "/" or "/SYM64/" or "__.SYMDEF" or "__.SYMDEF SORTED") { }
                else if (name.Length > 1 && name[0] == '/' && int.TryParse(name.AsSpan(1), out var at))
                {
                    if (longNames is not null && at < longNames.Length)
                    {
                        var end = longNames.IndexOf('\n', at);
                        names.Add(longNames[at..(end < 0 ? longNames.Length : end)].TrimEnd('/'));
                    }
                }
                else names.Add(name.TrimEnd('/'));
                if (stored) s.Position = body + size + (size & 1);
            }
            return names;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
