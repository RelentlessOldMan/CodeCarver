using System.Text;
using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>Review step 5: what the emitter writes is exactly what the build needs — byte-exact rewrites (E1),
/// header carving that sees assembly (H1), C# carved file-level and closed over references (CS1/CS2), and
/// forceKeepFiles that really keeps (K1).</summary>
public sealed class EmitSoundnessTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-emit-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public string Out => Path.Combine(Root, "out");
        public Work() => Directory.CreateDirectory(Src);
        public void Write(string rel, string text) => WriteBytes(rel, Encoding.UTF8.GetBytes(text));
        public void WriteBytes(string rel, byte[] bytes)
        {
            var p = Path.Combine(Src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        public (int Code, string Out) Carve(string body, string lang = "c", string entry = "main")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Out.Replace('\\', '/')}\"\n[common]\nentryPoints = [\"{entry}\"]\n"
                + $"languages = [\"{lang}\"]\n" + body);
            var so = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, new StringWriter());
            return (code, so.ToString());
        }
        public string Carved(string rel) => Path.Combine(Out, "carved", rel);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    public static IEnumerable<object[]> Encodings => new[]
    {
        new object[] { "latin1", new byte[] { 0xB5, (byte)'s', (byte)' ', 0xB0, (byte)'C' }, "\n" },
        new object[] { "shiftjis", new byte[] { 0x93, 0xFA, 0x96, 0x7B }, "\n" },
        new object[] { "utf8", Encoding.UTF8.GetBytes("µs °C"), "\r\n" },
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void PrunedEmit_IsByteExact_ForSurvivingLines(string name, byte[] literal, string eol)
    {
        using var w = new Work();
        var pre = Encoding.ASCII.GetBytes("const char *unit(void){ return \"");
        var post = Encoding.ASCII.GetBytes($"\"; }}{eol}int dead(void){{ return 1; }}{eol}int main(void){{ unit(); return 0; }}{eol}");
        var bom = name == "utf8" ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>();
        w.WriteBytes("main.c", bom.Concat(pre).Concat(literal).Concat(post).ToArray());
        var (code, o) = w.Carve("carveSourceFileContents = true\n");
        Assert.Equal(0, code);
        var outBytes = File.ReadAllBytes(w.Carved("main.c"));
        var expected = bom.Concat(pre).Concat(literal)
            .Concat(Encoding.ASCII.GetBytes($"\"; }}{eol}int main(void){{ unit(); return 0; }}{eol}")).ToArray();
        Assert.Equal(expected, outBytes);
    }

    [Fact]
    public void HeaderCarve_KeepsDefineUsedOnlyByStartupAsm_AndOwnTypedefs_AndPastes_H1()
    {
        using var w = new Work();
        var sb = new StringBuilder("#ifndef REGS_H\r\n#define REGS_H\r\n");
        for (var i = 0; i < 40000; i++) sb.Append($"#define UNUSED_REG_{i} 0x{i:X}\r\n");
        sb.Append("#define STACK_TOP 0x20008000\r\n#define USED_BY_C 1\r\n#define WIDTH 32\r\ntypedef int reg_t[WIDTH];\r\n");
        sb.Append("#define GLUE(n) REG_##n##_BASE\r\n#define REG_7_BASE 7\r\n#endif\r\n");
        w.Write("regs.h", sb.ToString());
        w.Write("main.c", "#include \"regs.h\"\nint main(void){ return USED_BY_C + GLUE(7); }\n");
        w.Write("startup.S", "#include \"regs.h\"\n.word STACK_TOP\n");
        var (code, o) = w.Carve("carveSourceFileContents = true\ncarveHeaderFileContents = true\n");
        Assert.Equal(0, code);
        var h = File.ReadAllText(w.Carved("regs.h"));
        Assert.Contains("#define STACK_TOP", h);
        Assert.Contains("#define WIDTH", h);
        Assert.Contains("#define REG_7_BASE", h);
        Assert.DoesNotContain("UNUSED_REG_5 ", h);
        Assert.Contains("\r\n", h);
        Assert.DoesNotContain("\r\r", h);
    }

    [Fact]
    public void CSharp_WithCarveSourceFileContents_IsFileLevel_CS1()
    {
        using var w = new Work();
        var calc = "namespace App {\n  public sealed class Calculator {\n    public int Add(int a, int b) => a + b;\n"
                 + "    public int Subtract(int a, int b) => a - b;\n  }\n}\n";
        w.Write("Calculator.cs", calc);
        w.Write("Program.cs", "namespace App { static class P { static void Main() { new Calculator().Add(1,2); } } }\n");
        var (code, o) = w.Carve("carveSourceFileContents = true\n", lang: "csharp", entry: "Main");
        Assert.Equal(0, code);
        Assert.Equal(calc, File.ReadAllText(w.Carved("Calculator.cs")));
        Assert.Contains("C/C++ only", o);
    }

    [Theory]
    [InlineData("enum", "Color.cs", "public enum Color { Red }", "var c = Color.Red;")]
    [InlineData("interface", "IShape.cs", "public interface IShape { }", "IShape s = null;")]
    [InlineData("implicit ctor", "Box.cs", "public class Box { public int W; }", "var b = new Box();")]
    [InlineData("base class", "BaseThing.cs", "public class BaseThing { }", "")]
    public void CSharp_FileLevel_KeepsFilesNamedByKeptCode_CS2(string _, string file, string decl, string use)
    {
        using var w = new Work();
        w.Write(file, "namespace App { " + decl + " }\n");
        w.Write("Program.cs", "namespace App { class Derived : BaseThing { } static class P { static void Main() { " + use + " } } }\n");
        w.Write("Unused.cs", "namespace App { class Unused { void Nope() { } } }\n");
        var (code, o) = w.Carve("", lang: "csharp", entry: "Main");
        Assert.Equal(0, code);
        Assert.True(File.Exists(w.Carved(file)), o);
        Assert.False(File.Exists(w.Carved("Unused.cs")), o);
    }

    [Fact]
    public void CSharp_TopLevelStatements_AreAnEntry()
    {
        using var w = new Work();
        w.Write("Program.cs", "System.Console.WriteLine(Helper.Go());\n");
        w.Write("Helper.cs", "static class Helper { public static int Go() => 1; }\n");
        var (code, o) = w.Carve("", lang: "csharp", entry: "Go");
        Assert.Equal(0, code);
        Assert.True(File.Exists(w.Carved("Program.cs")), o);
    }

    [Fact]
    public void ForceKeepFiles_RestoresDroppedCode_WithItsCallees_K1()
    {
        using var w = new Work();
        w.Write("main.c", "int main(void){ return 0; }\n");
        w.Write("dead.c", "void helper(void);\nvoid dead(void){ helper(); }\n");
        w.Write("helper.c", "void helper(void){}\n");
        w.Write("tools/only.c", "int tool(void){ return 0; }\n");
        var (code, o) = w.Carve("forceKeepFiles = [\"dead.c\", \"tools/only.c\"]\nexcludeDirectories = [\"tools\"]\n");
        Assert.Equal(0, code);
        Assert.True(File.Exists(w.Carved("dead.c")), o);
        Assert.True(File.Exists(w.Carved("helper.c")), o);       // the forced file's callee came with it
        Assert.True(File.Exists(w.Carved("tools/only.c")), o);   // forced under an excluded directory still works
    }
}
