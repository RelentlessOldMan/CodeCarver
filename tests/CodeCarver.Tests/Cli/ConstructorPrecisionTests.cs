using CodeCarver.Cli;
using CodeCarver.Core.Reachability;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>Review P5: a C++ constructor is rooted only when its class is named in text that will be emitted.</summary>
public sealed class ConstructorPrecisionTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-ctor-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work() => Directory.CreateDirectory(Src);
        public void W(string rel, string text)
        {
            var p = Path.Combine(Src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        public (int Code, string Out, string Err) Carve(string stages = "")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, "out").Replace('\\', '/')}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"cpp\"]\n" + stages);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so.ToString(), se.ToString());
        }
        public bool Emitted(string rel, string stage = "") =>
            File.Exists(Path.Combine(Root, "out", stage, "carved", rel));
        public string Text(string rel, string stage = "") => File.ReadAllText(Path.Combine(Root, "out", stage, "carved", rel));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    const string WidgetH = "#pragma once\nclass Widget {\npublic:\n  Widget();\n  Widget(const Widget& o);\n  ~Widget();\n  void run();\n};\n";
    const string WidgetCpp = "#include \"widget.h\"\nvoid helper();\nWidget::Widget(){ helper(); }\nWidget::Widget(const Widget& o){ (void)o; }\n"
                           + "Widget::~Widget(){}\nvoid Widget::run(){}\n";

    [Fact]
    public void UnusedClass_ItsConstructorAndWhatItCalls_AreDropped()
    {
        using var w = new Work();
        w.W("widget.h", WidgetH);
        w.W("widget.cpp", WidgetCpp);
        w.W("helper.cpp", "void helper(){}\n");
        w.W("main.cpp", "int main(){ return 0; }\n");
        var (code, o, e) = w.Carve();
        Assert.Equal(0, code);
        Assert.False(w.Emitted("widget.cpp"), o + e);
        Assert.False(w.Emitted("helper.cpp"), o + e);
    }

    [Fact]
    public void UsedClass_ConstructorAndItsCallees_AreKept()
    {
        using var w = new Work();
        w.W("widget.h", WidgetH);
        w.W("widget.cpp", WidgetCpp);
        w.W("helper.cpp", "void helper(){}\n");
        w.W("main.cpp", "#include \"widget.h\"\nint main(){ Widget x; x.run(); return 0; }\n");
        var (code, o, e) = w.Carve();
        Assert.Equal(0, code);
        Assert.True(w.Emitted("widget.cpp"), o + e);
        Assert.True(w.Emitted("helper.cpp"), o + e);
    }

    [Fact]
    public void ClassNamedOnlyInAnotherHeader_IsTreatedAsUsed()
    {
        // A header is emitted whole wherever it is included, so a mention there (a member of another class,
        // an extern instance) releases the constructor even if no kept .cpp names it.
        using var w = new Work();
        w.W("widget.h", WidgetH);
        w.W("widget.cpp", WidgetCpp);
        w.W("helper.cpp", "void helper(){}\n");
        w.W("holder.h", "#include \"widget.h\"\nstruct Holder { Widget w; };\n");
        w.W("main.cpp", "int main(){ return 0; }\n");
        var (code, o, e) = w.Carve();
        Assert.Equal(0, code);
        Assert.True(w.Emitted("helper.cpp"), o + e);
    }

    [Fact]
    public void SingletonBuiltInsideItsOwnMember_KeepsTheConstructor_AtThePrunedStage()
    {
        using var w = new Work();
        w.W("reg.h", "#pragma once\nclass Reg {\npublic:\n  static Reg& get();\n  void touch();\nprivate:\n  Reg();\n};\n");
        w.W("reg.cpp", "#include \"reg.h\"\nvoid helper();\nReg& Reg::get(){ static Reg inst; return inst; }\nReg::Reg(){ helper(); }\nvoid Reg::touch(){}\n");
        w.W("helper.cpp", "void helper(){}\n");
        w.W("main.cpp", "#include \"reg.h\"\nint main(){ Reg::get().touch(); return 0; }\n");
        var (code, o, e) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.Equal(0, code);
        Assert.Contains("Reg::Reg()", w.Text("reg.cpp", "aggressive"));
        Assert.True(w.Emitted("helper.cpp", "aggressive"), o + e);
    }

    [Theory]
    [InlineData("class T { T(); T(const T&); ~T(); };", false)]          // own head and own class body
    [InlineData("class T;\nfriend class T;", false)]                      // forward declarations
    [InlineData("template<class T, class U = int> struct Box;", false)]   // template parameter declaration
    [InlineData("T::T(){}\nT::~T(){}\nint v = T::count;", false)]        // definitions and qualifiers
    [InlineData("T x;", true)]
    [InlineData("struct T x;", true)]                                     // elaborated use
    [InlineData("auto p = new T;", true)]
    [InlineData("std::vector<T> v;", true)]
    [InlineData("class D : public T {};", true)]                          // a base is constructed by D
    [InlineData("class T { static T& get() { static T i; return i; } };", true)] // member body
    [InlineData("#define MAKE() T()\n", true)]                            // macro body
    [InlineData("// T x;\nconst char* s = \"T\";", false)]                // comments and strings
    public void InstantiatingMentions(string text, bool expected)
    {
        var found = ConstructorGate.InstantiatingMentions(text, new HashSet<string> { "T" });
        Assert.Equal(expected, found.Contains("T"));
    }
}
