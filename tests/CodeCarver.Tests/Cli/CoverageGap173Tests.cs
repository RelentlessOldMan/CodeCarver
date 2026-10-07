using CodeCarver.Cli;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Preprocess;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Branches of the 1.0.161-1.0.173 changes the 1.0.174 coverage run did not reach.</summary>
public sealed class CoverageGap173Tests
{
    // The verify diagnosis of a definition that never became a node: each shape name for its head.
    [Theory]
    [InlineData("REG(x) int f(int a) { return a; }", 1, "macroCallBeforeName")]
    [InlineData("int f(a, b)\nint a; int b;\n{ return a; }", 1, "kAndRDeclarations")]
    [InlineData("int f(int a) ODD_ATTR { return a; }", 1, "wordsAfterParameters")]
    [InlineData("f(int a) { return a; }", 1, "noReturnType")]
    [InlineData("int f(int (*cb)(void)) { return cb(); }", 1, "functionPointerParameter")]
    [InlineData("int f(int a, P(b)) { return a; }", 1, "macroCallInParameters")]
    [InlineData("int f(int a;", 1, "unbalancedParameters")]
    [InlineData("int f(int a);", 1, "noBodyAfterHead")]
    [InlineData("int g(void) { return 0; }", 1, "nameNotOnLine")]
    [InlineData("int f(void) { return 0; }", 7, "nameNotOnLine")]
    [InlineData("static ODD_T ODD_Q f(void) { return 0; }", 1, "extraWordBeforeName")]
    [InlineData("struct s ODD f(void) { return 0; }", 1, "extraWordBeforeName")]
    [InlineData("int f(int a\n#ifdef X\n, int b\n#endif\n) { return a; }", 1, "directiveInHead")]
    public void DefinitionHead_NamesTheShape(string text, int line, string shape)
        => Assert.Contains(shape, DefinitionHead.TextShapes(text, line, "f"));

    [Fact]
    public void DefinitionHead_PlainHead_HasNoShape()
        => Assert.Empty(DefinitionHead.TextShapes("static unsigned int f(int a) { return a; }", 1, "f"));

    [Theory]
    [InlineData("void g(void) { int f(void); }", 1, "insideABody")]
    [InlineData("void g(void) { int f(void); }", 1, "parsedAsDeclaration")]
    [InlineData("int g(void) { return 0; }", 1, "parserSawNoName")]
    [InlineData("typedef int f;\nf x(void) { return 0; }", 2, "nameReadAsType")]
    [InlineData("int a = (;\nint b = );\nint c = (;\nint q = 1; int f(void) { return 0; } }", 4, "parsedAsDefinitionButRejected")]
    [InlineData("struct { int a\nint b\nint c\nint d\nint f(void) { return 0; }", 5, "parsedAs_function_declarator")]
    public void DiagnoseDefinition_NamesWhatTheParserMade(string text, int line, string shape)
    {
        using var fe = new CFrontEnd();
        var shapes = fe.DiagnoseDefinition("x.c", text, line, "f");
        Assert.True(shapes.Contains(shape), string.Join(",", shapes));
    }

    // Python triple-quoted strings carry generated C: on one line, and spanning lines.
    [Fact]
    public void GeneratedCode_PythonTripleQuotes()
    {
        var py = "out.write(\"\"\"int x = one_line_hook(1);\"\"\")\n"
               + "out.write('''\n"
               + "void g(void) {\n"
               + "    spanning_hook();\n"
               + "}\n"
               + "''')\n"
               + "not_in_a_string(3)\n";
        var names = GeneratedCode.Calls(py, template: false).Select(c => c.Name).ToList();
        Assert.Contains("one_line_hook", names);
        Assert.Contains("spanning_hook", names);
        Assert.DoesNotContain("not_in_a_string", names);
    }

    // Trigraphs are rewritten in code only: never inside a comment, a string or a character literal.
    [Fact]
    public void Trigraphs_LeaveCommentsAndLiteralsAlone()
    {
        var src = "/* ??( */ // ??)\nchar *s = \"??<\"; char c = '??!'; int a??(2??);";
        var r = SourceText.Trigraphs(src);
        Assert.Contains("/* ??( */ // ??)", r);
        Assert.Contains("\"??<\"", r);
        Assert.Contains("'??!'", r);
        Assert.Contains("a  [2  ]", r);
        Assert.Equal(src.Length, r.Length);
    }

    [Fact]
    public void TemplateInstances_Expand_Stringizes()
    {
        var macros = new Dictionary<string, (List<string>? Params, string Body)>
        {
            ["STR"] = (new List<string> { "x" }, "#x"),
            ["NAME"] = (new List<string> { "p" }, "p ## _impl"),
        };
        Assert.Equal("\"a \\\"b\\\"\"", TemplateInstances.Expand("STR(a \"b\")", macros));
        Assert.Equal("open_impl", TemplateInstances.Expand("NAME(open)", macros));
    }

    // C++: "<::" is "< ::", not a digraph "[:" - unless followed by ':' or '>'.
    [Fact]
    public void Digraphs_LeaveTemplateScopeAlone()
    {
        Assert.Equal("std::vector<::ns::t> v;", Digraphs.Rewrite("std::vector<::ns::t> v;"));
        Assert.Equal("int a[ 2]  ;", Digraphs.Rewrite("int a<:2:> ;"));
    }

    [Fact]
    public void Why_UnknownSymbol_SaysNoNodeAndExits1()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void) { return 0; }\n");
        Assert.Equal(0, t.Carve(log: false).Code);
        var so = new StringWriter(); var se = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", t.Src, "--config", Path.Combine(t.Root, "carve.toml"), "--why", "ns::Widget::nowhere" }, so, se);
        Assert.Equal(1, code);
        Assert.Contains("no symbol named 'nowhere'", se.ToString());
    }

    // analysisOnly verifies what a normal emit would write, including the assembly files that define what C calls.
    [Fact]
    public void AnalysisOnly_CountsAssemblyFiles()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int fast_sum(int);\nint main(void) { return fast_sum(1); }\n")
         .W("fast.S", ".globl fast_sum\nfast_sum:\n  ret\n");
        var (code, _, err) = t.Carve(log: false, common: "", extraToml: "");
        Assert.Equal(0, code);
        // Same tree, analysis only: verify must agree (the .S would be copied, so fast_sum is defined).
        var cfg = Path.Combine(t.Root, "carve.toml");
        File.WriteAllText(cfg, "analysisOnly = true\n" + File.ReadAllText(cfg));
        var so = new StringWriter(); var se = new StringWriter();
        Assert.Equal(0, CarveCommand.Run(new[] { "carve", t.Src, "--config", cfg }, so, se));
    }
}
