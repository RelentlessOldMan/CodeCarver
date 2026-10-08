using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

public sealed class ExternalIncludersTests
{
    /// <summary>A whole product's worth of outside code against a long -I list, its headers spread over those dirs
    /// and including each other: each include name is searched once, and a search reads directory listings rather
    /// than asking the file system about every (dir, name) pair.</summary>
    [Fact]
    public void ManyOutsideFiles_ManyIncludeDirs_SearchEachNameOnce()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-ext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(work, "mod");
            Directory.CreateDirectory(Path.Combine(root, "pub"));
            File.WriteAllText(Path.Combine(root, "pub", "mod_api.h"), "int mod_api(void);\n");
            var dirs = Enumerable.Range(0, 300).Select(i => Path.Combine(work, "inc", "d" + i)).ToList();
            foreach (var d in dirs) Directory.CreateDirectory(d);
            var rnd = new Random(7);
            for (var h = 0; h < 3000; h++)
                File.WriteAllText(Path.Combine(dirs[rnd.Next(300)], $"h_{h}.h"),
                    string.Concat(Enumerable.Range(0, 4).Select(_ => $"#include \"h_{rnd.Next(3000)}.h\"\n")));
            dirs.Add(root);
            var files = new List<string>();
            for (var i = 0; i < 3000; i++)
            {
                var f = Path.Combine(work, "glue", "g" + (i % 30), $"glue_{i}.c");
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, string.Concat(Enumerable.Range(0, 25).Select(_ => $"#include <sys_{rnd.Next(200)}.h>\n#include \"h_{rnd.Next(3000)}.h\"\n"))
                                     + (i == 1234 ? "#include \"pub/mod_api.h\"\n" : "") + "int x;\n");
                files.Add(f);
            }
            var lookup = new CodeCarver.Core.Util.FileLookup();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ExternalIncluders.Find(files, dirs, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadLines, lookup);
            sw.Stop();
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);
            Assert.Equal(3000 + 3000 + 1, r.FilesScanned);   // and the root header, followed for what it includes
            // Every directory is listed once, whatever the number of (dir, name) probes: 300 -I dirs and the pub/ each
            // could hold, 30 glue dirs, the root and its pub/. Probing each pair with File.Exists took 13 s here on
            // Windows; this takes about 2 s (it reads all 6,000 files). The limit leaves room for a loaded test run.
            Assert.True(lookup.DirectoriesListed <= 2 * 301 + 30 + 2, $"listed {lookup.DirectoriesListed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>The cap counts files followed through #includes only: the build's own outside files (however many,
    /// and whatever else a trace lists) never use it up before an include is followed. Reaching it is reported.</summary>
    [Fact]
    public void Cap_CountsFollowedIncludesOnly_AndIsReported()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-ext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(work, "mod");
            Directory.CreateDirectory(Path.Combine(root, "pub"));
            File.WriteAllText(Path.Combine(root, "pub", "mod_api.h"), "int mod_api(void);\n");
            var glue = Path.Combine(work, "glue");
            Directory.CreateDirectory(glue);
            var files = Enumerable.Range(0, 50).Select(i => Path.Combine(glue, $"o{i}.o")).ToList();
            foreach (var f in files) File.WriteAllText(f, "");
            var many = Enumerable.Range(0, 20).Select(i => Path.Combine(glue, $"c{i}.c")).ToList();
            foreach (var f in many) File.WriteAllText(f, "int x;\n");
            // The include sits in the LAST outside file: before, the 20 ahead of it used up a limit of 10.
            File.WriteAllText(Path.Combine(glue, "g.c"), "#include \"pub/mod_api.h\"\n");
            var seeds = files.Concat(many).Append(Path.Combine(glue, "g.c")).ToList();
            var r = ExternalIncluders.Find(seeds, new[] { root }, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadLines, maxFiles: 10);
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);
            Assert.False(r.Capped);
            Assert.Equal(22, r.FilesScanned);
            Assert.Equal(1, r.Followed);

            // Past the limit of headers followed: reported.
            for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(glue, $"h{i}.h"), "int y;\n");
            File.WriteAllText(Path.Combine(glue, "wide.c"), string.Concat(Enumerable.Range(0, 20).Select(i => $"#include \"h{i}.h\"\n")));
            r = ExternalIncluders.Find(new[] { Path.Combine(glue, "wide.c") }, new[] { root }, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadLines, maxFiles: 10);
            Assert.True(r.Capped);
            Assert.Equal(10, r.Followed);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>An outside compile force-includes a root header (`-include mod/cfg/autoconf.h`, Kconfig style): the
    /// outside code uses it though no #include names it. Before, the in-root seed was dropped. A forced include that
    /// isn't beside the command is looked for through the include directories, like the compiler does.</summary>
    [Fact]
    public void ForcedInclude_OfARootHeader_IsFound()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-ext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(work, "mod");
            Directory.CreateDirectory(Path.Combine(root, "cfg"));
            File.WriteAllText(Path.Combine(root, "cfg", "autoconf.h"), "#define CONFIG_FAST 1\n#include \"feature.h\"\n");
            File.WriteAllText(Path.Combine(root, "cfg", "feature.h"), "#define CONFIG_WIDE 1\n");
            File.WriteAllText(Path.Combine(root, "cfg", "other.h"), "\n");
            var glue = Path.Combine(work, "glue");
            Directory.CreateDirectory(glue);
            var rootFiles = new[] { "cfg/autoconf.h", "cfg/feature.h", "cfg/other.h" };

            var r = ExternalIncluders.Find(Array.Empty<string>(), Array.Empty<string>(), root, rootFiles, _ => false, File.ReadLines,
                                           forcedIncludes: new[] { (glue, Path.Combine(root, "cfg", "autoconf.h")) });
            Assert.Equal(new[] { "cfg/autoconf.h", "cfg/feature.h" }, r.Headers);   // and what it includes

            r = ExternalIncluders.Find(Array.Empty<string>(), new[] { Path.Combine(root, "cfg") }, root, rootFiles, _ => false, File.ReadLines,
                                       forcedIncludes: new[] { (glue, "autoconf.h") });
            Assert.Equal(new[] { "cfg/autoconf.h", "cfg/feature.h" }, r.Headers);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>A carve root at a drive or file-system root (`C:\`, `/`): its prefix used to get a doubled separator
    /// and match nothing.</summary>
    [Fact]
    public void RootAtADriveRoot_StillMatches()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-ext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inc = Path.Combine(work, "mod", "pub");
            Directory.CreateDirectory(inc);
            File.WriteAllText(Path.Combine(inc, "mod_api.h"), "int mod_api(void);\n");
            var drive = Path.GetPathRoot(Path.GetFullPath(work))!;
            var rel = Path.GetRelativePath(drive, Path.Combine(inc, "mod_api.h")).Replace('\\', '/');
            var r = ExternalIncluders.Find(Array.Empty<string>(), new[] { inc }, drive, new[] { rel }, _ => false, File.ReadLines,
                                           forcedIncludes: new[] { (work, "mod_api.h") }, realDirectory: _ => null);
            Assert.Equal(new[] { rel }, r.Headers);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>An include directory that reaches the root through a link (a junction, a symlink, a subst or mapped
    /// drive) is the root: its hits are root headers, spelled as the walk spells them. Before, they were outside
    /// files, the by-name fallback never ran (there were hits), and the result was empty with no warning.</summary>
    [Fact]
    public void IncludeDir_ThroughALink_ResolvesIntoTheRoot()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-ext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(work, "mod");
            Directory.CreateDirectory(Path.Combine(root, "pub"));
            File.WriteAllText(Path.Combine(root, "pub", "mod_api.h"), "int mod_api(void);\n");
            var glue = Path.Combine(work, "glue");
            Directory.CreateDirectory(glue);
            File.WriteAllText(Path.Combine(glue, "g.c"), "#include \"api/mod_api.h\"\n");
            var rootFiles = new[] { "pub/mod_api.h" };

            // A stand-in link: <work>/alias/api is the root's pub/ (portable; no link on disk needed).
            var aliasInc = Path.Combine(work, "alias");
            Directory.CreateDirectory(Path.Combine(aliasInc, "api"));
            File.WriteAllText(Path.Combine(aliasInc, "api", "mod_api.h"), "int mod_api(void);\n");
            string? Real(string d) => string.Equals(Path.TrimEndingDirectorySeparator(d), Path.Combine(aliasInc, "api"), StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, "pub") : d;
            var r = ExternalIncluders.Find(new[] { Path.Combine(glue, "g.c") }, new[] { aliasInc }, root, rootFiles, _ => false, File.ReadLines,
                                           realDirectory: Real);
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);

            // A real link on disk, when this machine can make one (a junction on Windows, a symlink elsewhere).
            var linkParent = Path.Combine(work, "linked");
            Directory.CreateDirectory(linkParent);
            var link = Path.Combine(linkParent, "api");
            if (!MakeDirectoryLink(link, Path.Combine(root, "pub"))) return;
            r = ExternalIncluders.Find(new[] { Path.Combine(glue, "g.c") }, new[] { linkParent }, root, rootFiles, _ => false, File.ReadLines);
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);
        }
        finally
        {
            // Remove the link itself first: deleting through it would empty the root's pub/ before the tree goes.
            try { var l = Path.Combine(work, "linked", "api"); if (Directory.Exists(l)) Directory.Delete(l); } catch { }
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static bool MakeDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
