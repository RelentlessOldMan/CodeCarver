using System.Text;
using CodeCarver.Core.Frontend;

namespace CodeCarver.Core.Diagnostics;

/// <summary>
/// Writes a replayable, anonymized copy of part of a carve: the chosen files (every name, string, number and comment
/// rewritten by one <see cref="Anonymizer"/>), their compile commands as a compile_commands.json, the build trace's
/// opened files, and a carve.toml with the same entry points, languages and stage. Carving the bundle should give the
/// same answer as carving the original, with nothing of the original in it.
/// <para>Every path is written as an anonymized absolute path under <c>/anon</c>, and the bundle maps <c>/anon</c>
/// onto its <c>fs/</c> folder, so files outside the carve root (an SDK beside it) keep their place.</para>
/// </summary>
public static class AnonymizedBundle
{
    public sealed record Input
    {
        /// <summary>The carve root, absolute.</summary>
        public required string Root { get; init; }
        /// <summary>Absolute paths of the files to copy (in or outside the root).</summary>
        public required IReadOnlyList<string> Files { get; init; }
        /// <summary>Compile commands (absolute or root-relative paths); those for files not copied are dropped.</summary>
        public IReadOnlyList<CompileCommand> Commands { get; init; } = Array.Empty<CompileCommand>();
        /// <summary>Absolute paths the traced build opened (all of them, copied or not: an opened file that is missing
        /// changes the answer too), or null when there was no build trace.</summary>
        public IReadOnlyCollection<string>? TraceOpened { get; init; }
        public required IReadOnlyList<string> EntryPoints { get; init; }
        public IReadOnlyList<string> Languages { get; init; } = new[] { "c" };
        /// <summary>Root-relative force-kept files.</summary>
        public IReadOnlyList<string> ForceKeep { get; init; } = Array.Empty<string>();
        /// <summary>Manual defines from the configuration (NAME or NAME=VALUE).</summary>
        public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();
        public bool CarveSource { get; init; }
        public bool CarveHeaders { get; init; }
        /// <summary>[advanced] settings that change the answer, as <c>key = value</c> lines of numbers and booleans only.</summary>
        public IReadOnlyList<string> Advanced { get; init; } = Array.Empty<string>();
        /// <summary>Names the link needs (--defsym, --undefined, --entry, a linker script, generated code).</summary>
        public IReadOnlyList<string> LinkNames { get; init; } = Array.Empty<string>();
        /// <summary>Names the link wraps (--wrap=X).</summary>
        public IReadOnlyList<string> Wrapped { get; init; } = Array.Empty<string>();
        /// <summary>Files over this many bytes are left out (noted in the result).</summary>
        public long MaxFileBytes { get; init; } = 4 << 20;
    }

    /// <param name="Withheld">The withheld files and the original words each still held. Real names: for the local
    /// report only, never written into the bundle.</param>
    public sealed record Result(string ConfigPath, string SourceRoot, int FilesWritten, int FilesWithheld, int FilesTooLarge,
                                string StageName, IReadOnlyList<(string File, IReadOnlyList<string> Words)> Withheld);

    public const string StageName = "case";

    /// <summary>The anonymized absolute form of <paramref name="absPath"/>: under the carve root <c>/anon/root/p1/p2.c</c>,
    /// elsewhere <c>/anon/ext/p3/p4/p5.h</c>.</summary>
    public static string AnonAbs(Anonymizer a, string root, string absPath)
    {
        var full = Path.GetFullPath(absPath);
        var rel = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
        if (rel == ".") return "/anon/root";
        return !rel.StartsWith("../", StringComparison.Ordinal) && rel != ".." && !Path.IsPathFullyQualified(rel)
            ? "/anon/root/" + a.Path(rel)
            : "/anon/ext" + a.Path(Norm(full));
    }

    static string Norm(string abs)
    {
        var p = Path.GetFullPath(abs).Replace('\\', '/');
        if (p.Length >= 2 && p[1] == ':') p = p[2..];
        if (p.StartsWith("//", StringComparison.Ordinal)) p = p[1..];   // a UNC share: //server/share -> /server/share
        return p.StartsWith('/') ? p : "/" + p;
    }

    /// <summary>Teaches <paramref name="a"/> every text the bundle will rewrite. Call before <see cref="Write"/>, and
    /// before anything else rewritten with the same anonymizer, so its words never collide.</summary>
    public static void Learn(Input input, Anonymizer a, Func<string, string> read)
    {
        a.Learn(Norm(input.Root));
        foreach (var f in input.Files) { a.Learn(Norm(f)); a.Learn(read(f)); }
        foreach (var p in input.TraceOpened ?? Array.Empty<string>()) a.Learn(Norm(p));
        foreach (var c in input.Commands)
        {
            a.Learn(c.Directory); a.Learn(c.File);
            foreach (var s in c.Defines.Concat(c.Includes).Concat(c.ForcedIncludes)) a.Learn(s);
        }
        foreach (var s in input.EntryPoints.Concat(input.ForceKeep).Concat(input.Defines).Concat(input.LinkNames).Concat(input.Wrapped)) a.Learn(s);
    }

    public static Result Write(Input input, string outDir, Anonymizer a, Func<string, string> read)
    {
        var fsDir = Path.Combine(outDir, "fs");
        Directory.CreateDirectory(fsDir);
        var root = Path.GetFullPath(input.Root);
        var written = 0; var withheld = 0; var tooLarge = 0;
        var withheldList = new List<(string, IReadOnlyList<string>)>();
        var copied = new HashSet<string>(Util.PathComparer.Default);
        foreach (var f in input.Files.Distinct(Util.PathComparer.Default))
        {
            long len;
            try { len = new FileInfo(f).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (len > input.MaxFileBytes) { tooLarge++; continue; }
            var text = a.Code(read(f));
            // The guarantee: a file that still holds an original word is not written at all.
            var anonPath = AnonAbs(a, root, f);
            // (The bundle's own /anon/root and /anon/ext prefix is not from the original.)
            var leaks = a.Leaks(text).Concat(a.Leaks(anonPath[(anonPath.IndexOf('/', 6) + 1)..])).Distinct().ToList();
            if (leaks.Count > 0) { withheld++; withheldList.Add((f, leaks)); continue; }
            var dest = Path.Combine(fsDir, anonPath.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, text);
            copied.Add(Path.GetFullPath(f));
            written++;
        }

        string Abs(string dir, string p) => Path.GetFullPath(Path.IsPathFullyQualified(p) ? p : Path.Combine(dir, p));
        var cmds = new StringBuilder("[\n");
        var first = true;
        foreach (var c in input.Commands)
        {
            var dir = Abs(root, c.Directory);
            var file = Abs(dir, c.File);
            if (!copied.Contains(file)) continue;
            var args = new List<string> { "gcc" };
            args.AddRange(c.Defines.Select(d => "-D" + a.Text(d)));
            args.AddRange(c.Includes.Select(i => "-I" + AnonAbs(a, root, Abs(dir, i))));
            foreach (var fi in c.ForcedIncludes) { args.Add("-include"); args.Add(AnonAbs(a, root, Abs(dir, fi))); }
            args.Add("-c");
            args.Add(AnonAbs(a, root, file));
            if (!first) cmds.Append(",\n");
            first = false;
            cmds.Append("  {\"directory\": ").Append(Json(AnonAbs(a, root, dir))).Append(", \"file\": ").Append(Json(AnonAbs(a, root, file)))
                .Append(", \"arguments\": [").Append(string.Join(", ", args.Select(Json))).Append("]}");
        }
        cmds.Append("\n]\n");
        File.WriteAllText(Path.Combine(outDir, "compile_commands.json"), cmds.ToString());
        if (input.TraceOpened is not null)
            File.WriteAllText(Path.Combine(outDir, "build.trace"),
                string.Concat(input.TraceOpened.Select(p => AnonAbs(a, root, p)).Distinct(StringComparer.Ordinal).Select(p => p + "\n")));

        var anonRoot = AnonAbs(a, root, root);
        var srcRoot = Path.Combine(fsDir, anonRoot.TrimStart('/'));
        Directory.CreateDirectory(srcRoot);
        // What the link needs, as linker flags in a response file in the tree (build scripts there are read for them).
        if (input.LinkNames.Count > 0 || input.Wrapped.Count > 0)
            File.WriteAllText(Path.Combine(srcRoot, "link-names.rsp"),
                string.Concat(input.LinkNames.Distinct(StringComparer.Ordinal).Select(n => $"-Wl,--undefined={a.Name(n)}\n"))
                + string.Concat(input.Wrapped.Distinct(StringComparer.Ordinal).Select(n => $"-Wl,--wrap={a.Name(n)}\n")));
        const string up = "..";   // fs/anon/root -> fs/anon
        var toml = new StringBuilder();
        toml.Append("# Anonymized CodeCarver case: carve with  codecarver carve ").Append(Rel(outDir, srcRoot)).Append(" --config carve.toml\n");
        toml.Append("outputDirectory = \"out\"\n\n[common]\n");
        toml.Append("entryPoints = [").Append(string.Join(", ", input.EntryPoints.Select(e => Json(a.Name(e))))).Append("]\n");
        toml.Append("languages = [").Append(string.Join(", ", input.Languages.Select(Json))).Append("]\n");
        if (input.ForceKeep.Count > 0)
            toml.Append("forceKeepFiles = [").Append(string.Join(", ", input.ForceKeep.Select(f => Json(a.Path(f.Replace('\\', '/')))))).Append("]\n");
        if (!first || input.TraceOpened is not null || input.Defines.Count > 0)
        {
            toml.Append("\n[builds.main]\n");
            if (!first) toml.Append("buildLogs = [\"compile_commands.json\"]\n");
            if (input.TraceOpened is not null) toml.Append("buildTraceFiles = [\"build.trace\"]\n");
            if (input.Defines.Count > 0)
                toml.Append("defines = [").Append(string.Join(", ", input.Defines.Select(d => Json(a.Text(d))))).Append("]\n");
        }
        toml.Append("\n[advanced]\npathMap = [ { from = \"/anon\", to = ").Append(Json(up)).Append(" } ]\n");
        foreach (var line in input.Advanced) toml.Append(line).Append('\n');
        toml.Append("\n[stages.").Append(StageName).Append("]\ncarveSourceFileContents = ").Append(input.CarveSource ? "true" : "false")
            .Append("\ncarveHeaderFileContents = ").Append(input.CarveHeaders ? "true" : "false").Append('\n');
        var cfg = Path.Combine(outDir, "carve.toml");
        File.WriteAllText(cfg, toml.ToString());
        return new Result(cfg, srcRoot, written, withheld, tooLarge, StageName, withheldList);
    }

    static string Rel(string from, string to) => Path.GetRelativePath(from, to).Replace('\\', '/');

    static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
