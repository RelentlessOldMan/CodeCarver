namespace CodeCarver.Frontend;

/// <summary>
/// A text's lines as <c>text.Split('\n')</c> gives them (a "\r" stays on its line), but made only when asked for: the
/// parse wants a few lines of each file (a global's span, a directive), and splitting every file made a second copy of
/// the whole tree in line strings. Holds where each line starts.
/// </summary>
internal sealed class TextLines
{
    readonly string _text;
    readonly int[] _starts;

    public TextLines(string text)
    {
        _text = text;
        var n = 1;
        foreach (var c in text.AsSpan()) if (c == '\n') n++;
        _starts = new int[n];
        for (int i = 0, line = 1; i < text.Length; i++) if (text[i] == '\n') _starts[line++] = i + 1;
    }

    public int Length => _starts.Length;

    /// <summary>The 1-based line holding <paramref name="offset"/> (1 + the newlines before it; past the end, the last
    /// line): a search over the line starts, where counting from offset 0 cost a pass over the text per match.</summary>
    public int LineOf(int offset)
    {
        var i = Array.BinarySearch(_starts, Math.Clamp(offset, 0, _text.Length));
        return i >= 0 ? i + 1 : ~i;
    }

    public string this[int line] => _text.Substring(_starts[line], End(line) - _starts[line]);

    /// <summary>The line's characters, without making a string.</summary>
    public ReadOnlySpan<char> Span(int line) => _text.AsSpan(_starts[line], End(line) - _starts[line]);

    int End(int line) => line + 1 < _starts.Length ? _starts[line + 1] - 1 : _text.Length;
}
