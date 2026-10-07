using CodeCarver.Core.Diagnostics;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

public sealed class AnonymizerTests
{
    static (Anonymizer A, string Out) Run(string text)
    {
        var a = new Anonymizer();
        a.Learn(text);
        return (a, a.Code(text));
    }

    [Fact]
    public void Names_AreReplacedConsistently_KeywordsStay()
    {
        var (a, o) = Run("static int uart_init(void) { return uart_init_count; }\nint main(void) { return uart_init(); }\n");
        Assert.DoesNotContain("uart", o);
        Assert.Contains("static int ", o);
        Assert.Contains("int main(void)", o);
        var init = a.Name("uart_init");
        Assert.Equal(2, o.Split(init + "(").Length - 1);
        Assert.Empty(a.Leaks(o));
    }

    [Fact]
    public void WordsMapTheSameInsideEveryName_SoPastedNamesStillMatch()
    {
        var a = new Anonymizer();
        a.Learn("uart_init uart_ init FooInit Foo Init");
        Assert.Equal(a.Name("uart_") + a.Name("init"), a.Name("uart_init"));
        Assert.Equal(a.Name("Foo") + a.Name("Init"), a.Name("FooInit"));
    }

    [Fact]
    public void CaseClass_IsKept()
    {
        var a = new Anonymizer();
        a.Learn("MY_MACRO MyType my_fn __wrap_my_fn __real_my_fn");
        Assert.Matches("^[A-Z][A-Z0-9_]*$", a.Name("MY_MACRO"));
        Assert.Matches("^[A-Z][a-z]", a.Name("MyType"));
        Assert.DoesNotMatch("^[A-Z0-9_]*$", a.Name("MyType"));
        Assert.Matches("^[a-z][a-z0-9_]*$", a.Name("my_fn"));
        Assert.Equal("__wrap_" + a.Name("my_fn"), a.Name("__wrap_my_fn"));
        Assert.Equal("__real_" + a.Name("my_fn"), a.Name("__real_my_fn"));
    }

    [Fact]
    public void LettersAndDigits_RunTogether_AreOneWord()
    {
        var (a, o) = Run("int m7t2w41 = 0;\n");
        Assert.DoesNotContain("m7t2w41", o);
        Assert.DoesNotContain("t2", o);
        Assert.Empty(a.Leaks(o));
    }

    [Fact]
    public void Comments_AreBlanked_LinesKept()
    {
        var src = "int a; // secret_word here\n/* another\n secret */ int b;\n";
        var (_, o) = Run(src);
        Assert.DoesNotContain("secret", o);
        Assert.Equal(src.Split('\n').Length, o.Split('\n').Length);
        Assert.Equal(src.Length, o.Length);
    }

    [Fact]
    public void Strings_AreRewritten_FunctionNamesInStringsMatch()
    {
        var (a, o) = Run("void *p = dlsym(h, \"plugin_entry\"); void plugin_entry(void) {} const char *m = \"Init failed: %08lx\\n\";\n");
        Assert.Contains($"\"{a.Name("plugin_entry")}\"", o);
        Assert.Contains("dlsym(", o);
        Assert.Contains("%08lx\\n", o);
        Assert.DoesNotContain("plugin", o);
        Assert.DoesNotContain("failed", o);
        Assert.Empty(a.Leaks(o));
    }

    [Fact]
    public void Includes_MapAsPaths_SameAsThePathItself_SystemHeadersStay()
    {
        var src = "#include \"drivers/uart_hal.h\"\n#include <stdio.h>\n#include <vendor_sdk.h>\n#define CFG \"board/cfg.h\"\n#include CFG\n";
        var (a, o) = Run(src);
        Assert.Contains($"#include \"{a.Path("drivers/uart_hal.h")}\"", o);
        Assert.Contains("#include <stdio.h>", o);
        Assert.Contains($"<{a.Path("vendor_sdk.h")}>", o);
        Assert.Contains($"\"{a.Path("board/cfg.h")}\"", o);
        Assert.EndsWith(".h", a.Path("drivers/uart_hal.h"));
        Assert.Equal(a.Path("drivers/x.c").Split('/')[0], a.Path("drivers/uart_hal.h").Split('/')[0]);
        Assert.Equal(a.Path("Drivers/UART_HAL.h").ToLowerInvariant(), a.Path("drivers/uart_hal.h").ToLowerInvariant());
        Assert.Empty(a.Leaks(o));
    }

    [Fact]
    public void Numbers_SmallStay_LargeKeepTheirOrder()
    {
        var (_, o) = Run("#if VERSION >= 300\nint a[4] = { 0x40021000, 300, 299, 7 };\n#endif\n");
        Assert.DoesNotContain("40021000", o);
        Assert.Contains("[4]", o);
        Assert.Contains(", 7 }", o);
        var nums = System.Text.RegularExpressions.Regex.Matches(o, @"\b\d+\b").Select(m => int.Parse(m.Value)).ToList();
        // 300 -> b, 0x40021000 -> c, 300 -> b, 299 -> a with a < b < c.
        var n300 = nums[0];
        Assert.True(nums[2] > n300);         // 0x40021000
        Assert.Equal(n300, nums[3]);          // 300 again
        Assert.True(nums[4] < n300);          // 299
    }

    [Fact]
    public void RawStrings_AreRewritten_AndDontLeak()
    {
        var (a, o) = Run("const char *s = R\"x(\n#if 0 secret_thing\n)x\";\nint f(void);\n");
        Assert.DoesNotContain("secret", o);
        Assert.Contains("R\"x(", o);
        Assert.Contains(")x\"", o);
        Assert.Empty(a.Leaks(o));
    }

    [Fact]
    public void Leaks_ReportsAnOriginalWordThatSurvived()
    {
        var a = new Anonymizer();
        a.Learn("int secretive_name;");
        Assert.Contains("secretive", a.Leaks("int secretive_k1;"));
        Assert.Empty(a.Leaks(a.Code("int secretive_name;")));
    }

    [Fact]
    public void GeneratedWords_NeverCollideWithOriginals()
    {
        var a = new Anonymizer();
        a.Learn("int k1, k2, K1, p1; int foo, BAR;");
        Assert.NotEqual("k1", a.Name("foo"));
        Assert.NotEqual("k2", a.Name("foo"));
        Assert.NotEqual("K1", a.Name("BAR"));
        Assert.NotEqual("p1", a.Path("foo.c")[..^2]);
    }

    [Fact]
    public void Text_ForFlagValues_KeepsPunctuation()
    {
        var a = new Anonymizer();
        a.Learn("BOARD_REV=board_rev_2");
        Assert.Equal(a.Name("BOARD_REV") + "=" + a.Name("board_rev_2"), a.Text("BOARD_REV=board_rev_2"));
    }
}
