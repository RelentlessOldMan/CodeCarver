using CodeCarver.Core.Diagnostics;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

/// <summary>Build errors are sorted into kinds a summary can count: what a cut in the wrong place, a declaration left
/// behind and a dropped definition each look like.</summary>
public sealed class BuildErrorKindTests
{
    [Theory]
    [InlineData("a.c:3:13: error: 'modd_helper' declared 'static' but never defined [-Werror=unused-function]", BuildErrorKind.WarningAsError)]
    [InlineData("a.c:9:1: error: expected ';' before '}' token", BuildErrorKind.Syntax)]
    [InlineData("a.c:9:1: error: unterminated comment", BuildErrorKind.Syntax)]
    [InlineData("a.c:9:2: error: #endif without #if", BuildErrorKind.Syntax)]
    [InlineData("a.c:4:5: error: redefinition of 'modd_x'", BuildErrorKind.Redefinition)]
    [InlineData("a.c:4:5: error: conflicting types for 'modd_x'", BuildErrorKind.Redefinition)]
    [InlineData("a.c:4:5: error: 'modd_y' undeclared (first use in this function)", BuildErrorKind.Undeclared)]
    [InlineData("a.c:4:5: error: unknown type name 'modd_t'", BuildErrorKind.UnknownType)]
    [InlineData("a.c:1:10: fatal error: modd.h: No such file or directory", BuildErrorKind.MissingHeader)]
    public void Kinds(string line, BuildErrorKind kind)
    {
        var e = Assert.Single(BuildErrors.Parse(line));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public void WerrorFlag_IsNamed()
        => Assert.Equal("unused-function", BuildErrors.WerrorFlagOf(BuildErrors.Parse(
               "a.c:3:13: error: 'modd_helper' declared 'static' but never defined [-Werror=unused-function]")[0]));
}
