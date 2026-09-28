using System.Text;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

/// <summary>
/// Boundary tests for the macro-dense-header detector (eval #14). The CLI integration test proves the
/// detector fires end-to-end and prevents the node explosion; these pin the DECISION RULE itself — the
/// ratio threshold, the minimum-line floor, comment/blank handling, CRLF, `#  define` spacing, and the
/// truncated-final-line case — so a tweak to the heuristic can't silently drift.
/// </summary>
public sealed class MacroDensityTests
{
    // Build a body with `defines` #define lines and `other` ordinary code lines, interleaved deterministically.
    private static string Body(int defines, int other, string defineText = "#define REG_{0} 0x{0:X}", string otherText = "int fn_{0}(void){{return {0};}}")
    {
        var sb = new StringBuilder();
        int max = Math.Max(defines, other);
        int d = 0, o = 0;
        for (int i = 0; i < max; i++)
        {
            if (d < defines) { sb.AppendFormat(defineText, d); sb.Append('\n'); d++; }
            if (o < other) { sb.AppendFormat(otherText, o); sb.Append('\n'); o++; }
        }
        return sb.ToString();
    }

    [Fact]
    public void AllDefines_AboveFloor_IsDense()
        => Assert.True(MacroDensity.IsDense(Body(defines: 200, other: 0), prefixTruncated: false));

    [Fact]
    public void PureCode_IsNotDense()
        => Assert.False(MacroDensity.IsDense(Body(defines: 0, other: 200), prefixTruncated: false));

    [Fact]
    public void JustBelowFloor_AllDefines_IsNotDense()
        // < 50 substantive lines: too small to be the multi-MB register map this targets, so never flagged.
        => Assert.False(MacroDensity.IsDense(Body(defines: 49, other: 0), prefixTruncated: false));

    [Fact]
    public void AtFloor_AllDefines_IsDense()
        => Assert.True(MacroDensity.IsDense(Body(defines: 50, other: 0), prefixTruncated: false));

    [Fact]
    public void JustBelowRatio_IsNotDense()
    {
        // 59 defines / 100 substantive = 0.59 < 0.60 threshold.
        Assert.False(MacroDensity.IsDense(Body(defines: 59, other: 41), prefixTruncated: false));
    }

    [Fact]
    public void AtRatio_IsDense()
    {
        // 60 defines / 100 substantive = 0.60 == threshold (>=).
        Assert.True(MacroDensity.IsDense(Body(defines: 60, other: 40), prefixTruncated: false));
    }

    [Fact]
    public void BlankAndCommentLines_DoNotCountAsSubstantive()
    {
        // 60 defines + 200 blank/comment lines: comments/blanks are excluded, so the ratio is still 100%.
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("#define REG_").Append(i).Append(" 1\n");
        for (int i = 0; i < 100; i++) sb.Append('\n');                       // blank
        for (int i = 0; i < 100; i++) sb.Append("// a comment line\n");      // line comment
        for (int i = 0; i < 100; i++) sb.Append("/* block comment start\n"); // block-comment-open line
        Assert.True(MacroDensity.IsDense(sb.ToString(), prefixTruncated: false));
    }

    [Fact]
    public void SpacedHashDefine_Counts_ButDefinedIdentifierDoesNot()
    {
        // `#  define` / `#\tdefine` are define directives; `#ifdef`, `#define`d-lookalikes are not. Build
        // 60 spaced-define lines vs 40 non-define directive lines -> dense iff spacing variants are counted.
        var sb = new StringBuilder();
        for (int i = 0; i < 30; i++) sb.Append("#  define A").Append(i).Append(" 1\n");
        for (int i = 0; i < 30; i++) sb.Append("#\tdefine B").Append(i).Append(" 1\n");
        for (int i = 0; i < 40; i++) sb.Append("#ifdef GUARD").Append(i).Append('\n'); // NOT a define
        Assert.True(MacroDensity.IsDense(sb.ToString(), prefixTruncated: false));
    }

    [Fact]
    public void DefinedWord_IsNotADefineDirective()
    {
        // `#defined` (not a real directive, but tests the whole-word check) must NOT count as #define.
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++) sb.Append("#defineX not a directive ").Append(i).Append('\n');
        Assert.False(MacroDensity.IsDense(sb.ToString(), prefixTruncated: false));
    }

    [Fact]
    public void Crlf_LineEndings_Handled()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++) sb.Append("#define REG_").Append(i).Append(" 1\r\n");
        Assert.True(MacroDensity.IsDense(sb.ToString(), prefixTruncated: false));
    }

    [Fact]
    public void TruncatedPrefix_DropsPartialFinalLine()
    {
        // Simulate a prefix cut mid-line: a trailing partial `#defin` with NO newline. With prefixTruncated,
        // it must be dropped (not counted either way). 60 whole defines + partial -> still dense; the partial
        // isn't miscounted as a non-define that would dilute the ratio.
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("#define REG_").Append(i).Append(" 1\n");
        sb.Append("#defin"); // truncated, no newline
        Assert.True(MacroDensity.IsDense(sb.ToString(), prefixTruncated: true));
    }

    [Fact]
    public void Empty_IsNotDense()
        => Assert.False(MacroDensity.IsDense("", prefixTruncated: false));

    [Fact]
    public void IsMacroDenseHeader_ReadsFileAndDecides()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-density-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var dense = Path.Combine(work, "regs.h");
            var sb = new StringBuilder();
            for (int i = 0; i < 5000; i++) sb.Append("#define REG_").Append(i).Append(" 0x").Append(i.ToString("X4")).Append('\n');
            File.WriteAllText(dense, sb.ToString());
            Assert.True(MacroDensity.IsMacroDenseHeader(dense));

            var code = Path.Combine(work, "code.c");
            var cb = new StringBuilder();
            for (int i = 0; i < 5000; i++) cb.Append("int fn_").Append(i).Append("(void){return ").Append(i).Append(";}\n");
            File.WriteAllText(code, cb.ToString());
            Assert.False(MacroDensity.IsMacroDenseHeader(code));

            // A missing file must not throw — biases to false (parse/keep-whole either way).
            Assert.False(MacroDensity.IsMacroDenseHeader(Path.Combine(work, "nope.h")));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
