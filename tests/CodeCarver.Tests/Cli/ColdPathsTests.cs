using CodeCarver.Core.Emit;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// <c>runLineCoverage</c>: the cold-path report. In functions that ran, the case groups and braced if/else blocks no
/// line of which ran, and what removing them would free. A report only: the carve is the same with or without it.
/// All names invented.
/// </summary>
public sealed class ColdPathsTests
{
    const string Header = "int dispatch(int reg);\nint never(void);\n";
    const string Lib = "#include \"lib.h\"\n"                                  //  1
                     + "static int on_alpha(int x) { return x + 1; }\n"       //  2
                     + "static int on_beta(int x) { return x * 2; }\n"        //  3
                     + "int g_beta_hits;\n"                                    //  4
                     + "static int shared(int x) { return x - 1; }\n"         //  5
                     + "int dispatch(int reg)\n"                               //  6
                     + "{\n"                                                   //  7
                     + "    switch (reg)\n"                                    //  8
                     + "    {\n"                                               //  9
                     + "    case 1:\n"                                         // 10
                     + "        return on_alpha(reg);\n"                       // 11
                     + "    case 2:\n"                                         // 12
                     + "    case 3:\n"                                         // 13
                     + "        g_beta_hits++;\n"                              // 14
                     + "        return on_beta(reg) + shared(reg);\n"          // 15
                     + "    default:\n"                                        // 16
                     + "        break;\n"                                      // 17
                     + "    }\n"                                               // 18
                     + "    if (reg > 100)\n"                                  // 19
                     + "    {\n"                                               // 20
                     + "        return shared(reg);\n"                         // 21
                     + "    }\n"                                               // 22
                     + "    return 0;\n"                                       // 23
                     + "}\n"                                                   // 24
                     + "int never(void) { return 9; }\n";                      // 25
    const string Main = "#include \"lib.h\"\nint main(void) { return dispatch(1) + never(); }\n";

    static TreeCarve Tree()
    {
        var t = new TreeCarve();
        t.W("lib.h", Header).W("lib.c", Lib).W("main.c", Main).Compile("lib.c").Compile("main.c");
        return t;
    }

    const string Stage = "[stages.t]\ncarveSourceFileContents = true\n";

    [Fact]
    public void ColdArms_AreReported_WithWhatOnlyTheyUse()
    {
        using var t = Tree();
        var lib = Path.Combine(t.Src, "lib.c").Replace('\\', '/');
        var cov = Path.Combine(t.Root, "cov.txt");
        // addr2line -p shape (absolute path, discriminator) and a plain relative range.
        File.WriteAllText(cov, "# coverage\n"
                             + $"0x08000100: on_alpha at {lib}:2\n"
                             + $"0x08000120: dispatch at {lib}:6\n"
                             + $"0x08000124: dispatch at {lib}:8 (discriminator 1)\n"
                             + "lib.c:10-11\n"
                             + "main.c:2\n"
                             + "0x08000200: ?? ??:0\n");
        var (code, o, e) = t.Carve(extraToml: $"[runs.r]\nrunLineCoverage = [\"{cov.Replace('\\', '/')}\"]\n" + Stage);
        Assert.True(code == 0, o + e);
        var cc = Path.Combine(t.Root, "out", "t", "codecarver");
        var summary = File.ReadAllText(Path.Combine(cc, "summary.txt"));
        Assert.Contains("cold.arms = 3", summary);              // case 2/3, default, the if block
        Assert.Contains("cold.armsJudged = 4", summary);
        Assert.Contains("cold.freedFunctions = 2", summary);    // on_beta, shared
        Assert.Contains("cold.freedGlobals = 1", summary);      // g_beta_hits
        Assert.Contains("cold.functionsNeverRan = 3", summary); // on_beta, shared, never
        Assert.Contains("cold.coverageUnreadableLines = 1", summary);
        Assert.DoesNotContain("dispatch", summary);             // names stay in the local report

        var report = File.ReadAllText(Path.Combine(cc, "cold-paths.txt"));
        Assert.Contains("lib.c:12-15  case  in dispatch", report);
        Assert.Contains("lib.c:16-17  case  in dispatch", report);
        Assert.Contains("lib.c:21-21  then  in dispatch", report);
        Assert.DoesNotContain("lib.c:10-11", report);
        Assert.Contains("function  on_beta", report);
        Assert.Contains("global  g_beta_hits", report);
        Assert.DoesNotContain("function  on_alpha", report);

        // A report only: the cold code is still in the carve.
        var carved = File.ReadAllText(Path.Combine(t.Root, "out", "t", "carved", "lib.c"));
        Assert.Contains("on_beta(reg) + shared(reg)", carved);
    }

    [Fact]
    public void Coverage_ChangesNoCarve()
    {
        string Sha(string toml)
        {
            using var t = Tree();
            var cov = Path.Combine(t.Root, "cov.txt");
            File.WriteAllText(cov, "lib.c:2\nlib.c:6-11\nmain.c:2\n");
            var (code, o, e) = t.Carve(extraToml: toml.Replace("COV", cov.Replace('\\', '/')) + Stage);
            Assert.True(code == 0, o + e);
            var summary = File.ReadAllLines(Path.Combine(t.Root, "out", "t", "codecarver", "summary.txt"));
            return string.Join("\n", summary.Where(l => l.Contains("emittedContentSha") || l.Contains("keptFilesSha")));
        }
        var with = Sha("[runs.r]\nrunLineCoverage = [\"COV\"]\n");
        var without = Sha("");
        Assert.Contains("emittedContentSha", with);
        Assert.Equal(without, with);
    }

    [Fact]
    public void CoverageFromAnotherCheckout_Warns()
    {
        using var t = Tree();
        var cov = Path.Combine(t.Root, "cov.txt");
        File.WriteAllText(cov, "/build/agent/src/lib.c:6\n/build/agent/src/lib.c:8\n");
        var (code, o, e) = t.Carve(extraToml: $"[runs.r]\nrunLineCoverage = [\"{cov.Replace('\\', '/')}\"]\n" + Stage);
        Assert.True(code == 0, o + e);
        Assert.Contains("runLineCoverage: none of its locations", e);
        Assert.False(File.Exists(Path.Combine(t.Root, "out", "t", "codecarver", "cold-paths.txt")));
    }

    static List<ColdPaths.Arm> Arms(string body, out int notJudged)
    {
        var text = "void f(int r)\n{\n" + body + "}\n";
        var src = new StubBodies.Source(text);
        return ColdPaths.ArmsOf(src, text.IndexOf('{'), text.LastIndexOf('}'), out notJudged);
    }

    [Fact]
    public void Arms_OnlyWhenTheirLinesAreTheirOwn()
    {
        // Unbraced branch, two arms on one line, a label inside a nested block (Duff's device): not judged.
        Assert.Empty(Arms("    if (r) a();\n", out var n1));
        Assert.Equal(1, n1);
        var oneLine = Arms("    switch (r)\n    {\n    case 1: a(); break; case 2: b(); break;\n    }\n", out var n2);
        Assert.Empty(oneLine);
        Assert.True(n2 >= 1);
        Assert.Empty(Arms("    switch (r)\n    {\n    case 0: do {\n        a();\n    case 1:\n        b();\n    } while (0);\n    }\n", out var n3));
        Assert.Equal(1, n3);
        // A nested switch's labels are its own; else-if is its own if.
        var nested = Arms("    switch (r)\n    {\n    case 1:\n        switch (r)\n        {\n        case 7:\n            a();\n            break;\n        }\n        break;\n"
                        + "    }\n    if (r)\n    {\n        a();\n    }\n    else if (r > 1)\n    {\n        b();\n    }\n    else\n    {\n        c();\n    }\n", out var n4);
        Assert.Equal(0, n4);
        Assert.Equal(new[] { (ColdPaths.ArmKind.Case, 5, 12), (ColdPaths.ArmKind.Case, 8, 10), (ColdPaths.ArmKind.Then, 16, 16),
                             (ColdPaths.ArmKind.Then, 20, 20), (ColdPaths.ArmKind.Else, 24, 24) },
                     nested.Select(a => (a.Kind, a.StartLine, a.EndLine)).OrderBy(a => a.StartLine).ThenBy(a => a.Item1));
    }

    [Fact]
    public void Coverage_ReadsAddr2lineAndPlainLines()
    {
        var recs = LineCoverage.Parse(new[] { "0x0800a1c4: run at C:/w/src/a.c:12 (discriminator 3)", "src/b.c:4-6", "12:34:56 tick", "?? ??:0", "", "0x0800a1d0: tick at C:/Program Files/w/c.c:7", "D:/My Tree/d.c:3-4" }, out var bad);
        Assert.Equal(2, bad);
        Assert.Equal(new LineCoverage.Lines("C:/w/src/a.c", 12, 12), recs[0]);
        Assert.Equal(new LineCoverage.Lines("src/b.c", 4, 6), recs[1]);
        Assert.Equal(new LineCoverage.Lines("C:/Program Files/w/c.c", 7, 7), recs[2]);
        Assert.Equal(new LineCoverage.Lines("D:/My Tree/d.c", 3, 4), recs[3]);
    }
}
