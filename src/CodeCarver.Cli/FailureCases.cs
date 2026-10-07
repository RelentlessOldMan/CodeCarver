using System.IO.Compression;
using System.Text;
using CodeCarver.Core.Diagnostics;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;

namespace CodeCarver.Cli;

/// <summary>
/// When verify fails, everything a developer would otherwise go and dig for, written next to the carve with no flag:
/// <list type="bullet">
/// <item><c>debug/raw/cases.txt</c>: per failure, the use and its enclosing function, the line's #if state and enclosing
///   #if lines, every file that defines the name with whether the build compiled it, the trace opened it, the parser
///   read it and the carve kept it, every graph node with the name, and the code around the use and the
///   definition. Real names and paths: it stays on the machine.</item>
/// <item><c>debug/raw/key.txt</c>: which anonymized name is which real one, to read an answer that names
///   <c>k12</c> or <c>p3.c</c>. Stays on the machine.</item>
/// <item><c>debug/anon/</c> and <c>debug/anon.zip</c>: the same cases anonymized, each with a replayable bundle of the
///   files involved (<see cref="AnonymizedBundle"/>), and whether carving that bundle reproduces the failure. Safe to
///   send: every file is checked to hold no original word, and one that does is left out.</item>
/// </list>
/// </summary>
public static class FailureCases
{
    /// <summary>Set while a bundle is being re-carved, so that carve doesn't write its own cases.</summary>
    [ThreadStatic] static bool _replaying;
    public static bool Replaying => _replaying;

    public const int MaxCases = 12;
    const int MaxFilesPerCase = 200;
    const long MaxBytesPerCase = 16L << 20;

    public sealed record Context
    {
        public required string Root { get; init; }
        public required Func<string, string> FullPath { get; init; }
        public required CodeGraph Graph { get; init; }
        public required CarvePlan Plan { get; init; }
        public required IReadOnlyList<CompileCommand> BuildCommands { get; init; }
        /// <summary>Root-relative files the traced build opened, or null without a build trace.</summary>
        public IReadOnlySet<string>? BuildOpened { get; init; }
        /// <summary>Every path the build traces name (absolute, in or outside the root, present or not), or null.</summary>
        public IReadOnlyCollection<string>? BuildTraceAll { get; init; }
        /// <summary>Names the link needs and names it wraps.</summary>
        public IReadOnlyList<string> LinkNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Wrapped { get; init; } = Array.Empty<string>();
        public required IReadOnlySet<string> NotBuilt { get; init; }
        public required IReadOnlySet<string> Unparsed { get; init; }
        /// <summary>(rel, text) -> per-line "dead" and "uncertain" maps under this file's #if model, either may be null.</summary>
        public required Func<string, string, (bool[]? Dead, bool[]? Uncertain)> LineMaps { get; init; }
        public required IReadOnlyList<string> Languages { get; init; }
        public required IReadOnlyList<string> ManualDefines { get; init; }
        public bool CarveSource { get; init; }
        public bool CarveHeaders { get; init; }
        /// <summary>[advanced] lines that change the answer (numbers and booleans only), carried into each bundle.</summary>
        public IReadOnlyList<string> Advanced { get; init; } = Array.Empty<string>();
    }

    public sealed record Outcome(int Cases, int Reproduced, string? AnonZip);

    sealed class Case
    {
        public required LinkViolation V;
        public required string Cause;
        public required string Shape;
        public Node? Enclosing;
        public List<string> Files = new();
        public bool Truncated;
        public string? Reproduced;   // "yes", "no", "error", or null when not tried
        public string? ReproCause;
    }

    public static Outcome Write(Context cx, IReadOnlyList<LinkViolation> hard, IReadOnlyDictionary<LinkViolation, string> why,
                                IReadOnlyDictionary<LinkViolation, List<string>> shapes, string ccDir)
    {
        var debugDir = Path.Combine(ccDir, "debug");
        try { if (Directory.Exists(debugDir)) Directory.Delete(debugDir, recursive: true); } catch (IOException) { }
        var rawDir = Path.Combine(debugDir, "raw");
        var anonDir = Path.Combine(debugDir, "anon");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(anonDir);

        // One case per distinct (cause, shape) first, then the rest, up to the cap.
        var ordered = hard.GroupBy(v => (why.GetValueOrDefault(v) ?? "other") + "|" + ShapeOf(shapes, v))
                          .SelectMany(g => g.Select((v, i) => (v, i))).OrderBy(x => x.i).Select(x => x.v).Take(MaxCases).ToList();
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        string Text(string rel)
        {
            if (text.TryGetValue(rel, out var t)) return t;
            try { t = File.ReadAllText(cx.FullPath(rel)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { t = ""; }
            return text[rel] = t;
        }

        var nodesByFile = cx.Graph.Nodes.Where(n => n.FilePath is not null && n.Kind != NodeKind.File)
                            .GroupBy(n => n.FilePath!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var fileNode = cx.Graph.Nodes.Where(n => n.Kind == NodeKind.File)
                         .GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var cases = new List<Case>();
        foreach (var v in ordered)
        {
            var c = new Case { V = v, Cause = why.GetValueOrDefault(v) ?? "other", Shape = ShapeOf(shapes, v) };
            c.Enclosing = nodesByFile.GetValueOrDefault(v.ReferencedIn)?
                .Where(n => n.Kind is NodeKind.Function or NodeKind.Global && n.Span.IsKnown && n.Span.StartLine <= v.Line && v.Line <= n.Span.EndLine)
                .OrderBy(n => n.Span.EndLine - n.Span.StartLine).FirstOrDefault();
            c.Files = Closure(cx, fileNode, new[] { v.ReferencedIn }.Concat(v.DefinedInAll.DefaultIfEmpty(v.DefinedIn)), out c.Truncated);
            cases.Add(c);
        }

        // One anonymizer for every case, taught everything first so its words never collide.
        var a = new Anonymizer();
        var inputs = cases.Select(c => BundleInput(cx, c)).ToList();
        foreach (var inp in inputs) AnonymizedBundle.Learn(inp, a, f => SafeRead(f));
        foreach (var c in cases) a.Learn(c.V.Name);

        var raw = new StringBuilder();
        var anon = new StringBuilder();
        raw.AppendLine("# CodeCarver verify failures, in full. Real names and paths: keep this on this machine.");
        raw.AppendLine("# The anonymized copy is debug/anon.zip; debug/raw/key.txt says which anonymized name is which.");
        anon.AppendLine("# CodeCarver verify failures, anonymized: every name, path, string, number and comment rewritten.");
        anon.AppendLine("# Each caseN/ folder is a replayable carve of the files involved:  codecarver carve <caseN/src root> --config caseN/carve.toml");
        anon.AppendLine($"# {hard.Count} failure(s), {cases.Count} written.");
        // Anonymized file text for the excerpts, checked like every bundle file: a word that survived is cut out.
        var anonText = new Dictionary<string, string>(StringComparer.Ordinal);
        string AnonText(string rel)
        {
            if (anonText.TryGetValue(rel, out var t)) return t;
            t = a.Code(Text(rel));
            foreach (var leak in a.Leaks(t)) t = System.Text.RegularExpressions.Regex.Replace(t, $@"\b{leak}\b", "<withheld>");
            return anonText[rel] = t;
        }
        var reproduced = 0;
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var caseDir = Path.Combine(anonDir, $"case{i + 1}");
            AnonymizedBundle.Result? bundle = null;
            try
            {
                bundle = AnonymizedBundle.Write(inputs[i], caseDir, a, f => SafeRead(f));
                (c.Reproduced, c.ReproCause) = Replay(bundle, a.Name(c.V.Name));
                if (c.Reproduced == "yes") reproduced++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                c.Reproduced = "error (" + ex.GetType().Name + ")";
            }
            raw.Append(Describe(cx, c, i + 1, Text, null, bundle));
            anon.Append(Describe(cx, c, i + 1, AnonText, a, bundle));
        }

        File.WriteAllText(Path.Combine(rawDir, "cases.txt"), raw.ToString());
        File.WriteAllText(Path.Combine(rawDir, "key.txt"),
            "# anonymized -> original. Keep this on this machine.\n" + string.Concat(a.Map().Select(p => $"{p.Anonymized}\t{p.Original}\n")));
        File.WriteAllText(Path.Combine(anonDir, "cases.txt"), anon.ToString());
        var zip = Path.Combine(debugDir, "anon.zip");
        try
        {
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(anonDir, zip);
        }
        catch (IOException) { zip = null!; }
        return new Outcome(cases.Count, reproduced, zip);
    }

    static string ShapeOf(IReadOnlyDictionary<LinkViolation, List<string>> shapes, LinkViolation v) =>
        shapes.TryGetValue(v, out var s) ? string.Join('+', s) : "";

    static string SafeRead(string full)
    {
        try { return File.ReadAllText(full); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // The files a case needs: the use and definition files, the headers each includes (the graph's include edges,
    // breadth first), and the files their compile commands force-include. Capped.
    static List<string> Closure(Context cx, Dictionary<string, Node> fileNode, IEnumerable<string> seeds, out bool truncated)
    {
        truncated = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var queue = new Queue<string>();
        foreach (var s in seeds) if (seen.Add(s)) queue.Enqueue(s);
        long bytes = 0;
        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();
            long len;
            try { len = new FileInfo(cx.FullPath(rel)).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            if (order.Count >= MaxFilesPerCase || bytes + len > MaxBytesPerCase) { truncated = true; continue; }
            order.Add(rel);
            bytes += len;
            if (fileNode.TryGetValue(rel, out var fn))
                foreach (var e in cx.Graph.OutEdges(fn.Id))
                    if (e.Kind == EdgeKind.Includes && cx.Graph.GetNode(e.To).FilePath is { } inc && seen.Add(inc)) queue.Enqueue(inc);
            foreach (var cmd in cx.BuildCommands.Where(cc => SameFile(cx, cc, rel)))
                foreach (var fi in cmd.ForcedIncludes)
                    if (ToRel(cx, cmd.Directory, fi) is { } r && seen.Add(r)) queue.Enqueue(r);
        }
        return order;
    }

    static string Abs(Context cx, string dir, string p)
    {
        var d = Path.IsPathFullyQualified(dir) ? dir : Path.Combine(cx.Root, dir);
        return Path.GetFullPath(Path.IsPathFullyQualified(p) ? p : Path.Combine(d, p));
    }

    static string? ToRel(Context cx, string dir, string p)
    {
        var rel = Path.GetRelativePath(cx.Root, Abs(cx, dir, p)).Replace('\\', '/');
        return rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(rel) ? null : rel;
    }

    static bool SameFile(Context cx, CompileCommand cc, string rel) =>
        string.Equals(ToRel(cx, cc.Directory, cc.File), rel, StringComparison.OrdinalIgnoreCase);

    static readonly System.Text.RegularExpressions.Regex IncludeTarget =
        new(@"^[ \t]*#[ \t]*include(?:_next)?[ \t]*[""<]([^"">\r\n]+)["">]", System.Text.RegularExpressions.RegexOptions.Multiline);

    // The part of the build trace this case needs: its own files, the objects named like them (an incremental build
    // links helper.o without opening helper.c), and files outside the tree its files #include (an SDK or a configure
    // header) — those are copied too when they exist. A missing one stays in the trace: that it was opened and isn't
    // here changes the answer.
    static (List<string>? Trace, List<string> Outside) TraceFor(Context cx, Case c)
    {
        var outside = new List<string>();
        if (cx.BuildTraceAll is not { Count: > 0 } all)
            return (cx.BuildOpened?.Where(c.Files.Contains).Select(cx.FullPath).ToList(), outside);
        var closure = c.Files.Select(f => Path.GetFullPath(cx.FullPath(f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stems = c.Files.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in c.Files)
            foreach (System.Text.RegularExpressions.Match m in IncludeTarget.Matches(SafeRead(cx.FullPath(f))))
                includes.Add(Path.GetFileName(m.Groups[1].Value.Trim()));
        var rootF = Path.TrimEndingDirectorySeparator(cx.Root) + Path.DirectorySeparatorChar;
        var trace = new List<string>();
        foreach (var p in all)
        {
            if (closure.Contains(p)) { trace.Add(p); continue; }
            if (Path.GetExtension(p).ToLowerInvariant() is ".o" or ".obj")
            {
                if (stems.Contains(Path.GetFileNameWithoutExtension(p))) trace.Add(p);
                continue;
            }
            if (p.StartsWith(rootF, StringComparison.OrdinalIgnoreCase) || !includes.Contains(Path.GetFileName(p))) continue;
            trace.Add(p);
            if (outside.Count < 64 && File.Exists(p)) outside.Add(p);
        }
        return (trace, outside);
    }

    static AnonymizedBundle.Input BundleInput(Context cx, Case c)
    {
        var (trace, outside) = TraceFor(cx, c);
        var files = c.Files.Select(cx.FullPath).Concat(outside).ToList();
        var cmds = cx.BuildCommands.Where(cc => c.Files.Any(f => SameFile(cx, cc, f)))
                     .Select(cc => cc with { Directory = Path.IsPathFullyQualified(cc.Directory) ? cc.Directory : Path.Combine(cx.Root, cc.Directory) })
                     .ToList();
        // The root: the function (or table) around the use. A use the carve never tied to one keeps its whole file.
        var entry = c.Enclosing is { } n ? new[] { n.Name } : Array.Empty<string>();
        return new AnonymizedBundle.Input
        {
            Root = cx.Root,
            Files = files,
            Commands = cmds,
            TraceOpened = trace,
            LinkNames = cx.LinkNames,
            Wrapped = cx.Wrapped,
            EntryPoints = entry.Length > 0 ? entry : new[] { "main" },
            ForceKeep = entry.Length > 0 ? Array.Empty<string>() : new[] { c.V.ReferencedIn },
            Languages = cx.Languages,
            Defines = cx.ManualDefines,
            CarveSource = cx.CarveSource,
            CarveHeaders = cx.CarveHeaders,
            Advanced = cx.Advanced,
        };
    }

    // Carves the bundle and reads its verify.txt: does the same name fail, and with what cause? The bundle's output
    // folder is deleted afterwards (its report names this machine's paths).
    static (string, string?) Replay(AnonymizedBundle.Result bundle, string anonName)
    {
        var outDir = Path.Combine(Path.GetDirectoryName(bundle.ConfigPath)!, "out");
        var saved = (DiagState.Report, DiagState.Path, DiagState.DefaultPath);
        _replaying = true;
        try
        {
            var code = CarveCommand.Run(new[] { "carve", bundle.SourceRoot, "--config", bundle.ConfigPath }, TextWriter.Null, TextWriter.Null);
            var verify = Path.Combine(outDir, bundle.StageName, "codecarver", "verify.txt");
            var line = File.Exists(verify)
                ? File.ReadLines(verify).FirstOrDefault(l => l.StartsWith("FAIL " + anonName + "\t", StringComparison.Ordinal))
                : null;
            if (line is null) return (code == 3 ? "no (verify failed on another name)" : $"no (exit {code})", null);
            var cause = line.Split('\t').FirstOrDefault(p => p.StartsWith("cause ", StringComparison.Ordinal))?[6..];
            return ("yes", cause);
        }
        finally
        {
            _replaying = false;
            (DiagState.Report, DiagState.Path, DiagState.DefaultPath) = saved;
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); } catch (IOException) { }
        }
    }

    static string Describe(Context cx, Case c, int n, Func<string, string> text, Anonymizer? a, AnonymizedBundle.Result? bundle)
    {
        string N(string s) => a is null ? s : a.Name(s);
        string P(string s) => a is null ? s : a.Path(s);
        var v = c.V;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"=== case {n}: {N(v.Name)}   cause {c.Cause}{(c.Shape.Length > 0 ? "   shape " + c.Shape : "")}");
        sb.AppendLine($"replay: {(c.Reproduced ?? "not tried")}{(c.ReproCause is { } rc ? " (cause " + rc + ")" : "")}"
                      + (bundle is null ? "" : $"   bundle case{n}/: {bundle.FilesWritten} file(s)"
                         + (bundle.FilesWithheld > 0 ? $", {bundle.FilesWithheld} withheld (still held an original word)" : "")
                         + (bundle.FilesTooLarge > 0 ? $", {bundle.FilesTooLarge} too large" : "")
                         + (c.Truncated ? ", closure capped" : "")));
        sb.AppendLine($"root used for the replay: {(c.Enclosing is { } en ? $"{en.Kind} {N(en.Name)}" : "the use's whole file (no enclosing function found)")}");

        // The use.
        var useText = text(v.ReferencedIn);
        var (useDead, useUnc) = cx.LineMaps(v.ReferencedIn, SafeRead(cx.FullPath(v.ReferencedIn)));
        sb.AppendLine($"use: {P(v.ReferencedIn)}:{v.Line}   line {State(useDead, useUnc, v.Line)}   {FileFacts(cx, v.ReferencedIn)}");
        if (c.Enclosing is { } e)
            sb.AppendLine($"  inside {e.Kind} {N(e.Name)} (lines {e.Span}), {(cx.Plan.IsKept(e.Id) ? "kept" : "NOT kept")}");
        Ifs(sb, useText, v.Line);
        Excerpt(sb, useText, v.Line - 4, v.Line + 3, v.Line);

        // Every definition the check found, and what the carve knew about each.
        var defs = v.DefinedInAll.Count > 0 ? v.DefinedInAll : new[] { v.DefinedIn };
        sb.AppendLine($"defined only in dropped file(s): {defs.Count}");
        foreach (var d in defs)
        {
            var line = d == v.DefinedIn ? v.DefinedLine : 0;
            var (dd, du) = cx.LineMaps(d, SafeRead(cx.FullPath(d)));
            sb.AppendLine($"  {P(d)}{(line > 0 ? ":" + line : "")}   {(line > 0 ? "line " + State(dd, du, line) + "   " : "")}{FileFacts(cx, d)}");
        }
        if (v.DefinedLine > 0)
        {
            var defText = text(v.DefinedIn);
            Ifs(sb, defText, v.DefinedLine);
            Excerpt(sb, defText, v.DefinedLine - 12, v.DefinedLine + 4, v.DefinedLine);
        }

        // What the graph has under the name.
        var named = cx.Graph.Nodes.Where(x => x.Name == v.Name).Take(20).ToList();
        sb.AppendLine($"graph nodes named {N(v.Name)}: {named.Count}{(named.Count == 20 ? "+" : "")}");
        foreach (var x in named)
            sb.AppendLine($"  {x.Kind} {(x.FilePath is { } fp ? P(fp) + ":" + x.Span : "(no file)")}  {(cx.Plan.IsKept(x.Id) ? "kept" : "dropped")}"
                          + (x.Flags != NodeFlags.None ? "  flags " + x.Flags : ""));
        return sb.ToString();
    }

    static string State(bool[]? dead, bool[]? unc, int line) =>
        dead is not null && line < dead.Length && dead[line] ? "dead (#if model)"
        : unc is not null && line < unc.Length && unc[line] ? "uncertain (#if model can't decide)"
        : "live";

    static string FileFacts(Context cx, string rel)
    {
        var cmds = cx.BuildCommands.Count(cc => SameFile(cx, cc, rel));
        var parts = new List<string>
        {
            cmds > 0 ? $"compiled by the log ({cmds} command(s))" : cx.BuildCommands.Count > 0 ? "not in the build log" : "no build log",
            cx.BuildOpened is null ? "no build trace" : cx.BuildOpened.Contains(rel) ? "opened by the traced build" : "NOT opened by the traced build",
            cx.Unparsed.Contains(rel) ? "not parsed" : cx.NotBuilt.Contains(rel) ? "not parsed (not built)" : "parsed",
            cx.Plan.KeptFiles.Contains(rel) ? "kept" : "dropped",
        };
        return string.Join(", ", parts);
    }

    // The #if/#ifdef lines open at the given line, outermost first.
    static void Ifs(StringBuilder sb, string text, int line)
    {
        var lines = text.Split('\n');
        var stack = new List<(int, string)>();
        for (var i = 0; i < Math.Min(line - 1, lines.Length); i++)
        {
            var t = lines[i].TrimStart();
            if (!t.StartsWith('#')) continue;
            var d = t[1..].TrimStart();
            if (d.StartsWith("if", StringComparison.Ordinal)) stack.Add((i + 1, lines[i].TrimEnd('\r')));
            else if (d.StartsWith("el", StringComparison.Ordinal) && stack.Count > 0) stack.Add((i + 1, lines[i].TrimEnd('\r')));
            else if (d.StartsWith("endif", StringComparison.Ordinal))
            {
                while (stack.Count > 0 && !stack[^1].Item2.TrimStart()[1..].TrimStart().StartsWith("if", StringComparison.Ordinal)) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
        }
        if (stack.Count == 0) return;
        sb.AppendLine("  open #if lines:");
        foreach (var (no, t) in stack) sb.AppendLine($"    {no,6}: {t.Trim()}");
    }

    static void Excerpt(StringBuilder sb, string text, int from, int to, int mark)
    {
        var lines = text.Split('\n');
        from = Math.Max(1, from);
        to = Math.Min(lines.Length, to);
        for (var i = from; i <= to; i++)
            sb.AppendLine($"  {(i == mark ? ">" : " ")}{i,6}| {lines[i - 1].TrimEnd('\r')}");
    }
}
