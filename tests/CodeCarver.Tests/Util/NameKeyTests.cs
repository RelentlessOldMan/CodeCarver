using CodeCarver.Core.Util;
using Xunit;

namespace CodeCarver.Tests.Util;

/// <summary>
/// The header carve holds names as keys: a name it skips because its key is taken must not be some other name's key.
/// </summary>
public sealed class NameKeyTests
{
    [Fact]
    public void RegisterShapedNames_AllHaveTheirOwnKey()
    {
        // Names alike but for a digit or two, as register headers hold them, every length through a few 8-byte blocks.
        var full = new HashSet<UInt128>();
        var low = new HashSet<ulong>();
        var count = 0;
        for (var block = 0; block < 400; block++)
            for (var reg = 0; reg < 500; reg++)
                foreach (var name in new[] { $"HWA_B{block}_R{reg}", $"HWA_BLOCK{block}_REG{reg}_FIELD_SHIFT", $"K{block}x{reg}" })
                {
                    count++;
                    Assert.True(full.Add(NameKey.Of(name)), name);
                    Assert.True(low.Add(NameKey.Short(name)), name);
                }
        Assert.Equal(600_000, count);
    }

    [Fact]
    public void KeyOfASpan_IsTheKeyOfItsString()
    {
        const string text = "x = HWB_UNIT3_CTRL + 1";
        Assert.Equal(NameKey.Of("HWB_UNIT3_CTRL"), NameKey.Of(text.AsSpan(4, 14)));
        Assert.NotEqual(NameKey.Of("A"), NameKey.Of("A\0"));
        Assert.NotEqual(NameKey.Of(""), NameKey.Of("\0"));
    }
}
