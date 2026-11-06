using System.Text.RegularExpressions;
using CodeCarver.Core.Emit;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Roots;

namespace CodeCarver.Core.Reachability;

/// <summary>
/// Line coverage from real runs (<c>[runs.X] runLineCoverage</c>): the source lines the image executed. One location per
/// line of the file: the last <c>path:line</c> or <c>path:line-line</c> on it, so addr2line's <c>-p</c> output
/// (<c>0x08000123: main at src/a.c:12 (discriminator 2)</c>) and a plain <c>src/a.c:12</c> list both read. Line 0 and
/// <c>??</c> (no debug info) are skipped.
/// </summary>
public static class LineCoverage
{
    private static readonly Regex Location = new(@"(?<file>(?:[A-Za-z]:)?[^\s:()]*\.[A-Za-z0-9_+]+):(?<from>\d+)(?:-(?<to>\d+))?",
                                                 RegexOptions.Compiled);
    private static readonly Regex WholeLocation = new(@"^(?<file>\S.*\.[A-Za-z0-9_+]+):(?<from>\d+)(?:-(?<to>\d+))?$", RegexOptions.Compiled);
    private static readonly Regex Discriminator = new(@"\s*\(discriminator \d+\)\s*$", RegexOptions.Compiled);

    public readonly record struct Lines(string File, int From, int To);

    /// <param name="unmatched">Non-blank, non-comment lines with no location (or only line 0).</param>
    public static List<Lines> Parse(IEnumerable<string> lines, out int unmatched)
    {
        unmatched = 0;
        var result = new List<Lines>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // A path may hold spaces (C:\Program Files\...): addr2line puts it after " at ", a plain list has it whole.
            // Anything else: the last space-free location on the line.
            Match? last = null;
            var at = line.LastIndexOf(" at ", StringComparison.Ordinal);
            var whole = WholeLocation.Match(at >= 0 ? Discriminator.Replace(line[(at + 4)..], "") : line);
            if (whole.Success && (at >= 0 || !line.Contains(": ", StringComparison.Ordinal))) last = whole;
            else for (var m = Location.Match(line); m.Success; m = m.NextMatch()) last = m;
            if (last is null || !int.TryParse(last.Groups["from"].Value, out var from) || from < 1) { unmatched++; continue; }
            var to = last.Groups["to"].Success && int.TryParse(last.Groups["to"].Value, out var t) && t >= from && t - from <= 100_000 ? t : from;
            result.Add(new Lines(last.Groups["file"].Value, from, to));
        }
        return result;
    }
}

/// <summary>
/// Cold paths: code the static carve must keep because it <em>can</em> run, which line coverage says never did. A
/// switch whose 1,000 cases all stay because the register could hold any value, when only one case ever ran. The
/// arms are not removed here: this only reports them and what removing them would free (the functions and globals
/// nothing else reaches), so it is known whether the next tier is worth having. It changes no carve.
///
/// An arm is a <c>case</c>/<c>default</c> group of a switch, or a braced <c>if</c>/<c>else</c> block, inside a function
/// that ran. It is cold when none of its lines executed. It is judged only when its lines are its own (no other arm
/// or condition on them); an unbraced branch, or a switch with a label inside a nested block (Duff's device), is
/// counted as not judged.
/// </summary>
public static class ColdPaths
{
    public enum ArmKind { Case, Then, Else }

    public readonly record struct Arm(ArmKind Kind, int StartLine, int EndLine, int Start, int End);

    public sealed record ColdArm(Node Function, ArmKind Kind, int StartLine, int EndLine);

    public sealed class Report
    {
        public int FunctionsRan, FunctionsNeverRan, FunctionsInUncoveredFiles, BodiesUnread;
        public long NeverRanLines;
        public int ArmsJudged, ArmsNotJudged;
        public long ColdLines;
        public List<ColdArm> Cold = new();
        public List<Node> NeverRan = new();
        /// <summary>Functions and globals the kept code reaches only through cold arms.</summary>
        public List<Node> Freed = new();
        public long FreedLines => Freed.Sum(n => (long)(n.Span.IsKnown ? n.Span.EndLine - n.Span.StartLine + 1 : 0));
        public int CoveredFiles, CoverageFilesMatched;
    }

    private static readonly Regex Word = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>The arms of the body between <paramref name="open"/> and <paramref name="close"/> (its braces), and how
    /// many branches could not be judged.</summary>
    public static List<Arm> ArmsOf(StubBodies.Source src, int open, int close, out int notJudged)
    {
        var code = src.Code;
        var arms = new List<Arm>();
        notJudged = 0;
        for (var m = Word.Match(code, open + 1, close - open - 1); m.Success; m = m.NextMatch())
        {
            if (m.Value == "switch") Switch(src, m.Index + m.Length, close, arms, ref notJudged);
            else if (m.Value == "if") If(src, m.Index + m.Length, close, arms, ref notJudged);
        }
        return arms;
    }

    static void If(StubBodies.Source src, int at, int limit, List<Arm> arms, ref int notJudged)
    {
        var code = src.Code;
        at = Skip(code, at, limit);
        if (at >= limit || code[at] != '(') return;
        var cond = Match(code, at, '(', ')', limit);
        if (cond < 0) return;
        at = Skip(code, cond + 1, limit);
        if (at >= limit) return;
        if (code[at] != '{') { notJudged++; return; }
        var then = Match(code, at, '{', '}', limit);
        if (then < 0) return;
        Block(src, ArmKind.Then, at, then, arms, ref notJudged);
        at = Skip(code, then + 1, limit);
        if (!IsWordAt(code, at, "else", limit)) return;
        at = Skip(code, at + 4, limit);
        if (at >= limit || IsWordAt(code, at, "if", limit)) return;   // else-if: its own if
        if (code[at] != '{') { notJudged++; return; }
        var els = Match(code, at, '{', '}', limit);
        if (els >= 0) Block(src, ArmKind.Else, at, els, arms, ref notJudged);
    }

    /// <summary>A braced block, judged on the lines strictly between its braces when each brace ends or starts its line.</summary>
    static void Block(StubBodies.Source src, ArmKind kind, int open, int close, List<Arm> arms, ref int notJudged)
    {
        var code = src.Code;
        if (!Blank(code, open + 1, LineEnd(code, open))) { notJudged++; return; }
        if (!Blank(code, src.LineStart(src.LineOf(close)), close)) { notJudged++; return; }
        int first = src.LineOf(open) + 1, last = src.LineOf(close) - 1;
        if (first > last || Blank(code, open + 1, close)) return;   // nothing in it
        arms.Add(new Arm(kind, first, last, open + 1, close));
    }

    static void Switch(StubBodies.Source src, int at, int limit, List<Arm> arms, ref int notJudged)
    {
        var code = src.Code;
        at = Skip(code, at, limit);
        if (at >= limit || code[at] != '(') return;
        var cond = Match(code, at, '(', ')', limit);
        if (cond < 0) return;
        var open = Skip(code, cond + 1, limit);
        if (open >= limit || code[open] != '{') return;
        var close = Match(code, open, '{', '}', limit);
        if (close < 0) return;

        // The labels directly in this switch: depth 1, outside parentheses, nested switches skipped.
        var labels = new List<(int Start, int AfterColon)>();
        int depth = 0, parens = 0;
        for (var i = open + 1; i < close; i++)
        {
            var c = code[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == '(') parens++;
            else if (c == ')') parens--;
            else if ((c == '_' || char.IsLetter(c)) && (i == 0 || !IsWord(code[i - 1])))
            {
                var end = i;
                while (end < close && IsWord(code[end])) end++;
                var w = code[i..end];
                if (w == "switch")
                {
                    // Skip the nested switch whole: its labels are its own.
                    var p = Skip(code, end, close);
                    var pc = p < close && code[p] == '(' ? Match(code, p, '(', ')', close) : -1;
                    var b = pc >= 0 ? Skip(code, pc + 1, close) : -1;
                    var bc = b >= 0 && b < close && code[b] == '{' ? Match(code, b, '{', '}', close) : -1;
                    if (bc >= 0) { i = bc; continue; }
                }
                else if (w is "case" or "default")
                {
                    var colon = LabelColon(code, end, close);
                    if (colon < 0 || depth != 0 || parens != 0)
                    {
                        if (parens == 0) { notJudged++; return; }   // a label in a nested block: not judged
                    }
                    else { labels.Add((i, colon + 1)); i = colon; continue; }
                }
                i = end - 1;
            }
        }
        if (labels.Count == 0) return;

        // Arms: labels with nothing between them are one arm.
        for (var k = 0; k < labels.Count; k++)
        {
            var start = labels[k].Start;
            while (k + 1 < labels.Count && Blank(code, labels[k].AfterColon, labels[k + 1].Start)) k++;
            var bodyStart = labels[k].AfterColon;
            var end = k + 1 < labels.Count ? labels[k + 1].Start : close;
            if (Blank(code, bodyStart, end)) continue;   // falls into the next arm, or empty
            // Its lines must be its own: the label starts its line, and the next label starts a later one.
            var last = end - 1;
            while (last > bodyStart && char.IsWhiteSpace(code[last])) last--;
            if (!Blank(code, src.LineStart(src.LineOf(start)), start)
                || k + 1 < labels.Count && !Blank(code, src.LineStart(src.LineOf(end)), end))
            { notJudged++; continue; }
            arms.Add(new Arm(ArmKind.Case, src.LineOf(start), src.LineOf(last), start, end));
        }
    }

    /// <summary>The colon that ends a case label (not one of <c>::</c>); -1 when the label is not plain (a ternary).</summary>
    static int LabelColon(string code, int at, int limit)
    {
        var parens = 0;
        for (var i = at; i < limit; i++)
        {
            var c = code[i];
            if (c is '(' or '[') parens++;
            else if (c is ')' or ']') parens--;
            else if (c == '?' || c is ';' or '{' or '}') return -1;
            else if (c == ':' && parens == 0)
            {
                if (i + 1 < limit && code[i + 1] == ':') { i++; continue; }
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The report. <paramref name="kept"/>: what the static carve keeps (only kept functions are looked at).
    /// <paramref name="executed"/>: per carve-relative file, the lines that ran. <paramref name="text"/>: a file's text, or null.
    /// </summary>
    public static Report Analyze(CodeGraph graph, IReadOnlyCollection<Root> roots, Func<NodeId, bool> kept,
                                 IReadOnlyDictionary<string, HashSet<int>> executed, Func<string, string?> text,
                                 Func<string, bool>? isMacro = null, ReachabilityOptions? options = null)
    {
        options ??= ReachabilityOptions.Safe;
        var report = new Report();
        var rootIds = roots.Select(r => r.Node).ToHashSet();
        var cut = new Dictionary<NodeId, HashSet<string>>();   // per function: names only its cold arms use

        var byFile = graph.Nodes.Where(n => n.Kind == NodeKind.Function && n.Span.IsKnown && n.FilePath is not null && kept(n.Id))
                                .GroupBy(n => n.FilePath!, StringComparer.Ordinal);
        foreach (var g in byFile.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!executed.TryGetValue(g.Key, out var ran)) { report.FunctionsInUncoveredFiles += g.Count(); continue; }
            report.CoveredFiles++;
            StubBodies.Source? src = null;
            var unread = false;
            var twins = g.ToLookup(n => n.Span);
            foreach (var n in g.DistinctBy(n => n.Span).OrderBy(n => n.Span.StartLine))
            {
                var any = false;
                for (var l = n.Span.StartLine; l <= n.Span.EndLine && !any; l++) any = ran.Contains(l);
                if (!any)
                {
                    report.FunctionsNeverRan++;
                    report.NeverRanLines += n.Span.EndLine - n.Span.StartLine + 1;
                    report.NeverRan.Add(n);
                    continue;
                }
                report.FunctionsRan++;
                if (src is null && !unread)
                {
                    var t = text(g.Key);
                    if (t is null) unread = true;
                    else src = new StubBodies.Source(t);
                }
                if (src is null) { report.BodiesUnread++; continue; }
                if (StubBodies.Find(src, n.Span.StartLine, n.Span.EndLine, n.Name, isMacro) is not { } body) { report.BodiesUnread++; continue; }
                var arms = ArmsOf(src, body.Open, body.Close, out var notJudged);
                report.ArmsNotJudged += notJudged;
                report.ArmsJudged += arms.Count;

                // Cold: no line of it ran. Only the outermost of nested cold arms.
                var cold = new List<Arm>();
                foreach (var a in arms.OrderBy(a => a.Start).ThenByDescending(a => a.End))
                {
                    if (cold.Count > 0 && a.Start < cold[^1].End) continue;
                    var hit = false;
                    for (var l = a.StartLine; l <= a.EndLine && !hit; l++) hit = ran.Contains(l);
                    if (!hit) cold.Add(a);
                }
                if (cold.Count == 0) continue;
                foreach (var a in cold)
                {
                    report.Cold.Add(new ColdArm(n, a.Kind, a.StartLine, a.EndLine));
                    report.ColdLines += a.EndLine - a.StartLine + 1;
                }

                // What only the cold arms name: the body's words inside them, less those outside them.
                var inside = new HashSet<string>(StringComparer.Ordinal);
                var outside = new HashSet<string>(StringComparer.Ordinal);
                var at = body.Open + 1;
                foreach (var a in cold)
                {
                    Words(src.Code, at, a.Start, outside);
                    Words(src.Code, a.Start, a.End, inside);
                    at = a.End;
                }
                Words(src.Code, at, body.Close, outside);
                inside.ExceptWith(outside);
                if (inside.Count == 0) continue;
                foreach (var twin in twins[n.Span]) cut[twin.Id] = inside;
            }
        }

        // What removing the cold arms frees: reached from the roots now, and not once the arms' uses are gone.
        var now = Reach(graph, rootIds, options, null);
        var after = Reach(graph, rootIds, options, cut);
        foreach (var id in now)
        {
            if (after.Contains(id)) continue;
            var n = graph.GetNode(id);
            if (n.Kind is NodeKind.Function or NodeKind.Global) report.Freed.Add(n);
        }
        report.Freed.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath) is var c && c != 0 ? c : a.Span.StartLine.CompareTo(b.Span.StartLine));
        return report;
    }

    static HashSet<NodeId> Reach(CodeGraph graph, IEnumerable<NodeId> roots, ReachabilityOptions options, Dictionary<NodeId, HashSet<string>>? cut)
    {
        var reached = new HashSet<NodeId>();
        var work = new Queue<NodeId>();
        foreach (var r in roots) if (reached.Add(r)) work.Enqueue(r);
        while (work.Count > 0)
        {
            var from = work.Dequeue();
            var names = cut is not null && cut.TryGetValue(from, out var c) ? c : null;
            foreach (var e in graph.OutEdges(from))
            {
                if (!options.Follows(e.Kind)) continue;
                if (names is not null && e.Kind is not (EdgeKind.DefinedIn or EdgeKind.Includes))
                {
                    var to = graph.GetNode(e.To);
                    if (to.Kind is NodeKind.Function or NodeKind.Global && names.Contains(to.Name)) continue;
                }
                if (reached.Add(e.To)) work.Enqueue(e.To);
            }
        }
        return reached;
    }

    static void Words(string code, int from, int to, HashSet<string> into)
    {
        if (to <= from) return;
        for (var m = Word.Match(code, from, to - from); m.Success; m = m.NextMatch()) into.Add(m.Value);
    }

    static int Skip(string code, int at, int limit)
    {
        while (at < limit && char.IsWhiteSpace(code[at])) at++;
        return at;
    }

    static bool Blank(string code, int from, int to)
    {
        for (var i = Math.Max(0, from); i < to && i < code.Length; i++) if (!char.IsWhiteSpace(code[i])) return false;
        return true;
    }

    static int LineEnd(string code, int at)
    {
        var nl = code.IndexOf('\n', at);
        return nl < 0 ? code.Length : nl;
    }

    static bool IsWordAt(string code, int at, string word, int limit) =>
        at + word.Length <= limit && string.CompareOrdinal(code, at, word, 0, word.Length) == 0
        && (at + word.Length >= code.Length || !IsWord(code[at + word.Length]));

    static int Match(string code, int open, char o, char c, int limit)
    {
        var d = 0;
        for (var k = open; k < limit; k++)
        {
            if (code[k] == o) d++;
            else if (code[k] == c && --d == 0) return k;
        }
        return -1;
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
