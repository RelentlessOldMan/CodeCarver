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

    /// <summary>One failure to write up: a name used in <see cref="UseRel"/> (when known) that only <see cref="DefRels"/>
    /// define. From a verify violation, or from a compiler or linker error building the carved tree.</summary>
    public sealed record Subject(string Name, string? UseRel, int UseLine, IReadOnlyList<string> DefRels, int DefLine,
                                 string Cause, string Shape = "", string? Message = null, string? MessageFile = null)
    {
        public bool FromBuild => Message is not null;
    }

    sealed class Case
    {
        public required Subject S;
        public Node? Enclosing;
        public List<string> Files = new();
        public bool Truncated;
        public string? Reproduced;   // "yes", "no", "error", or null when not tried
        public string? ReproCause;
    }

    /// <summary>Writes up verify's failures in <c>debug/</c>.</summary>
    public static Outcome Write(Context cx, IReadOnlyList<LinkViolation> hard, IReadOnlyDictionary<LinkViolation, string> why,
                                IReadOnlyDictionary<LinkViolation, List<string>> shapes, string ccDir) =>
        Write(cx, hard.Select(v => new Subject(v.Name, v.ReferencedIn, v.Line,
                                               v.DefinedInAll.Count > 0 ? v.DefinedInAll : new[] { v.DefinedIn }, v.DefinedLine,
                                               why.GetValueOrDefault(v) ?? "other", ShapeOf(shapes, v))).ToList(),
              Path.Combine(ccDir, "debug"), "verify failures");

    /// <summary>Writes up the errors building the carved tree printed (<paramref name="buildOutput"/>) in
    /// <c>debug-build/</c>: for each name the compiler or linker missed, where the original tree defines it.</summary>
    public static Outcome WriteBuildErrors(Context cx, string buildOutput, string carvedDir, string ccDir)
    {
        var errors = BuildErrors.Parse(buildOutput);
        var rels = cx.Graph.Nodes.Where(n => n.Kind == NodeKind.File).Select(n => n.Name).ToList();
        var subjects = new List<Subject>();
        foreach (var e in errors)
        {
            var useRel = e.File is { } f ? ResolveRel(f, carvedDir, rels) : null;
            var useLine = e.Line;
            var name = e.Name ?? "";
            List<string> defs;
            var defLine = 0;
            if (e.Kind == BuildErrorKind.MissingHeader)
            {
                var h = name.Replace('\\', '/');
                defs = rels.Where(r => r == h || r.EndsWith("/" + h, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                var named = name.Length == 0 ? new List<Node>()
                    : cx.Graph.Nodes.Where(n => n.Name == name && n.Kind != NodeKind.File && n.FilePath is not null).ToList();
                defs = named.Select(n => n.FilePath!).Distinct(StringComparer.Ordinal).ToList();
                if (named.Count == 1) defLine = named[0].Span.StartLine;
                // A link error says no file: the use is any kept code with an edge to the name.
                if (useRel is null && named.Count > 0)
                {
                    var ids = named.Select(n => n.Id).ToHashSet();
                    var user = cx.Graph.Nodes.FirstOrDefault(n => n.Kind != NodeKind.File && cx.Plan.IsKept(n.Id) && n.FilePath is not null
                                                                  && cx.Graph.OutEdges(n.Id).Any(x => ids.Contains(x.To)));
                    if (user is not null) { useRel = user.FilePath; useLine = user.Span.StartLine; }
                }
            }
            subjects.Add(new Subject(name, useRel, useLine, defs, defLine, "build." + e.Kind, Message: e.Message, MessageFile: e.File));
        }
        return Write(cx, subjects, Path.Combine(ccDir, "debug-build"), "errors building the carved tree");
    }

    // The tree-relative path a compiler printed: under the carved tree, or the longest tree path it ends with.
    static string? ResolveRel(string printed, string carvedDir, IReadOnlyList<string> rels)
    {
        var p = printed.Replace('\\', '/');
        try
        {
            if (Path.IsPathFullyQualified(printed))
            {
                var rel = Path.GetRelativePath(carvedDir, printed).Replace('\\', '/');
                if (!rel.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathFullyQualified(rel) && rels.Contains(rel)) return rel;
            }
        }
        catch (ArgumentException) { }
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return rels.Where(r => p == r || p.EndsWith("/" + r, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(r => r.Length).FirstOrDefault();
    }

    static Outcome Write(Context cx, IReadOnlyList<Subject> subjects, string debugDir, string what)
    {
        try { if (Directory.Exists(debugDir)) Directory.Delete(debugDir, recursive: true); } catch (IOException) { }
        var rawDir = Path.Combine(debugDir, "raw");
        var anonDir = Path.Combine(debugDir, "anon");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(anonDir);

        // One case per distinct (cause, shape) first, then the rest, up to the cap.
        var ordered = subjects.GroupBy(s => s.Cause + "|" + s.Shape)
                              .SelectMany(g => g.Select((s, i) => (s, i))).OrderBy(x => x.i).Select(x => x.s).Take(MaxCases).ToList();
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
        foreach (var s in ordered)
        {
            var c = new Case { S = s };
            if (s.UseRel is { } use)
                c.Enclosing = nodesByFile.GetValueOrDefault(use)?
                    .Where(n => n.Kind is NodeKind.Function or NodeKind.Global && n.Span.IsKnown && n.Span.StartLine <= s.UseLine && s.UseLine <= n.Span.EndLine)
                    .OrderBy(n => n.Span.EndLine - n.Span.StartLine).FirstOrDefault();
            c.Files = Closure(cx, fileNode, (s.UseRel is { } u ? new[] { u } : Array.Empty<string>()).Concat(s.DefRels), out c.Truncated);
            cases.Add(c);
        }

        // One anonymizer for every case, taught everything first so its words never collide.
        var a = new Anonymizer();
        var inputs = cases.Select(c => BundleInput(cx, c)).ToList();
        foreach (var inp in inputs) AnonymizedBundle.Learn(inp, a, f => SafeRead(f));
        foreach (var c in cases) { a.Learn(c.S.Name); if (c.S.Message is { } msg) a.Learn(msg); }

        var folder = Path.GetFileName(debugDir);
        var raw = new StringBuilder();
        var anon = new StringBuilder();
        raw.AppendLine($"# CodeCarver {what}, in full. Real names and paths: keep this on this machine.");
        raw.AppendLine($"# The anonymized copy is {folder}/anon.zip; {folder}/raw/key.txt says which anonymized name is which.");
        anon.AppendLine($"# CodeCarver {what}, anonymized: every name, path, string, number and comment rewritten.");
        anon.AppendLine("# Each caseN/ folder is a replayable carve of the files involved. Each distinct file is stored once in store/;");
        anon.AppendLine("# rebuild the case trees with  powershell -File unpack.ps1  then  codecarver carve caseN/fs/anon/root --config caseN/carve.toml");
        anon.AppendLine($"# {subjects.Count} failure(s), {cases.Count} written.");
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
                (c.Reproduced, c.ReproCause) = Replay(bundle, a, c.S, root: cx.Root);
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
        Pack(anonDir);
        var zip = Path.Combine(debugDir, "anon.zip");
        try
        {
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(anonDir, zip);
        }
        catch (IOException) { zip = null!; }
        return new Outcome(cases.Count, reproduced, zip);
    }

    const string FilesList = "files.tsv";

    /// <summary>Rebuilds every caseN/ tree under <paramref name="anonDir"/> from store/ (what unpack.ps1 does).</summary>
    public static void Unpack(string anonDir)
    {
        foreach (var caseDir in Directory.EnumerateDirectories(anonDir, "case*"))
        {
            var list = Path.Combine(caseDir, FilesList);
            if (!File.Exists(list)) continue;
            foreach (var line in File.ReadLines(list))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2) continue;
                var dest = Path.Combine(caseDir, parts[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(anonDir, "store", parts[1]), dest, overwrite: true);
            }
        }
    }

    // Each case's tree (caseN/fs) becomes caseN/files.tsv (path, stored file) plus one copy of each distinct file in
    // store/: cases that include the same headers share them, so the zip carries each once.
    static void Pack(string anonDir)
    {
        var store = Path.Combine(anonDir, "store");
        Directory.CreateDirectory(store);
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var caseDir in Directory.EnumerateDirectories(anonDir, "case*"))
        {
            var fs = Path.Combine(caseDir, "fs");
            if (!Directory.Exists(fs)) continue;
            var list = new StringBuilder();
            foreach (var f in Directory.EnumerateFiles(fs, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(f);
                var id = Convert.ToHexString(sha.ComputeHash(bytes))[..20].ToLowerInvariant();
                var blob = Path.Combine(store, id);
                if (!File.Exists(blob)) File.WriteAllBytes(blob, bytes);
                list.Append(Path.GetRelativePath(caseDir, f).Replace('\\', '/')).Append('\t').Append(id).Append('\n');
            }
            File.WriteAllText(Path.Combine(caseDir, FilesList), list.ToString());
            Directory.Delete(fs, recursive: true);
        }
        File.WriteAllText(Path.Combine(anonDir, "unpack.ps1"), UnpackScript);
    }

    // ASCII only (Windows PowerShell 5.1 reads a BOM-less script as the ANSI code page).
    const string UnpackScript =
        "# Rebuilds each caseN/ tree from store/: every line of caseN/files.tsv is <path in the case><TAB><stored file>.\n"
        + "#   powershell -ExecutionPolicy Bypass -File unpack.ps1\n"
        + "$ErrorActionPreference = 'Stop'\n"
        + "Get-ChildItem -Path $PSScriptRoot -Directory -Filter 'case*' | ForEach-Object {\n"
        + "    $case = $_.FullName\n"
        + "    $list = Join-Path $case 'files.tsv'\n"
        + "    if (Test-Path $list) {\n"
        + "        Get-Content $list | Where-Object { $_ -ne '' } | ForEach-Object {\n"
        + "            $p = $_ -split \"`t\"\n"
        + "            $dest = Join-Path $case $p[0]\n"
        + "            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null\n"
        + "            Copy-Item (Join-Path (Join-Path $PSScriptRoot 'store') $p[1]) $dest -Force\n"
        + "        }\n"
        + "    }\n"
        + "}\n"
        + "Write-Host 'unpacked: carve a case with  codecarver carve caseN/fs/anon/root --config caseN/carve.toml'\n";

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
            ForceKeep = entry.Length > 0 || c.S.UseRel is null ? Array.Empty<string>() : new[] { c.S.UseRel },
            Languages = cx.Languages,
            Defines = cx.ManualDefines,
            CarveSource = cx.CarveSource,
            CarveHeaders = cx.CarveHeaders,
            Advanced = cx.Advanced,
        };
    }

    // Carves the bundle. A verify failure reproduces when the bundle's verify fails on the same name; a build error
    // when it does, or when the bundle's carve drops every file that defines the name (or the missing header) too.
    // The bundle's output folder is deleted afterwards (its report names this machine's paths).
    static (string, string?) Replay(AnonymizedBundle.Result bundle, Anonymizer a, Subject s, string root)
    {
        var outDir = Path.Combine(Path.GetDirectoryName(bundle.ConfigPath)!, "out");
        var saved = (DiagState.Report, DiagState.Path, DiagState.DefaultPath);
        _replaying = true;
        try
        {
            var code = CarveCommand.Run(new[] { "carve", bundle.SourceRoot, "--config", bundle.ConfigPath }, TextWriter.Null, TextWriter.Null);
            var verify = Path.Combine(outDir, bundle.StageName, "codecarver", "verify.txt");
            var anonName = a.Name(s.Name);
            var line = File.Exists(verify) && s.Name.Length > 0
                ? File.ReadLines(verify).FirstOrDefault(l => l.StartsWith("FAIL " + anonName + "\t", StringComparison.Ordinal))
                : null;
            if (line is not null)
                return ("yes", line.Split('\t').FirstOrDefault(p => p.StartsWith("cause ", StringComparison.Ordinal))?[6..]);
            if (s.FromBuild && s.DefRels.Count > 0)
            {
                var carved = Path.Combine(outDir, bundle.StageName, "carved");
                bool Present(string rel)
                {
                    var f = Path.Combine(carved, a.Path(rel));
                    return File.Exists(f) && !File.ReadAllText(f).Contains("Placeholder written by CodeCarver", StringComparison.Ordinal);
                }
                if (!s.DefRels.Any(Present)) return ("yes", "every defining file dropped");
                return ("no (a defining file is kept)", null);
            }
            return (code == 3 ? "no (verify failed on another name)" : $"no (exit {code})", null);
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
        var s = c.S;
        var sb = new StringBuilder();
        sb.AppendLine();
        var title = s.Name.Length == 0 ? "(no name)" : s.Cause == "build." + BuildErrorKind.MissingHeader ? P(s.Name.Replace('\\', '/')) : N(s.Name);
        sb.AppendLine($"=== case {n}: {title}   cause {s.Cause}{(s.Shape.Length > 0 ? "   shape " + s.Shape : "")}");
        if (s.Message is { } msg)
        {
            // The compiler's own line, its file shown as the tree path. Anonymized, the compiler's own words stay and
            // every other word (a name, a path segment) is rewritten like the code: nothing else can get through.
            if (a is not null)
            {
                if (s.MessageFile is { } mf && mf.Length > 0)
                    msg = msg.Replace(mf, s.UseRel is { } ur ? P(ur) : a.Path(mf.Replace('\\', '/')), StringComparison.Ordinal);
                if (s.Cause == "build." + BuildErrorKind.MissingHeader && s.Name.Length > 0)
                    msg = msg.Replace(s.Name, P(s.Name.Replace('\\', '/')), StringComparison.Ordinal);
                msg = MessageWord.Replace(msg, m => MessageVocabulary.Contains(m.Value.ToLowerInvariant()) || m.Value.All(char.IsAsciiDigit)
                    || IsAnonToken(m.Value) ? m.Value : a.Name(m.Value));
            }
            sb.AppendLine($"error: {msg}");
        }
        sb.AppendLine($"replay: {(c.Reproduced ?? "not tried")}{(c.ReproCause is { } rc ? " (cause " + rc + ")" : "")}"
                      + (bundle is null ? "" : $"   bundle case{n}/: {bundle.FilesWritten} file(s)"
                         + (bundle.FilesWithheld > 0 ? $", {bundle.FilesWithheld} withheld (still held an original word)" : "")
                         + (bundle.FilesTooLarge > 0 ? $", {bundle.FilesTooLarge} too large" : "")
                         + (c.Truncated ? ", closure capped" : "")));
        sb.AppendLine($"root used for the replay: {(c.Enclosing is { } en ? $"{en.Kind} {N(en.Name)}" : "the use's whole file (no enclosing function found)")}");

        // The use (in the ORIGINAL file: a carved file's lines can differ where content was carved).
        if (s.UseRel is { } use)
        {
            var useText = text(use);
            var (useDead, useUnc) = cx.LineMaps(use, SafeRead(cx.FullPath(use)));
            sb.AppendLine($"use: {P(use)}:{s.UseLine}   line {State(useDead, useUnc, s.UseLine)}   {FileFacts(cx, use)}");
            if (c.Enclosing is { } e)
                sb.AppendLine($"  inside {e.Kind} {N(e.Name)} (lines {e.Span}), {(cx.Plan.IsKept(e.Id) ? "kept" : "NOT kept")}");
            Ifs(sb, useText, s.UseLine);
            Excerpt(sb, useText, s.UseLine - 4, s.UseLine + 3, s.UseLine);
        }
        else sb.AppendLine("use: not found in the tree");

        // Every definition, and what the carve knew about each.
        sb.AppendLine($"{(s.FromBuild ? "defined in" : "defined only in dropped")} file(s): {s.DefRels.Count}");
        for (var i = 0; i < s.DefRels.Count; i++)
        {
            var d = s.DefRels[i];
            var line = i == 0 ? s.DefLine : 0;
            var (dd, du) = cx.LineMaps(d, SafeRead(cx.FullPath(d)));
            sb.AppendLine($"  {P(d)}{(line > 0 ? ":" + line : "")}   {(line > 0 ? "line " + State(dd, du, line) + "   " : "")}{FileFacts(cx, d)}");
        }
        if (s.DefLine > 0 && s.DefRels.Count > 0)
        {
            var defText = text(s.DefRels[0]);
            Ifs(sb, defText, s.DefLine);
            Excerpt(sb, defText, s.DefLine - 12, s.DefLine + 4, s.DefLine);
        }

        // What the graph has under the name.
        var named = s.Name.Length == 0 ? new List<Node>() : cx.Graph.Nodes.Where(x => x.Name == s.Name).Take(20).ToList();
        sb.AppendLine($"graph nodes named {(s.Name.Length > 0 ? N(s.Name) : "-")}: {named.Count}{(named.Count == 20 ? "+" : "")}");
        foreach (var x in named)
            sb.AppendLine($"  {x.Kind} {(x.FilePath is { } fp ? P(fp) + ":" + x.Span : "(no file)")}  {(cx.Plan.IsKept(x.Id) ? "kept" : "dropped")}"
                          + (x.Flags != NodeFlags.None ? "  flags " + x.Flags : ""));
        return sb.ToString();
    }

    // A word, not the tail of a number (0x9, 1f).
    static readonly System.Text.RegularExpressions.Regex MessageWord = new(@"(?<![0-9A-Za-z_])[A-Za-z_][A-Za-z0-9_]*");
    // Words the compilers and linkers themselves print. Anything else in a message is a name or a path: rewritten.
    static readonly HashSet<string> MessageVocabulary = new(StringComparer.Ordinal)
    {
        "error", "fatal", "warning", "note", "undefined", "reference", "references", "to", "implicit", "declaration", "of",
        "function", "functions", "undeclared", "first", "use", "in", "this", "unknown", "type", "name", "no", "such", "file",
        "or", "directory", "cannot", "open", "include", "source", "identifier", "is", "not", "a", "an", "was", "declared",
        "scope", "storage", "size", "isn", "t", "known", "expected", "before", "after", "token", "unresolved", "external",
        "symbol", "referenced", "by", "from", "definition", "for", "conflicting", "types", "incompatible", "pointer",
        "integer", "without", "cast", "too", "many", "few", "arguments", "call", "iso", "and", "later", "do", "does",
        "support", "declarations", "multiple", "redefinition", "previous", "here", "has", "incomplete", "field", "member",
        "struct", "union", "enum", "named", "invalid", "initializer", "required", "as", "operand", "assignment", "makes",
        "return", "value", "non", "void", "at", "end", "input", "missing", "terminating", "character", "stray", "program",
        "collect2", "ld", "lld", "returned", "exit", "status", "the", "found", "not", "found", "text", "data", "bss",
        "rodata", "section", "relocation", "truncated", "fit", "against", "lnk2019", "lnk2001", "c2065", "c1083",
        "l6218e", "pe020", "pe1696", "li005", "referred", "line", "undefined", "symbol", "unresolved", "c99", "c11",
        "wimplicit", "werror", "std", "int", "char", "long", "short", "unsigned", "signed", "const", "static", "extern",
        "inline", "first", "defined", "macro", "passed", "takes", "only", "parameter", "parameters", "argument",
        "incompatible", "implicitly", "declaring", "library", "built", "in", "did", "you", "mean", "use", "of",
    };

    static bool IsAnonToken(string w) => System.Text.RegularExpressions.Regex.IsMatch(w, @"^(?:[kK]q?\d+|p\d+)(?:_(?:[kK]q?\d+))*$");

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
