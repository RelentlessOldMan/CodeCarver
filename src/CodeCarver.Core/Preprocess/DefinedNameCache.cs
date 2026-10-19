using System.Security.Cryptography;
using System.Text;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// What <see cref="DefinedNameScan"/> found in each big header, kept from one run to the next: a header of hundreds of
/// MB on a network share takes minutes to read, and a rerun over the same tree read every one again to learn the same
/// few names. An entry holds a file's size, last-write time and the wanted names it #defines or #undefs; it is used only
/// while the file's size and last-write time are unchanged (as make decides a file is unchanged).
/// <para>Each entry is the scan's answer for the set of wanted names the run that read it asked with. A later run that
/// wants a subset of that set gets the answer filtered to its own; one that wants a name that run did not ask about
/// reads the file again, and the two answers are kept as one, for both sets. A save keeps the entries this run did not
/// touch, so carves of one tree with different settings don't empty the cache for each other. A cache that can't be read, or was written by another format, is
/// ignored.</para>
/// <para>One cache per tree: <see cref="TreeDirectory"/> gives each tree its own folder under the configured cache
/// directory, so files of two trees with the same relative path, size and time never answer for each other.</para>
/// </summary>
public sealed class DefinedNameCache
{
    const string Magic = "codecarver-defined-names-2";

    sealed record Entry(long Size, long Ticks, string[] Names, HashSet<string> Asked);

    readonly HashSet<string> _wanted;
    readonly Dictionary<string, Entry> _old = new(StringComparer.Ordinal);
    readonly Dictionary<string, Entry> _new = new(StringComparer.Ordinal);

    /// <summary>The folder under <paramref name="cacheRoot"/> for the tree at <paramref name="tree"/> (the tree a mirror
    /// copies, or the carve root): its last name and a hash of its full path, compared as the file system compares
    /// paths. The same tree reached by another spelling (a drive letter, a share path) gets another folder.</summary>
    public static string TreeDirectory(string cacheRoot, string tree)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree));
        var key = OperatingSystem.IsWindows() ? full.Replace('/', '\\').ToUpperInvariant() : full;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var leaf = new string(Path.GetFileName(full).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        if (OperatingSystem.IsWindows()) leaf = leaf.ToLowerInvariant();   // one folder for one tree, however it is cased
        return Path.Combine(Path.GetFullPath(cacheRoot), (leaf.Length == 0 ? "root" : leaf) + "-" + hash);
    }

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
            var sets = new HashSet<string>[r.ReadInt32()];
            for (var s = 0; s < sets.Length; s++)
            {
                sets[s] = new HashSet<string>(StringComparer.Ordinal);
                for (var n = r.ReadInt32(); n > 0; n--) sets[s].Add(r.ReadString());
            }
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var rel = r.ReadString();
                long size = r.ReadInt64(), ticks = r.ReadInt64();
                var asked = sets[r.ReadInt32()];
                var names = new string[r.ReadInt32()];
                for (var k = 0; k < names.Length; k++) names[k] = r.ReadString();
                _old[rel] = new Entry(size, ticks, names, asked);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException
                                   or ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException)
        {
            _old.Clear();
        }
    }

    /// <summary>The wanted names <paramref name="rel"/> defines, as of the run that read it, when the file still has
    /// <paramref name="size"/> bytes and last-write time <paramref name="ticks"/> and that run asked about every name
    /// this one wants; otherwise null (read it).</summary>
    public List<string>? TryGet(string rel, long size, long ticks)
    {
        if (!_old.TryGetValue(rel, out var e) || e.Size != size || e.Ticks != ticks || !_wanted.IsSubsetOf(e.Asked)) return null;
        lock (_new) _new[rel] = e;   // kept as it was: a run that wants more of its names later still gets them
        return e.Names.Where(_wanted.Contains).ToList();
    }

    /// <summary>Records what this run read from <paramref name="rel"/>.</summary>
    public void Put(string rel, long size, long ticks, IEnumerable<string> names)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
        // The same file, unchanged, read before for other names: one answer for both sets, so a carve that wants either
        // set next takes it from the cache.
        if (_old.TryGetValue(rel, out var e) && e.Size == size && e.Ticks == ticks && !_wanted.IsSubsetOf(e.Asked))
        {
            var asked = new HashSet<string>(e.Asked, StringComparer.Ordinal);
            asked.UnionWith(_wanted);
            distinct = e.Names.Concat(distinct).Distinct(StringComparer.Ordinal).ToArray();
            lock (_new) _new[rel] = new Entry(size, ticks, distinct, asked);
            return;
        }
        lock (_new) _new[rel] = new Entry(size, ticks, distinct, _wanted);
    }

    /// <summary>Writes this run's entries, and the earlier ones it did not touch, to <paramref name="path"/>, whole or
    /// not at all. False when it could not be written; a run carries on without it.</summary>
    public bool Save(string path)
    {
        var all = new Dictionary<string, Entry>(_old, StringComparer.Ordinal);
        foreach (var (rel, e) in _new) all[rel] = e;
        var sets = new List<HashSet<string>>();
        var setIndex = new Dictionary<HashSet<string>, int>(ReferenceEqualityComparer.Instance);
        int IndexOf(HashSet<string> s)
        {
            if (setIndex.TryGetValue(s, out var i)) return i;
            // An equal set from another run is stored once.
            for (i = 0; i < sets.Count; i++) if (sets[i].SetEquals(s)) { setIndex[s] = i; return i; }
            sets.Add(s); setIndex[s] = sets.Count - 1;
            return sets.Count - 1;
        }
        var rows = all.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value, Set: IndexOf(kv.Value.Asked))).ToList();
        // Unique per run: two runs saving at once each write their own and the last rename wins, whole.
        var tmp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(sets.Count);
                foreach (var s in sets)
                {
                    w.Write(s.Count);
                    foreach (var name in s.Order(StringComparer.Ordinal)) w.Write(name);
                }
                w.Write(rows.Count);
                foreach (var (rel, e, set) in rows)
                {
                    w.Write(rel); w.Write(e.Size); w.Write(e.Ticks); w.Write(set);
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
