using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

public sealed class ExternalIncludersTests
{
    /// <summary>A whole product's worth of outside code against a long -I list: each include name is searched
    /// once, not once per file that includes it.</summary>
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
            dirs.Add(root);
            var common = string.Concat(Enumerable.Range(0, 25).Select(i => $"#include <sys_{i}.h>\n#include \"cfg_{i}.h\"\n"));
            var files = new List<string>();
            for (var i = 0; i < 3000; i++)
            {
                var f = Path.Combine(work, "glue", "g" + (i % 30), $"glue_{i}.c");
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, common + (i == 1234 ? "#include \"pub/mod_api.h\"\n" : "") + "int x;\n");
                files.Add(f);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ExternalIncluders.Find(files, dirs, root, new[] { "pub/mod_api.h" }, _ => false, File.ReadAllText);
            sw.Stop();
            Assert.Equal(new[] { "pub/mod_api.h" }, r.Headers);
            Assert.Equal(3000, r.FilesScanned);
            // Uncached this is 3000 x 51 x 301 = 46M probes (many minutes); cached it is about 15k.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
