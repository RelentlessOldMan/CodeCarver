using System.Text.RegularExpressions;

namespace CodeCarver.Core.Frontend;

/// <summary>
/// GNU attribute blocks in C text and the name each decorates, shared by the front end (keep attributes) and the
/// linker-section root provider. Accepts every spelling compilers take: <c>__attribute__((..))</c>,
/// <c>__attribute((..))</c>, <c>[[gnu::..]]</c>; arguments nested to any depth (<c>aligned(sizeof(void *))</c>); and the
/// reserved-name forms (<c>__constructor__</c>, <c>__section__</c>), which the body comes back normalised from, so a
/// pattern written for <c>constructor</c> or <c>section(</c> matches either.
/// </summary>
public static class Attributes
{
    public readonly record struct Block(int Index, int Length, string Body);

    // `__name__` -> `name` outside string literals (a section name keeps its underscores).
    private static readonly Regex Reserved = new(@"""(?:[^""\\]|\\.)*""|(?<![\w])__([A-Za-z]\w*?)__(?![\w])", RegexOptions.Compiled);
    private static readonly Regex GnuScope = new(@"(?<![\w:])(?:__gnu__|gnu)\s*::\s*", RegexOptions.Compiled);
    private const int MaxBody = 4096;

    /// <summary>Every attribute block, in text order.</summary>
    // A parameter list with any nesting: `(void (*cb)(int))`.
    private const string Params = @"\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)";
    private const string AliasWord = @"\b(?:alias|ifunc|weakref)\s*\(\s*""(?<target>[A-Za-z_]\w*)""";

    /// <summary>
    /// <c>void h(void) __attribute__((weak, alias("target")))</c>, <c>int v __attribute__((alias("w")))</c>,
    /// <c>static int p(void) __attribute__((weakref("impl")))</c>: <c>name</c> is defined as (or refers to)
    /// <c>target</c>. An ifunc names its resolver (reviews L1, L2).
    /// </summary>
    public const string AliasAfterPattern =
        @"(?<name>(?!__attribute__\b)[A-Za-z_]\w*)\s*(?:" + Params + @"\s*)?__attribute__\s*\(\([^;{}]*?" + AliasWord;

    /// <summary>The attribute first: <c>__attribute__((alias("impl"))) void hook(void);</c>.</summary>
    public const string AliasBeforePattern =
        @"__attribute__\s*\(\([^;{}]*?" + AliasWord + @"\s*\)[^;{}]*?\)\)[\w\s\*]*?\b(?<name>[A-Za-z_]\w*)\s*(?:" + Params + @"\s*)?;";

    public static IEnumerable<Block> Blocks(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && string.CompareOrdinal(text, i, "__attribute", 0, 11) == 0 && (i == 0 || !IsWord(text[i - 1])))
            {
                var p = i + 11;
                if (p + 1 < text.Length && text[p] == '_' && text[p + 1] == '_') p += 2;
                if (p < text.Length && IsWord(text[p])) continue;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                if (p >= text.Length || text[p] != '(') continue;
                var close = Matching(text, p, '(', ')');
                if (close < 0) continue;
                var inner = text.AsSpan(p + 1, close - p - 1).Trim();
                if (inner.Length < 2 || inner[0] != '(' || inner[^1] != ')') continue;
                yield return new Block(i, close + 1 - i, Normalise(inner[1..^1].ToString()));
                i = close;
            }
            else if (text[i] == '[' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var close = Matching(text, i, '[', ']');
                if (close < 0 || text[close - 1] != ']') continue;
                var body = text.AsSpan(i + 2, close - 1 - (i + 2)).Trim().ToString();
                if (!GnuScope.IsMatch(body)) continue;
                yield return new Block(i, close + 1 - i, Normalise(GnuScope.Replace(body, "")));
                i = close;
            }
        }
    }

    /// <summary>The name a block at <paramref name="index"/>..<paramref name="end"/> decorates: the declarator just
    /// before it (<c>void f(void) __attribute__</c>, <c>void (*const p)(void) __attribute__</c>), else the one just
    /// after it (<c>__attribute__ .. int x</c>, <c>__attribute__ .. void (* const v[])(void)</c>).</summary>
    public static string? DecoratedName(string text, int index, int end)
    {
        var before = text.AsSpan(0, index);
        var b = (before.Length > 200 ? before[^200..] : before).ToString();
        if (FnPtrBefore.Match(b) is { Success: true } fp) return fp.Groups[1].Value;
        if (NameBefore.Match(b) is { Success: true } nb && !NotAName.Contains(nb.Groups[1].Value)) return nb.Groups[1].Value;
        var after = text.AsSpan(end);
        var a = (after.Length > 200 ? after[..200] : after).ToString();
        if (FnPtrAfter.Match(a) is { Success: true } fa) return fa.Groups[1].Value;
        if (NameAfter.Match(a) is { Success: true } na) return na.Groups[1].Value;
        return null;
    }

    /// <summary>The declarator name just before a position (<see cref="DecoratedName"/>'s first half), for text that
    /// decorates like an attribute (a keep macro).</summary>
    public static string? NameBeforeAt(string before)
    {
        if (FnPtrBefore.Match(before) is { Success: true } fp) return fp.Groups[1].Value;
        if (NameBefore.Match(before) is { Success: true } nb && !NotAName.Contains(nb.Groups[1].Value)) return nb.Groups[1].Value;
        return null;
    }

    /// <summary>The declarator name just after a position (<see cref="DecoratedName"/>'s second half).</summary>
    public static string? NameAfterAt(string after)
    {
        if (FnPtrAfter.Match(after) is { Success: true } fa) return fa.Groups[1].Value;
        if (NameAfter.Match(after) is { Success: true } na) return na.Groups[1].Value;
        return null;
    }

    private const string Quals = @"(?:(?:const|volatile|restrict|__restrict|__restrict__)\s*)*";
    // `(*const name[N])(params)` ending the text: a function pointer (or array of them) declared before the block.
    private static readonly Regex FnPtrBefore = new(
        @"\(\s*\*\s*" + Quals + @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\]\s*)*\)\s*\((?:[^()]|\([^()]*\))*\)\s*$", RegexOptions.Compiled);
    // `type (*const name...` starting the text.
    private static readonly Regex FnPtrAfter = new(
        @"^\s*(?:[A-Za-z_]\w*[\s*]+)*?\(\s*\*\s*" + Quals + @"([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex NameBefore = new( // trailing:  name / name(...) / name[...]
        @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\]|\((?:[^()]|\([^()]*\))*\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex NameAfter = new(  // leading:   ... name( / name[ / name =
        @"^\s*(?:[A-Za-z_][\w*]*[\s*]+)*?([A-Za-z_]\w*)\s*(?:[\(\[=;,]|$)", RegexOptions.Compiled);
    // Words that end a declaration's specifiers, not a declarator: `static __attribute__((used)) int x;` names x.
    private static readonly HashSet<string> NotAName = new(StringComparer.Ordinal)
    {
        "static", "extern", "const", "volatile", "inline", "__inline", "__inline__", "register", "typedef", "void", "char",
        "short", "int", "long", "float", "double", "signed", "unsigned", "struct", "union", "enum", "_Bool", "bool",
        "_Noreturn", "__thread", "_Thread_local", "thread_local", "constexpr", "auto", "__extension__",
    };

    private static string Normalise(string body)
        => body.Contains("__", StringComparison.Ordinal)
            ? Reserved.Replace(body, m => m.Groups[1].Success ? m.Groups[1].Value : m.Value)
            : body;

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Index of the bracket closing the one at <paramref name="open"/>, skipping string and character
    /// literals; -1 if it does not close within <see cref="MaxBody"/> characters.</summary>
    private static int Matching(string text, int open, char o, char c)
    {
        var depth = 0;
        var limit = Math.Min(text.Length, open + MaxBody);
        for (var i = open; i < limit; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                for (i++; i < limit && text[i] != ch; i++) if (text[i] == '\\') i++;
                continue;
            }
            if (ch == o) depth++;
            else if (ch == c && --depth == 0) return i;
            else if (ch is ';' or '{' or '}') return -1;
        }
        return -1;
    }
}
