namespace CodeCarver.Core.Util;

/// <summary>
/// A fixed set of names, looked up by a span of text: a scan asks whether each name it meets is in the set without
/// making a string of it first. Read-only once built, so any number of threads may look up at once.
/// </summary>
public sealed class NameSet
{
    readonly string?[] _slots;

    public NameSet(IEnumerable<string> names)
    {
        var list = names as ICollection<string> ?? names.ToList();
        var size = 16;
        while (size < list.Count * 2) size *= 2;
        _slots = new string?[size];
        foreach (var name in list)
        {
            var i = Slot(name);
            if (_slots[i] is null) _slots[i] = name;
        }
    }

    /// <summary>The set's own string equal to <paramref name="name"/>, or null when it isn't in the set.</summary>
    public string? Find(ReadOnlySpan<char> name) => _slots[Slot(name)];

    // The slot holding the name, or the empty slot where it would go.
    int Slot(ReadOnlySpan<char> name)
    {
        var mask = _slots.Length - 1;
        var i = string.GetHashCode(name) & mask;
        while (_slots[i] is { } have && !name.SequenceEqual(have)) i = (i + 1) & mask;
        return i;
    }
}
