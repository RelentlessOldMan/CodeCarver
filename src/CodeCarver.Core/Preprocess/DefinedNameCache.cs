using System.Text;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// What <see cref="DefinedNameScan"/> found in each big header, kept from one run to the next: a header of hundreds of
/// MB on a network share takes minutes to read, and a rerun over the same tree read every one again to learn the same
/// few names. An entry holds a file's size, last-write time and the wanted names it #defines or #undefs; it is used only
/// while the file's size and last-write time are unchanged (as make decides a file is unchanged).
/// <para>The names are the scan's answer for the set of wanted names the run asked with, which the cache stores once.
/// A later run that wants a subset of those gets the answer filtered to its set; one that wants a name the earlier run
/// did not reads every file again. A cache that can't be read, or was written by another format, is ignored.</para>
/// </summary>
public sealed class DefinedNameCache
{
    const string Magic = "codecarver-defined-names-1";

    readonly HashSet<string> _wanted;
    readonly Dictionary<string, (long Size, long Ticks, string[] Names)> _old = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long Size, long Ticks, string[] Names)> _new = new(StringComparer.Ordinal);

    /// <summary>The cache at <paramref name="path"/> (none yet, or unreadable: empty), for a run that wants
    /// <paramref name="wanted"/>.</summary>
    public DefinedNameCache(string path, HashSet<string> wanted)
    {
        _wanted = wanted;
        try
        {
            if (!File.Exists(path)) return;
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (r.ReadString() != Magic) return;
            var asked = new HashSet<string>(StringComparer.Ordinal);
            for (var n = r.ReadInt32(); n > 0; n--) asked.Add(r.ReadString());
            // An answer for other names than this run's: usable only when this run wants no name that run didn't.
            if (!wanted.IsSubsetOf(asked)) return;
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var rel = r.ReadString();
                long size = r.ReadInt64(), ticks = r.ReadInt64();
                var names = new string[r.ReadInt32()];
                for (var k = 0; k < names.Length; k++) names[k] = r.ReadString();
                _old[rel] = (size, ticks, names);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException
                                   or ArgumentOutOfRangeException or OverflowException)
        {
            _old.Clear();
        }
    }

    /// <summary>The wanted names <paramref name="rel"/> defines, as of the last run, when the file still has
    /// <paramref name="size"/> bytes and last-write time <paramref name="ticks"/>; otherwise null (read it).</summary>
    public List<string>? TryGet(string rel, long size, long ticks)
    {
        if (!_old.TryGetValue(rel, out var e) || e.Size != size || e.Ticks != ticks) return null;
        var names = e.Names.Where(_wanted.Contains).ToList();
        lock (_new) _new[rel] = (size, ticks, names.ToArray());
        return names;
    }

    /// <summary>Records what this run read from <paramref name="rel"/>.</summary>
    public void Put(string rel, long size, long ticks, IEnumerable<string> names)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
        lock (_new) _new[rel] = (size, ticks, distinct);
    }

    /// <summary>Writes this run's entries (only the files it asked about) to <paramref name="path"/>, whole or not at
    /// all. False when it could not be written; a run carries on without it.</summary>
    public bool Save(string path)
    {
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(_wanted.Count);
                foreach (var name in _wanted.Order(StringComparer.Ordinal)) w.Write(name);
                w.Write(_new.Count);
                foreach (var (rel, e) in _new.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    w.Write(rel); w.Write(e.Size); w.Write(e.Ticks);
                    w.Write(e.Names.Length);
                    foreach (var name in e.Names) w.Write(name);
                }
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
