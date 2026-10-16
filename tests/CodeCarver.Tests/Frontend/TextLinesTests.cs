using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

public class TextLinesTests
{
    // Review CPU C14: LineOf replaced counting the newlines from offset 0. Every offset, before, at and past each
    // newline and past the end, answers as the count did.
    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData("a\nbc\r\n\nd")]
    [InlineData("int f(void);\nint g(void) { return 0; }\n")]
    public void LineOf_IsOnePlusTheNewlinesBefore(string text)
    {
        var lines = new TextLines(text);
        for (var offset = -1; offset <= text.Length + 2; offset++)
        {
            var expected = 1;
            for (var k = 0; k < offset && k < text.Length; k++) if (text[k] == '\n') expected++;
            Assert.Equal(expected, lines.LineOf(offset));
        }
    }
}
