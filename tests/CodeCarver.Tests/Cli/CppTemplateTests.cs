using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>C++ definitions the front end's name patterns missed (review X4, X8).</summary>
public sealed class CppTemplateTests
{
    [Fact]
    public void ExplicitSpecialization_IsADefinition()
    {
        // Review X4: `template<> int codec<int>(int)` defines codec; its unit was dropped.
        using var t = new TreeCarve()
            .W("codec.h", "template <typename T> T codec(T v);\n")
            .W("codec_int.cpp", "#include \"codec.h\"\ntemplate <> int codec<int>(int v) { return v + 1; }\n")
            .W("main.cpp", "#include \"codec.h\"\nint main() { return codec<int>(1); }\n");
        t.Compile("codec_int.cpp").Compile("main.cpp");
        var r = t.Carve(languages: "\"cpp\"");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("codec_int.cpp"), r.Out + r.Err);
    }

    [Fact]
    public void ExplicitInstantiation_KeepsItsTemplate()
    {
        // Review X4: the instantiation stays, so the template it instantiates must too; the intra carve removed it.
        using var t = new TreeCarve()
            .W("twice.h", "template <typename T> T twice(T v);\nint used();\n")
            .W("twice.cpp", "#include \"twice.h\"\ntemplate <typename T> T twice(T v) { return v + v; }\ntemplate int twice<int>(int);\nint used() { return 1; }\n")
            .W("main.cpp", "#include \"twice.h\"\nint main() { return used(); }\n");
        t.Compile("twice.cpp").Compile("main.cpp");
        var r = t.Carve(languages: "\"cpp\"", common: "carveSourceFileContents = true\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("return v + v;", File.ReadAllText(t.CarvedPath("twice.cpp")));
    }

    [Fact]
    public void RangeForAndStructuredBinding_CallWhatTheyUse()
    {
        // Review X5: `for (x : r)` calls begin/end, `auto [a, b] = p` calls get; nothing spells the calls out.
        using var t = new TreeCarve()
            .W("range.h", "struct Range { int *b, *e; };\nint *begin(Range &r);\nint *end(Range &r);\n"
                + "struct Pair { int a, b; };\nnamespace std { template <unsigned long> struct tuple_element; template <class> struct tuple_size; }\n"
                + "template <unsigned long I> int get(const Pair &p);\n")
            .W("range_begin.cpp", "#include \"range.h\"\nint *begin(Range &r) { return r.b; }\n")
            .W("range_end.cpp", "#include \"range.h\"\nint *end(Range &r) { return r.e; }\n")
            .W("pair_get.cpp", "#include \"range.h\"\ntemplate <unsigned long I> int get(const Pair &p) { return I ? p.b : p.a; }\ntemplate int get<0>(const Pair &);\n")
            .W("main.cpp", "#include \"range.h\"\nint main() { int v[2] = {1, 2}; Range r{v, v + 2}; int s = 0;\n  for (int x : r) s += x;\n"
                + "  Pair p{1, 2}; auto [a, b] = p; return s + a + b; }\n");
        foreach (var f in new[] { "range_begin.cpp", "range_end.cpp", "pair_get.cpp", "main.cpp" }) t.Compile(f);
        var r = t.Carve(languages: "\"cpp\"");
        Assert.True(r.Code == 0, r.Out + r.Err);
        foreach (var f in new[] { "range_begin.cpp", "range_end.cpp", "pair_get.cpp" })
            Assert.True(t.Kept(f), f + "\n" + r.Out + r.Err);
    }

    [Theory]
    [InlineData("  auto f = [](int other) { return other + 1; };\n  return f(1) + other();\n")]
    [InlineData("  int (*fp)(int other) = 0;\n  return (fp ? 1 : 0) + other();\n")]
    public void ParameterOfALambdaOrPointer_HidesNothingAfterIt(string body)
    {
        // Review X6: the parameter `other` hid the function other() for the rest of main.
        using var t = new TreeCarve()
            .W("other.cpp", "int other() { return 2; }\n")
            .W("main.cpp", "int other();\nint main() {\n" + body + "}\n");
        t.Compile("other.cpp").Compile("main.cpp");
        var r = t.Carve(languages: "\"cpp\"");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("other.cpp"), r.Out + r.Err);
    }

    [Fact]
    public void BraceInitialisedObjects_OfAClassWithAConstructor_StayInTheIntraCarve()
    {
        // Review X7: `Registrar arr[] = {{"X"}, {"Y"}};` runs Registrar's constructor twice; the intra carve cut it.
        using var t = new TreeCarve()
            .W("reg.h", "void reg(const char *n);\nstruct Registrar { const char *n; Registrar(const char *x) : n(x) { reg(x); } };\n")
            .W("reg.cpp", "#include \"reg.h\"\nvoid reg(const char *n) {}\n")
            .W("main.cpp", "#include \"reg.h\"\nRegistrar arr[] = {{\"X\"}, {\"Y\"}};\nint unused_fn() { return 1; }\nint main() { return 0; }\n");
        foreach (var f in new[] { "reg.cpp", "main.cpp" }) t.Compile(f);
        var r = t.Carve(languages: "\"cpp\"", common: "carveSourceFileContents = true\n");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("Registrar arr[]", File.ReadAllText(t.CarvedPath("main.cpp")));
        Assert.True(t.Kept("reg.cpp"), r.Out + r.Err);
    }

    [Fact]
    public void MethodQualifiedFiveDeep_IsADefinition()
    {
        // Review X8: only four scopes of `a::b::c::m` were spelled out.
        using var t = new TreeCarve()
            .W("deep.h", "namespace a { namespace b { namespace c { namespace d { struct E { static int m(); }; } } } }\n")
            .W("deep.cpp", "#include \"deep.h\"\nint a::b::c::d::E::m() { return 5; }\n")
            .W("main.cpp", "#include \"deep.h\"\nint main() { return a::b::c::d::E::m(); }\n");
        t.Compile("deep.cpp").Compile("main.cpp");
        var r = t.Carve(languages: "\"cpp\"");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("deep.cpp"), r.Out + r.Err);
    }
}
