using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// File-scope data definitions as the link check reads them from a translation unit: what a declaration defines, and
/// what only looks like a definition (a base class, an enum's underlying type, a template argument, a K&amp;R parameter,
/// a function returning a function pointer). A wrong name is a false verify FAIL; a missed one hides a real failure.
/// </summary>
public sealed class EmittedLinkCheckDataTests
{
    static List<string> Data(string text) =>
        EmittedLinkCheck.Scan(text).Definitions.Where(d => d.Data).Select(d => d.Name).ToList();

    [Theory]
    [InlineData("class Derived : public Base { int v; };\n", "Base")]
    [InlineData("struct Derived : Base, private Other { };\n", "Other")]
    [InlineData("enum class mode : u8 { MODE_A, MODE_B };\n", "u8")]
    [InlineData("enum mode : u8 { MODE_A };\n", "u8")]
    [InlineData("int f(a, len) int a; int len; { return a + len; }\n", "len")]
    [InlineData("int f(a, len) int a; int len; { return a + len; }\n", "a")]
    [InlineData("void (*sig(int))(int);\n", "sig")]
    public void TypeNamesAndParameters_AreNotData(string text, string notData)
    {
        Assert.DoesNotContain(notData, Data(text));
    }

    [Theory]
    [InlineData("std::pair<Alpha, Beta> p;\n", "p")]
    [InlineData("std::map<Key,Val> m = {};\n", "m")]
    [InlineData("std::array<int, 4> a, b;\n", "a,b")]
    public void TemplateArguments_AreNotDeclarators(string text, string names)
    {
        Assert.Equal(names.Split(','), Data(text));
    }

    [Theory]
    [InlineData("const int v PROGMEM = 1;\n")]
    [InlineData("int v SECTION(\".noinit\");\n")]
    [InlineData("int v __ALIGNED(4);\n")]
    [InlineData("int v __attribute__((aligned(4)));\n")]
    [InlineData("static const unsigned char v RAM_ATTR FAST_DATA = 3;\n")]
    public void TrailingAttributeMacro_NamesTheVariable(string text)
    {
        Assert.Equal(new[] { "v" }, Data(text));
    }

    [Theory]
    [InlineData("int buf[8] __ALIGNED(4) = {0};\n", "buf")]
    [InlineData("uint32_t STATUS_WORD;\n", "STATUS_WORD")]
    [InlineData("static uint32_t STATUS_WORD;\n", "STATUS_WORD")]
    [InlineData("struct regs BANK0;\n", "BANK0")]
    public void UpperCaseNames_StillRead(string text, string name)
    {
        Assert.Equal(new[] { name }, Data(text));
    }

    [Theory]
    [InlineData("static uint32_t GET_COUNT(void);\n")]
    [InlineData("uint32_t GET_COUNT(void);\n")]
    [InlineData("static int helper __P((int));\n")]
    public void Prototypes_SpelledInCapitals_AreNotData(string text)
    {
        Assert.Empty(Data(text));
    }

    [Theory]
    [InlineData("struct { int a; } cfg;\n", "cfg")]
    [InlineData("union { int i; float f; } u = { 1 };\n", "u")]
    [InlineData("struct cfg { int a; } board = { 1 };\n", "board")]
    public void AnonymousAndInlineTypes_DefineTheirVariables(string text, string name)
    {
        Assert.Equal(new[] { name }, Data(text));
    }

    [Fact]
    public void UnbalancedBracesFromIfBranches_DoNotStopTheScan()
    {
        const string text = "#if WIDE\nvoid set(int x) {\n#else\nvoid set(long x) {\n#endif\n  (void)x;\n}\nint after = 2;\n";
        Assert.Contains("after", Data(text));
    }

    [Theory]
    [InlineData("struct node *next(struct node *n) const { return n; }\nint after;\n")]
    [InlineData("auto size() noexcept -> int { return 1; }\nint after;\n")]
    [InlineData("struct node *walk(int n) override { return 0; }\nint after;\n")]
    public void FunctionHeadsWithTrailingWords_DoNotSwallowTheNextStatement(string text)
    {
        Assert.Equal(new[] { "after" }, Data(text));
    }

    [Fact]
    public void FileScopeLambda_DefinesItsVariable()
    {
        Assert.Equal(new[] { "handler", "after" }, Data("auto handler = [](int x) { return x + 1; };\nint after;\n"));
    }

    [Fact]
    public void Run_BaseClassInADroppedFile_IsNotAViolation()
    {
        // Kept code naming `Base` (a type from a header) must not fail because a dropped file derives from it.
        var root = Path.Combine(Path.GetTempPath(), "cc-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kept = Path.Combine(root, "main.cpp");
            var dropped = Path.Combine(root, "derived.cpp");
            File.WriteAllText(kept, "int use(Base *b) { return b != 0; }\n");
            File.WriteAllText(dropped, "class Derived : public Base { };\nenum class E : u8 { A };\n");
            var r = EmittedLinkCheck.Run(new[] { ("main.cpp", kept) }, new[] { ("derived.cpp", dropped) });
            Assert.Empty(r.Hard);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Run_AttributedScalarInADroppedFile_IsAViolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kept = Path.Combine(root, "main.c");
            var dropped = Path.Combine(root, "data.c");
            File.WriteAllText(kept, "extern const int level;\nint main(void) { return level; }\n");
            File.WriteAllText(dropped, "const int level PROGMEM = 3;\n");
            var r = EmittedLinkCheck.Run(new[] { ("main.c", kept) }, new[] { ("data.c", dropped) });
            Assert.Contains(r.Hard, v => v.Name == "level");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
