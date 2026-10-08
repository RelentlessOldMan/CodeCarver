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
            // Windows; this, under 1 s.
            Assert.True(lookup.DirectoriesListed <= 2 * 301 + 30 + 2, $"listed {lookup.DirectoriesListed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>The cap counts code files followed, not every path a trace lists (objects, libraries, tools), and
    /// reaching it is reported: a capped scan may have missed headers.</summary>
    [Fact]
    public void Cap_CountsCodeFilesOnly_AndIsReported()
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
            File.WriteAllText(Path.Combine(glue, "g.c"), "#include \"pub/mod_api.h\"\n");
            files.Add(Path.Combine(glue, "g.c"));
            var r = ExternalIncluders.Find(files, new[] { root }, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadLines, maxFiles: 10);
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);
            Assert.False(r.Capped);
            var many = Enumerable.Range(0, 20).Select(i => Path.Combine(glue, $"c{i}.c")).ToList();
            foreach (var f in many) File.WriteAllText(f, "int x;\n");
            r = ExternalIncluders.Find(many.Append(Path.Combine(glue, "g.c")), new[] { root }, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadLines, maxFiles: 10);
            Assert.True(r.Capped);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
