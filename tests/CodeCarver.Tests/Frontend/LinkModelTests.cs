using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

public class LinkModelTests
{
    static LinkModel Model(string log) =>
        new(BuildLogScraper.Parse(log), BuildLogScraper.ParseToolSteps(log, null), d => d, cc => cc.File, "/w/fw");

    const string Compiles = """
        make[1]: Entering directory '/w/fw'
        arm-none-eabi-gcc -Iinc -c main.c -o out/main.o
        arm-none-eabi-gcc -Iinc -c drv/a.c -o out/a.o
        arm-none-eabi-gcc -Iinc -c drv/b.c -o out/b.o
        arm-none-eabi-ar rcs out/libdrv.a out/a.o out/b.o

        """;

    [Fact]
    public void AFullBuild_CoversTheImages()
    {
        var m = Model(Compiles + "arm-none-eabi-gcc -T fw.ld -o out/fw.axf out/main.o /opt/tc/lib/crt0.o blob/libkq.a -Lout -ldrv\n");
        Assert.Equal(0, m.InputsNotFromLog);
        Assert.True(m.CoversTheImages);
    }

    [Fact]
    public void AnIncrementalBuild_LinksObjectsItDidNotCompile()
    {
        // b.c was up to date: its object is linked, its compile isn't in the log.
        const string log = """
            make[1]: Entering directory '/w/fw'
            arm-none-eabi-gcc -Iinc -c main.c -o out/main.o
            arm-none-eabi-gcc -T fw.ld -o out/fw.axf out/main.o out/b.o

            """;
        var m = Model(log);
        Assert.Equal(1, m.InputsNotFromLog);
        Assert.False(m.CoversTheImages);
    }

    [Fact]
    public void APrebuiltObjectInTheTree_IsNotFromTheLog_APrebuiltLibraryIs()
    {
        var obj = Model(Compiles + "arm-none-eabi-gcc -o out/fw.axf out/main.o blob/kq_vendor.o\n");
        Assert.Equal(1, obj.InputsNotFromLog);
        var stale = Model(Compiles + "arm-none-eabi-gcc -o out/fw.axf out/main.o out/libold.a\n");   // beside the build's own
        Assert.Equal(1, stale.InputsNotFromLog);
    }

    [Fact]
    public void WithoutALink_NothingIsCovered()
    {
        var m = Model(Compiles);
        Assert.Equal(0, m.Links);
        Assert.False(m.CoversTheImages);
    }
}
