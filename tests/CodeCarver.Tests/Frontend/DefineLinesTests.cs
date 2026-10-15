using System.Text;
using System.Text.RegularExpressions;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>The hand define scan must find exactly what the regexes it replaced found.</summary>
public sealed class DefineLinesTests
{
    private static readonly Regex ObjectLikeDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]+((?:\\\r?\n|[^\r\n])*)", RegexOptions.Multiline);
    private static readonly Regex ValuelessDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]*\r?$", RegexOptions.Multiline);
    private static readonly Regex FuncLikeDefine = new(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(", RegexOptions.Multiline);
    private static readonly Regex AnyDefine = new(
        @"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(\()?((?:[^\n\\]|\\\r?\n|\\.)*)", RegexOptions.Multiline);

    private static void Same(string text)
    {
        var objects = new List<(string, string)>();
        var valueless = new List<string>();
        var fnLike = new List<string>();
        var any = new List<(string, bool, string)>();
        DefineLines.Scan(text, objects, valueless, fnLike, any);
        Assert.Equal(ObjectLikeDefine.Matches(text).Select(m => (m.Groups[1].Value,
            Regex.Replace(m.Groups[2].Value, @"\\\r?\n", " ").Replace("\r", " ").Replace("\n", " ").Trim())), objects);
        Assert.Equal(ValuelessDefine.Matches(text).Select(m => m.Groups[1].Value), valueless);
        Assert.Equal(FuncLikeDefine.Matches(text).Select(m => m.Groups[1].Value), fnLike);
        Assert.Equal(AnyDefine.Matches(text).Select(m => (m.Groups[1].Value, m.Groups[2].Success, m.Groups[3].Value)), any);
    }

    [Fact]
    public void Defines_FoundAsTheRegexesFindThem()
    {
        Same("");
        Same("#define A 1\n#define B\n#define C(x) ((x)+1)\n  # define D \\\n   2 \\\n   3\nint x;\n#define E\t\r\n#define F(a,b)\\\r\n a+b\r\n");
        Same("#define G x \\\n#define H y\n#define I \\\n\n#define J\n#defineK 1\n# define\tL\t \r\n#define M\\q\n#define N \\");
        Same("#define O  \n#define P\r\r\n#define Q+1\n#define é 1\n#define Rä 2\n/* #define S 3 */\n  #define T(");
    }

    [Fact]
    public void RandomText_FoundAsTheRegexesFindThem()
    {
        var pieces = new[] { "#", "define", " ", "\t", "\n", "\r\n", "\r", "\\", "\\\n", "\\\r\n", "A", "b_1", "(", ")", "x", "é", "9", "#define ", "# define\t", "\n#define X" };
        var rng = new Random(4321);
        for (var round = 0; round < 5000; round++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 30);
            for (var i = 0; i < len; i++) sb.Append(pieces[rng.Next(pieces.Length)]);
            Same(sb.ToString());
        }
    }
}
