using System.Text;

namespace CodeCarver.Frontend;

/// <summary>
/// C's digraphs, <c>&lt;% %&gt; &lt;: :&gt; %:</c>, spelled as the tokens they stand for (<c>{ } [ ] #</c>), for PARSING
/// only. tree-sitter doesn't know them, so <c>int f(void) &lt;% ... %&gt;</c> was no definition and its file was dropped.
/// Length-preserving (each two-character digraph becomes its token plus a space), and nothing inside a string, a
/// character constant or a comment is touched. C++'s <c>&lt;::</c> rule is honoured: <c>&lt;::</c> not followed by
/// <c>:</c> or <c>&gt;</c> is <c>&lt; ::</c>.
/// </summary>
public static class Digraphs
{
    public static string Rewrite(string text)
    {
        if (!(text.Contains("<%", StringComparison.Ordinal) || text.Contains("%>", StringComparison.Ordinal)
              || text.Contains("<:", StringComparison.Ordinal) || text.Contains(":>", StringComparison.Ordinal)
              || text.Contains("%:", StringComparison.Ordinal)))
            return text;
        var sb = new StringBuilder(text);
        var changed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i++;
                continue;
            }
            if (c is '"' or '\'')
            {
                i++;
                while (i < text.Length && text[i] != c && text[i] != '\n') { if (text[i] == '\\') i++; i++; }
                continue;
            }
            if (i + 1 >= text.Length) break;
            var two = text.Substring(i, 2);
            var to = two switch { "<%" => '{', "%>" => '}', "<:" => '[', ":>" => ']', "%:" => '#', _ => '\0' };
            if (to == '\0') continue;
            if (two == "<:" && i + 2 < text.Length && text[i + 2] == ':' && (i + 3 >= text.Length || text[i + 3] is not (':' or '>')))
                continue;
            sb[i] = to; sb[i + 1] = ' ';
            changed = true;
            i++;
        }
        return changed ? sb.ToString() : text;
    }
}
