using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// C++ code that runs with no call spelled out (review X2): <c>a + b</c>, <c>-v</c>, a conversion <c>double d = m;</c>,
/// a destructor at <c>delete</c>, a replacement <c>operator new</c>. The units defining them are kept when their class
/// is used, and a class nobody uses still takes its operators with it.
/// </summary>
public sealed class CppImplicitCallTests
{
    [Fact]
    public void OperatorsConversionsAndDestructors_KeepTheirUnits()
    {
        using var t = new TreeCarve()
            .W("vec.h", "struct Vec { int x; Vec operator-() const; };\nVec operator+(Vec a, Vec b);\n"
                + "struct Meters { double v; operator double() const; };\n"
                + "struct Shape { virtual ~Shape(); virtual int area() const { return 0; } };\n")
            .W("vec_ops.cpp", "#include \"vec.h\"\nVec operator+(Vec a, Vec b) { return Vec{a.x + b.x}; }\n")
            .W("vec_neg.cpp", "#include \"vec.h\"\nVec Vec::operator-() const { return Vec{-x}; }\n")
            .W("meters.cpp", "#include \"vec.h\"\nMeters::operator double() const { return v; }\n")
            .W("shape.cpp", "#include \"vec.h\"\nShape::~Shape() {}\n")
            .W("shape2.h", "struct Shape2 { virtual ~Shape2(); virtual int area() const { return 2; } };\n")
            .W("shape2.cpp", "#include \"shape2.h\"\nShape2::~Shape2() = default;\n")
            .W("alloc.cpp", "void *my_alloc(unsigned long n);\nvoid *operator new(unsigned long n) { return my_alloc(n); }\n"
                + "void operator delete(void *p) noexcept {}\n")
            .W("my_alloc.cpp", "void *my_alloc(unsigned long n) { static char pool[256]; return pool; }\n")
            .W("unused.cpp", "struct Unused { int u; };\nUnused operator+(Unused a, Unused b) { return a; }\nUnused::~Unused() {}\n")
            .W("main.cpp", "#include \"vec.h\"\n#include \"shape2.h\"\nint main() { Vec a{1}, b{2}; Vec c = -(a + b); Meters m{2}; double d = m;\n"
                + "  Shape *s = new Shape; delete s; Shape2 *s2 = new Shape2; delete s2; return c.x + (int)d; }\n");
        foreach (var f in new[] { "vec_ops.cpp", "vec_neg.cpp", "meters.cpp", "shape.cpp", "shape2.cpp", "alloc.cpp", "my_alloc.cpp", "unused.cpp", "main.cpp" })
            t.Compile(f);
        var r = t.Carve(languages: "\"cpp\"");
        Assert.True(r.Code == 0, r.Out + r.Err);
        foreach (var f in new[] { "vec_ops.cpp", "vec_neg.cpp", "meters.cpp", "shape.cpp", "shape2.cpp", "alloc.cpp", "my_alloc.cpp" })
            Assert.True(t.Kept(f), f + "\n" + r.Out + r.Err);
        Assert.False(t.Kept("unused.cpp"), r.Out + r.Err);
    }
}
