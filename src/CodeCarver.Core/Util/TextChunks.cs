using System.Text;

namespace CodeCarver.Core.Util;

/// <summary>
/// A file too big to hold whole (past maxParseBytes) that the carve must still read for names (an assembly file's
/// calls, a linker script's KEEPs): its lines, without their <c>/* */</c> comments, in pieces of about
/// <paramref name="chunkChars"/> that end on a line. A comment-heavy file shrinks to its code; the rest is read a piece
/// at a time and never held whole.
/// </summary>
public static class TextChunks
{
    public static IEnumerable<string> WithoutBlockComments(IEnumerable<string> lines, int chunkChars = 4 << 20)
    {
        var sb = new StringBuilder();
        var inComment = false;
        foreach (var line in lines)
        {
            var i = 0;
            while (i < line.Length)
            {
                if (inComment)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) { i = line.Length; break; }
                    inComment = false;
                    i = end + 2;
                    sb.Append(' ');   // a comment is a space, as the preprocessor reads it
                }
                else
                {
                    var open = line.IndexOf("/*", i, StringComparison.Ordinal);
                    if (open < 0) { sb.Append(line, i, line.Length - i); break; }
                    sb.Append(line, i, open - i);
                    inComment = true;
                    i = open + 2;
                }
            }
            sb.Append('\n');
            if (sb.Length >= chunkChars) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
