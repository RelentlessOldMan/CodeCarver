using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>
/// The static header carve's time limit (<c>[advanced] headerCarveMinutes</c>): past it the carve gives up as it does past
/// its memory limit, leaving the big headers whole. Work's 1.0.345 max stage read its big headers for 2 hours and kept
/// every define.
/// </summary>
public sealed class HeaderCarverTimeLimitTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-hct-" + Guid.NewGuid().ToString("N"));
    public HeaderCarverTimeLimitTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    const string Chip = "#ifndef KQ_CHIP_H\n#define KQ_CHIP_H\n#define KQ_REG_0 0x100\n#define KQ_REG_1 0x104\n#endif\n";

    [Fact]
    public void PastItsTimeLimit_LeavesTheHeadersWhole()
    {
        File.WriteAllText(Path.Combine(_dir, "chip.h"), Chip);
        File.WriteAllText(Path.Combine(_dir, "main.c"), "#include \"chip.h\"\nint main(void){ return KQ_REG_1; }\n");
        var keys = new Dictionary<string, long>();

        var res = HeaderCarver.Carve(_dir, new[] { "chip.h" }, partDone: (k, v) => keys[k] = v, timeLimit: TimeSpan.FromTicks(1));

        Assert.NotNull(res.AbandonedIn);
        Assert.True(res.AbandonedOverTime);
        Assert.Equal(1, keys["abandoned"]);
        Assert.Equal(1, keys["abandonedOverTime"]);
        Assert.Equal(Chip, File.ReadAllText(Path.Combine(_dir, "chip.h")));
    }

    [Fact]
    public void WithinItsTimeLimit_Carves()
    {
        File.WriteAllText(Path.Combine(_dir, "chip.h"), Chip);
        File.WriteAllText(Path.Combine(_dir, "main.c"), "#include \"chip.h\"\nint main(void){ return KQ_REG_1; }\n");

        var res = HeaderCarver.Carve(_dir, new[] { "chip.h" }, timeLimit: TimeSpan.FromMinutes(30));

        Assert.Null(res.AbandonedIn);
        Assert.DoesNotContain("KQ_REG_0", File.ReadAllText(Path.Combine(_dir, "chip.h")));
    }
}
