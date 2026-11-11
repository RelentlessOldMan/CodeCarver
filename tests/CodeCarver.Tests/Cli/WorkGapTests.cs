namespace CodeCarver.Tests.Cli;

/// <summary>Ways a file the build needs was carved away: a table defined only through a macro, and a directory a
/// makefile walks through.</summary>
public class WorkGapTests
{
    const string Regs = "typedef struct { int a; } kq_entry;\n"
                      + "#define KQ_TABLE_START(n) const kq_entry (n)[] = {\n"
                      + "#define KQ_TABLE_END };\n"
                      + "#define KQ_TABLE_EXTERN(n) extern const kq_entry (n)[]\n";

    // The file's one outside-used object comes from a macro, and its only plain function is used inside it.
    [Fact]
    public void ATableDefinedOnlyThroughAMacro_KeepsItsFile()
    {
        using var t = new TreeCarve()
            .W("kq_regs.h", Regs)
            .W("tbl.c", "#include \"kq_regs.h\"\nstatic int kq_local(void){ return 1; }\n"
                      + "KQ_TABLE_START(kq_tbl_one)\n  {1}, {2},\nKQ_TABLE_END\nint kq_unused(void){ return kq_local(); }\n")
            .W("main.c", "#include \"kq_regs.h\"\nKQ_TABLE_EXTERN(kq_tbl_one);\nconst kq_entry *kq_tables[] = { kq_tbl_one };\n"
                       + "int main(void){ return kq_tables[0][0].a; }\n");
        t.Compile("main.c");
        t.Compile("tbl.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(t.Kept("tbl.c"), r.Out + r.Err);
    }

    // The makefile reaches the target's sources through a sibling directory and "..": that directory must still exist.
    [Fact]
    public void ADirectoryAMakefileWalksThrough_IsKept()
    {
        using var t = new TreeCarve()
            .W("Makefile", "SRC_DIR = kq_side\nVAR := $(SRC_DIR)/../kq_target/\nall:\n\tgcc -c $(VAR)lib/main.c -I$(VAR)inc\n")
            .W("kq_side/other.c", "int kq_other(void){ return 2; }\n")
            .W("kq_target/lib/main.c", "int main(void){ return 0; }\n")
            .W("kq_unrelated/spare.c", "int kq_spare(void){ return 3; }\n");
        t.Compile("kq_target/lib/main.c");
        var r = t.Carve();
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.True(Directory.Exists(t.CarvedPath("kq_side")), r.Out + r.Err);
        Assert.False(Directory.Exists(t.CarvedPath("kq_unrelated")), r.Out + r.Err);
    }
}
