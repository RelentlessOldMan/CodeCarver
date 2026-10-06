using CodeCarver.Cli;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Definition heads a sweep of shapes found dropped after the 1.0.162 work eval: the function never became a node,
/// so its file was dropped while main still called it. Each is carved in a C-only tree (C grammar) and in a mixed
/// C/C++ tree (.c files read with the C++ grammar).
/// </summary>
public sealed class DefinitionShapeTests
{
    public static TheoryData<string, string> Shapes() => new()
    {
        { "unknown macro between type and name", "int WINAPI f(void) { return helper(); }\n" },
        { "NORETURN macro between", "void NORETURN f(void) { helper(); for (;;); }\n" },
        { "pointer then macro", "char * FAR_PTR f(void) { helper(); return 0; }\n" },
        { "macro type then macro", "STATUS CALLCONV f(void) { return helper(); }\n" },
        { "AUTOSAR FUNC", "FUNC(void, COM_CODE) f(void) { helper(); }\n" },
        { "AUTOSAR FUNC and P2VAR", "FUNC(Std_ReturnType, COM_CODE) f(P2VAR(uint8, AUTOMATIC, COM_APPL_DATA) p) { return helper(); }\n" },
        { "pragma before the body", "int f(void)\n#pragma optimize\n{\n  return helper();\n}\n" },
        { "return type chosen by #ifdef", "#ifdef WIDE\nlong\n#else\nint\n#endif\nf(void)\n{\n  return helper();\n}\n" },
        { "K&R function-pointer declaration", "f(a, cb)\n  int a;\n  int (*cb)();\n{\n  return cb(a) + helper();\n}\n" },
        { "implicit int, function-pointer parameter", "f(int (*cb)(void)) { return cb() + helper(); }\n" },
        { "PROTO wrapper", "#define PROTO(x) x\nint f PROTO((int a, int b))\n{\n  return a + b + helper();\n}\n" },
        { "__P wrapper, wrapper defined elsewhere", "int f __P((void)) { return helper(); }\n" },
        { "_ANSI_ARGS_ wrapper, macro type", "STATUS f _ANSI_ARGS_((P2VAR(uint8, AUTOMATIC, X) p)) { return helper(); }\n" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Definition_IsRecognised_InCAndMixedTrees(string shape, string source)
    {
        foreach (var mixed in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "cc-shape-" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                File.WriteAllText(Path.Combine(src, "main.c"), "int f();\nint main(void) { return f(); }\n");
                File.WriteAllText(Path.Combine(src, "f.c"), source);
                File.WriteAllText(Path.Combine(src, "helper.c"), "int helper(void) { return 0; }\n");
                File.WriteAllText(Path.Combine(src, "dead.c"), "int dead(void) { return 0; }\n");
                if (mixed) File.WriteAllText(Path.Combine(src, "x.cpp"), "int unused_cpp() { return 0; }\n");
                var cfg = Path.Combine(root, "carve.toml");
                File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace("\\", "/")}\"\n"
                                       + "[common]\nentryPoints = [\"main\"]\nlanguages = " + (mixed ? "[\"c\", \"cpp\"]" : "[\"c\"]") + "\n");
                var so = new StringWriter(); var se = new StringWriter();
                var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
                var why = $"{shape} ({(mixed ? "mixed" : "c")}):\n{so}\n{se}";
                Assert.True(code == 0, why);
                Assert.True(File.Exists(Path.Combine(root, "out", "carved", "f.c")), why);
                Assert.True(File.Exists(Path.Combine(root, "out", "carved", "helper.c")), why);   // the body's calls count
                Assert.False(File.Exists(Path.Combine(root, "out", "carved", "dead.c")), why);
            }
            finally { TempDir.Delete(root); }
        }
    }

    [Theory]
    [InlineData("int f(void) { return 0; }\n")]                                   // nothing to do
    [InlineData("static const uint8_t f(void) { return 0; }\n")]                 // one type name: kept
    [InlineData("int f(void);\nint x = g(1);\n")]                                // no body
    [InlineData("int f PROTO((int a, int b));\n")]                               // a wrapped prototype: no body
    [InlineData("typedef int WINAPI_T fn(void);\n")]                             // a typedef
    [InlineData("int g(void) {\n  int WINAPI f(void) { }\n}\n")]                  // not at file scope
    [InlineData("#ifdef A\nvoid helper_a(int x)\n#else\nvoid helper(int x)\n#endif\n{\n}\n")]   // split head: pass 1a's
    public void Normalize_LeavesOtherShapesAlone(string src)
    {
        Assert.Same(src, HeadNormalizer.Normalize(src, out var removed));
        Assert.Equal("", removed);
    }

    [Fact]
    public void Normalize_KeepsLines_AndReturnsWhatItBlanked()
    {
        const string src = "#ifdef WIDE\nlong\n#else\nint\n#endif\nf(void)\n#pragma once_more\n{\n  return 0;\n}\nint WINAPI g(void) { return 0; }\n";
        var parse = HeadNormalizer.Normalize(src, out var removed);
        Assert.Equal(src.Split('\n').Length, parse.Split('\n').Length);
        Assert.Equal(src.Length, parse.Length);
        Assert.DoesNotContain("#", parse);
        Assert.Contains("long", parse);
        Assert.DoesNotContain("WINAPI", parse);
        Assert.Contains("WINAPI", removed);
        Assert.Contains("int g(void)", parse.Replace("        ", " ").Replace("  ", " "));
    }
}
