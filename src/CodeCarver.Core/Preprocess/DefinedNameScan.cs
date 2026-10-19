using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Core.Util;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// The names a header #defines or #undefs, kept only when some #if tests them, read without parsing: what the big and
/// dense headers the parser skips can change in a #if. A header of hundreds of MB on a network share took minutes
/// read one buffer after another on one thread, and a regex per line was most of the CPU: this reads a big file in
/// pieces at once and matches the common line by hand. Any line with a byte outside ASCII, and any file in UTF-16 or
/// UTF-32, goes through <see cref="DirectiveLine"/> on the decoded text, so the names are the ones the regex finds
/// line by line.
/// </summary>
public sealed class DefinedNameScan
{
    /// <summary>A #define or #undef at the start of a line, in any directive spelling; group 1 is the name.</summary>
    public static readonly Regex DirectiveLine = new(@"^\s*" + SourceText.DirectiveStart + @"\s*(?:define|undef)\s+([A-Za-z_]\w*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    readonly HashSet<string> _wanted;
    readonly HashSet<int> _hashes = new();

    public DefinedNameScan(HashSet<string> wanted)
    {
        _wanted = wanted;
        foreach (var w in wanted) _hashes.Add(string.GetHashCode(w.AsSpan()));
    }

    /// <summary>How many bytes one read takes. A file of more than two pieces is read several pieces at once.</summary>
    public int PieceBytes { get; init; } = 1 << 20;

    /// <summary>How many pieces of one file are read at once.</summary>
    public int PiecesAtOnce { get; init; } = 16;

    /// <summary>The wanted names <paramref name="path"/> #defines or #undefs, in no set order and possibly repeated; null
    /// when it can't be read (then any of them might come from it).</summary>
    public List<string>? Read(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = RandomAccess.GetLength(h);
            IoMeter.Took(path, length);
            var head = new byte[4];
            var got = ReadFully(h, head, 0);
            // UTF-16 and UTF-32 (byte-order marks FF FE, FE FF, 00 00 FE FF): decoded as the line reader decodes them.
            if (got >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF)
                || got == 4 && head[0] == 0 && head[1] == 0 && head[2] == 0xFE && head[3] == 0xFF)
                return ReadDecoded(path);
            long start = got >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
            var found = new List<string>();
            var pieces = (length - start + PieceBytes - 1) / PieceBytes;
            if (pieces <= 2)
            {
                ScanRange(h, start, start, length, length, found);
                return found;
            }
            Parallel.For(0L, pieces, new ParallelOptions { MaxDegreeOfParallelism = PiecesAtOnce }, k =>
            {
                var mine = new List<string>();
                ScanRange(h, start, start + k * PieceBytes, Math.Min(length, start + (k + 1) * PieceBytes), length, mine);
                if (mine.Count > 0) lock (found) found.AddRange(mine);
            });
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is IOException or UnauthorizedAccessException)) { return null; }
    }

    /// <summary>The names on the lines that start in [from, to): a line starts at the file's first byte (after a UTF-8
    /// byte-order mark) or after a CR or LF.</summary>
    void ScanRange(Microsoft.Win32.SafeHandles.SafeFileHandle h, long fileStart, long from, long to, long length, List<string> found)
    {
        // One byte before (is this piece's first byte a line start?) and a little after (the last line's directive).
        const int Spill = 4096;
        var at = from > fileStart ? from - 1 : from;
        var end = Math.Min(length, to + Spill);
        var buf = new byte[end - at];
        var n = ReadFully(h, buf, at);
        var b = buf.AsSpan(0, n);
        var i = from > fileStart ? 1 : 0;
        if (from > fileStart && b.Length > 0 && b[0] is not ((byte)'\r' or (byte)'\n'))
            i = Next(b, i);
        var own = (int)(to - at);
        while (i < own && i < b.Length)
        {
            var lineEnd = b[i..].IndexOfAny((byte)'\r', (byte)'\n');
            var complete = lineEnd >= 0 || at + b.Length == length;
            ReadOnlySpan<byte> line = lineEnd >= 0 ? b.Slice(i, lineEnd) : b[i..];
            var (state, nameAt, nameLength) = Match(line);
            if (state == State.Undecided && !complete)
            {
                // A line whose directive runs past the spill: read it whole.
                line = WholeLine(h, at + i, length);
                (state, nameAt, nameLength) = Match(line);
            }
            // The whole line was read: a name that runs to its end is the name.
            if (state == State.Undecided && nameLength > 0) state = State.Name;
            if (state == State.Name) Keep(line.Slice(nameAt, nameLength), found);
            else if (state == State.Other) KeepDecoded(line, found);
            if (lineEnd < 0) break;
            i += lineEnd + 1;
        }
    }

    // Past the end of the line that i is in.
    static int Next(ReadOnlySpan<byte> b, int i)
    {
        var e = b[i..].IndexOfAny((byte)'\r', (byte)'\n');
        return e < 0 ? b.Length : i + e + 1;
    }

    static byte[] WholeLine(Microsoft.Win32.SafeHandles.SafeFileHandle h, long from, long length)
    {
        using var line = new MemoryStream();
        var piece = new byte[64 * 1024];
        for (var at = from; at < length;)
        {
            var n = ReadFully(h, piece, at);
            if (n == 0) break;
            var e = piece.AsSpan(0, n).IndexOfAny((byte)'\r', (byte)'\n');
            line.Write(piece, 0, e < 0 ? n : e);
            if (e >= 0) break;
            at += n;
        }
        return line.ToArray();
    }

    void Keep(ReadOnlySpan<byte> name, List<string> found)
    {
        Span<char> chars = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        for (var k = 0; k < name.Length; k++) chars[k] = (char)name[k];
        if (!_hashes.Contains(string.GetHashCode(chars))) return;
        var s = new string(chars);
        if (_wanted.Contains(s)) found.Add(s);
    }

    void KeepDecoded(ReadOnlySpan<byte> line, List<string> found)
    {
        var m = DirectiveLine.Match(Encoding.UTF8.GetString(line));
        if (m.Success && _wanted.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
    }

    List<string> ReadDecoded(string path)
    {
        var found = new List<string>();
        foreach (var line in IoMeter.ReadLines(path))
        {
            var m = DirectiveLine.Match(line);
            if (m.Success && _wanted.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, long at)
    {
        using var _ = IoMeter.Start(IoMeter.Kind.Read);
        var total = 0;
        while (total < buf.Length)
        {
            var n = RandomAccess.Read(h, buf.AsSpan(total), at + total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Name: an ASCII directive, the name at [at, at+length). None: no directive. Other: a byte outside ASCII
    /// before it was decided, so the regex decides. Undecided: the line ran out before it was decided.</summary>
    public enum State { None, Name, Other, Undecided }

    /// <summary>The <see cref="DirectiveLine"/> match on one line of bytes (no CR or LF in it), for ASCII.</summary>
    public static (State State, int At, int Length) Match(ReadOnlySpan<byte> l)
    {
        var i = 0;
        var e = l.Length;
        if (!Spaces(l, ref i, out var st)) return (st, 0, 0);
        if (i >= e) return (State.Undecided, 0, 0);
        if (l[i] == '#') i++;
        else if (l[i] == '%') { if (i + 1 >= e) return (State.Undecided, 0, 0); if (l[i + 1] != ':') return (State.None, 0, 0); i += 2; }
        else if (l[i] == '?')
        {
            if (i + 2 >= e) return (State.Undecided, 0, 0);
            if (l[i + 1] != '?' || l[i + 2] != '=') return (State.None, 0, 0);
            i += 3;
        }
        else return (l[i] >= 0x80 ? State.Other : State.None, 0, 0);
        if (!Spaces(l, ref i, out st)) return (st, 0, 0);
        var word = l[i..];
        if (word.StartsWith("define"u8)) i += 6;
        else if (word.StartsWith("undef"u8)) i += 5;
        else
        {
            // A prefix of a keyword at the end of what was read may still be one.
            if (word.Length < 6 && ("define"u8.StartsWith(word) || "undef"u8.StartsWith(word))) return (State.Undecided, 0, 0);
            return (State.None, 0, 0);
        }
        var before = i;
        if (!Spaces(l, ref i, out st)) return (st, 0, 0);
        if (i >= e) return (State.Undecided, 0, 0);
        if (i == before) return (l[i] >= 0x80 ? State.Other : State.None, 0, 0);
        var c = l[i];
        if (c >= 0x80) return (State.Other, 0, 0);
        if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_')) return (State.None, 0, 0);
        var at = i++;
        for (; i < e; i++)
        {
            c = l[i];
            if (c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_') continue;
            if (c >= 0x80) return (State.Other, 0, 0);
            return (State.Name, at, i - at);
        }
        // The name runs to the end of what was read: it may go on.
        return (State.Undecided, at, i - at);
    }

    // \s in ASCII: tab, LF, VT, FF, CR, space. Stops at a byte outside ASCII (it may be a space the regex knows).
    static bool Spaces(ReadOnlySpan<byte> l, ref int i, out State st)
    {
        while (i < l.Length && l[i] is 9 or 10 or 11 or 12 or 13 or 32) i++;
        st = State.Other;
        return i >= l.Length || l[i] < 0x80;
    }
}
