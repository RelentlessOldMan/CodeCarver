using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// A file that registers itself in a section the linker script keeps (or marks code <c>used</c>) is in the image only if
/// the link takes its object. When the build log's link line takes it only from an archive, and nothing the image
/// keeps refers to it, the linker leaves it out, and so does the carve. A file the link names, one in an archive it
/// links whole, one the log doesn't account for, and any log without a readable link line keep the roots as before.
/// </summary>
public class LinkedFromArchiveTests
{
    static TreeCarve Tree() => new TreeCarve()
        .W("api.h", "struct reg { int (*init)(void); };\n"
            + "#define REGISTER(f) static const struct reg reg_##f __attribute__((used, section(\".reg\"))) = { f }\n")
        .W("link.ld", "SECTIONS { .text : { *(.text*) __reg_start = .; KEEP(*(.reg)) __reg_end = .; } }\n")
        .W("main.c", "int a_init(void);\nint main(void) { return a_init(); }\n")
        .W("a.c", "#include \"api.h\"\nint a_init(void) { return 1; }\nREGISTER(a_init);\n")
        .W("b.c", "#include \"api.h\"\nstatic int b_init(void) { return 2; }\nREGISTER(b_init);\n")
        .W("c.c", "#include \"api.h\"\nstatic int c_init(void) { return 3; }\nREGISTER(c_init);\n")
        .W("d.c", "#include \"api.h\"\nstatic int d_init(void) { return 4; }\nREGISTER(d_init);\n")
        .Compile("main.c").Compile("a.c").Compile("b.c").Compile("c.c").Compile("d.c");

    TreeCarve Linked(TreeCarve t, string link)
    {
        t.LogLines.Add($"cd {t.S("")} && ar rcs libdrv.a a.o b.o");
        t.LogLines.Add($"cd {t.S("")} && gcc -nostdlib -Wl,-T,link.ld -o app.elf main.o c.o {link}");
        return t;
    }

    [Fact]
    public void ArchiveMember_NothingRefersTo_IsLeftOut_TheRestKeepTheirRegistration()
    {
        using var t = Linked(Tree(), "-L. -ldrv");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.False(t.Kept("b.c"));   // in libdrv.a, nothing refers to it: the link leaves it out
        Assert.True(t.Kept("a.c"));    // in libdrv.a, main calls it: linked, its registration with it
        Assert.True(t.Kept("c.c"));    // on the link line
        Assert.True(t.Kept("d.c"));    // compiled, in no link the log shows: kept as before
        Assert.Contains("roots.heldUntilLinked = 2", t.Summary);
        Assert.Contains("roots.heldUntilLinkedReleased = 1", t.Summary);
        Assert.Contains("link.archivesSearched = 1", t.Summary);
        Assert.Contains("only from an archive", r.Out);
    }

    [Fact]
    public void ArchiveNamedByPath_IsSearched_TheSame()
    {
        using var t = Linked(Tree(), "libdrv.a");
        Assert.Equal(0, t.Carve().Code);
        Assert.False(t.Kept("b.c"));
        Assert.True(t.Kept("a.c"));
    }

    [Theory]
    [InlineData("-Wl,--whole-archive -L. -ldrv -Wl,--no-whole-archive")]   // linked whole: every member is in
    [InlineData("-L. -ldrv @missing.rsp")]                                // a link we can't read whole
    public void WholeArchive_OrALinkNotReadWhole_KeepsEveryRegistration(string link)
    {
        using var t = Linked(Tree(), link);
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("b.c"));
        Assert.True(t.Kept("a.c"));
    }

    [Fact]
    public void NoLinkLine_KeepsEveryRegistration()
    {
        using var t = Tree();
        Assert.Equal(0, t.Carve().Code);
        Assert.True(t.Kept("b.c"));
        Assert.DoesNotContain("roots.heldUntilLinked = 1", t.Summary);
    }
}
