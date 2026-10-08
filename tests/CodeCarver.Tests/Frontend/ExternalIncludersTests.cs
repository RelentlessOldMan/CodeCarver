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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ExternalIncluders.Find(files, dirs, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadAllText);
            sw.Stop();
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);
            Assert.Equal(3000 + 3000, r.FilesScanned);
            // Probing each (dir, name) with File.Exists took 13 s here on Windows; from directory listings, under 1 s.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
