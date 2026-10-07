using CodeCarver.Core.Diagnostics;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

public sealed class BuildErrorsTests
{
    static BuildError One(string log) => Assert.Single(BuildErrors.Parse(log));

    [Theory]
    // gcc / clang
    [InlineData("src/a.c:12:5: error: implicit declaration of function 'foo_init' [-Wimplicit-function-declaration]", "src/a.c", 12, BuildErrorKind.Undeclared, "foo_init")]
    [InlineData("src/a.c:12:5: error: 'BAR_MAX' undeclared (first use in this function)", "src/a.c", 12, BuildErrorKind.Undeclared, "BAR_MAX")]
    [InlineData("src/a.c:3:1: error: unknown type name 'widget_t'", "src/a.c", 3, BuildErrorKind.UnknownType, "widget_t")]
    [InlineData("src/a.c:2:10: fatal error: drv/uart.h: No such file or directory", "src/a.c", 2, BuildErrorKind.MissingHeader, "drv/uart.h")]
    [InlineData("src/a.c:7: undefined reference to `helper_fn'", "src/a.c", 7, BuildErrorKind.UndefinedReference, "helper_fn")]
    [InlineData("src/a.c:7:3: error: use of undeclared identifier 'zap'", "src/a.c", 7, BuildErrorKind.Undeclared, "zap")]
    [InlineData("src/a.c:7:3: error: call to undeclared function 'zap'; ISO C99 and later do not support implicit function declarations", "src/a.c", 7, BuildErrorKind.Undeclared, "zap")]
    [InlineData("src/a.c:9:14: error: storage size of 'cfg' isn't known", "src/a.c", 9, BuildErrorKind.Other, "cfg")]
    // MSVC
    [InlineData(@"C:\w\src\a.c(12): error C2065: 'BAR_MAX': undeclared identifier", @"C:\w\src\a.c", 12, BuildErrorKind.Undeclared, "BAR_MAX")]
    [InlineData(@"C:\w\src\a.c(2,10): fatal error C1083: Cannot open include file: 'drv/uart.h': No such file or directory", @"C:\w\src\a.c", 2, BuildErrorKind.MissingHeader, "drv/uart.h")]
    // armcc (Keil) and IAR
    [InlineData("\"src\\a.c\", line 12: Error:  #20: identifier \"BAR_MAX\" is undefined", @"src\a.c", 12, BuildErrorKind.Undeclared, "BAR_MAX")]
    [InlineData("\"src\\a.c\",12  Error[Pe020]: identifier \"BAR_MAX\" is undefined", @"src\a.c", 12, BuildErrorKind.Undeclared, "BAR_MAX")]
    [InlineData("\"src\\a.c\",2  Fatal error[Pe1696]: cannot open source file \"drv/uart.h\"", @"src\a.c", 2, BuildErrorKind.MissingHeader, "drv/uart.h")]
    public void CompilerLines(string line, string file, int lineNo, BuildErrorKind kind, string name)
    {
        var e = One(line);
        Assert.Equal(file, e.File);
        Assert.Equal(lineNo, e.Line);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(name, e.Name);
    }

    [Theory]
    [InlineData("/usr/bin/ld: main.o: in function `main':\nmain.c:(.text+0x9): undefined reference to `helper_fn'", "helper_fn")]
    [InlineData("ld.lld: error: undefined symbol: helper_fn\n>>> referenced by main.c:7", "helper_fn")]
    [InlineData("main.obj : error LNK2019: unresolved external symbol helper_fn referenced in function main", "helper_fn")]
    [InlineData("main.obj : error LNK2001: unresolved external symbol _helper_fn", "helper_fn")]
    [InlineData("Error: L6218E: Undefined symbol helper_fn (referred from main.o).", "helper_fn")]
    [InlineData("Error[Li005]: no definition for \"helper_fn\" [referenced from main.o]", "helper_fn")]
    public void LinkerLines_NameTheSymbol(string log, string name)
    {
        var e = BuildErrors.Parse(log).Single(x => x.Kind == BuildErrorKind.UndefinedReference);
        Assert.Equal(name, e.Name);
    }

    [Fact]
    public void Lld_ReferencedBy_GivesTheFile()
    {
        var e = One("ld.lld: error: undefined symbol: helper_fn\n>>> referenced by main.c:7\n>>>               main.o:(main)");
        Assert.Equal("main.c", e.File);
        Assert.Equal(7, e.Line);
    }

    [Fact]
    public void Warnings_NotesAndNoise_AreIgnored_DuplicatesCollapse()
    {
        var log = "src/a.c:3:1: warning: unused variable 'x'\nsrc/a.c:3:1: note: declared here\nmake: *** [all] Error 2\n"
                  + "src/a.c:12:5: error: 'BAR' undeclared\nsrc/a.c:12:5: error: 'BAR' undeclared\nsrc/b.c:1:1: error: 'BAR' undeclared\n";
        var errs = BuildErrors.Parse(log);
        Assert.Equal(2, errs.Count);
        Assert.All(errs, e => Assert.Equal("BAR", e.Name));
    }

    [Fact]
    public void AnErrorWithoutAName_IsStillKept()
    {
        var e = One("src/a.c:5:1: error: expected ';' before '}' token");
        Assert.Equal(BuildErrorKind.Other, e.Kind);
        Assert.Equal(5, e.Line);
    }
}
