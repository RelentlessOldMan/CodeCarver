using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Emit;

/// <summary>Paste-use shapes the header carve must follow as the preprocessor does: a name each one builds is kept.
/// The expected names are what gcc -E gives.</summary>
public class HeaderCarverShapeTests
{
    private static string Carve(string app, string chip)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codecarver-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.c"), app);
            File.WriteAllText(Path.Combine(dir, "chip.h"), chip);
            HeaderCarver.Carve(dir, new[] { "chip.h" });
            return File.ReadAllText(Path.Combine(dir, "chip.h"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_name_that_expands_to_a_comma_gives_a_forwarder_two_arguments()
    {
        var carved = Carve("#include \"chip.h\"\nint u(void){ return APPLY(PIN_LED) + CALL(CAT, PIN_LED); }\n",
            "#define CAT(a, b) a##b\n#define XCAT(a, b) CAT(a, b)\n#define APPLY(x) XCAT(x)\n#define CALL(m, ...) m(__VA_ARGS__)\n" +
            "#define PORT ZGPA\n#define PIN_LED PORT, 5\n#define ZGPA5 1\n#define ZGPA6 2\n");
        Assert.Contains("#define ZGPA5 1", carved);
        Assert.DoesNotContain("ZGPA6", carved);
    }

    [Fact]
    public void A_comment_before_or_after_the_hash_is_a_space()
    {
        var carved = Carve("#include \"chip.h\"\nint u(void){ return CAT1(ZB, 1) + CAT2(ZC, 2); }\n",
            "/* c */ #define CAT1(a, b) a##b\n# /* c */ define CAT2(a, b) a##b\n#define ZB1 1\n#define ZC2 2\n#define ZB2 3\n");
        Assert.Contains("#define ZB1 1", carved);
        Assert.Contains("#define ZC2 2", carved);
        Assert.DoesNotContain("ZB2", carved);
    }

    [Fact]
    public void A_splice_with_a_space_after_it_and_a_lone_carriage_return_end_lines()
    {
        var carved = Carve("#include \"chip.h\"\nint u(void){ return CAT1(ZD, 1) + CAT2(ZE, 2); }\n",
            "#define CAT1(a, b) \\ \n a##b\n#define CAT2(a, b) a##b\r#define ZD1 1\n#define ZE2 2\n#define ZD2 3\n");
        Assert.Contains("#define ZD1 1", carved);
        Assert.Contains("#define ZE2 2", carved);
        Assert.DoesNotContain("ZD2", carved);
    }

    [Fact]
    public void A_call_a_body_leaves_open_takes_the_rest_of_its_arguments_from_after_it()
    {
        var carved = Carve("#include \"chip.h\"\nint u(void){ return OPENF(ZF) 3); }\n",
            "#define CAT(a, b) a##b\n#define OPENF(x) CAT(x,\n#define ZF3 1\n#define ZQ4 2\n");
        Assert.Contains("#define ZF3 1", carved);
        Assert.DoesNotContain("ZQ4", carved);
    }

    [Fact]
    public void A_forwarder_called_across_an_if_builds_each_branch()
    {
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define REGX(n) CAT(REG_, n)\n" +
            "int u(void){ return REGX(\n#if A\n  1\n#else\n  2\n#endif\n  ); }\n",
            "#define REG_1 1\n#define REG_2 2\n#define ZQ_3 3\n");
        Assert.Contains("#define REG_1 1", carved);
        Assert.Contains("#define REG_2 2", carved);
        Assert.DoesNotContain("ZQ_3", carved);
    }

    [Fact]
    public void A_paste_macro_passed_in_one_branch_stays_passed_after_the_else()
    {
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define APPLY(m, x, y) m(x, y)\n" +
            "int u(void){ return CAT(Z, Z) + APPLY(\n#if A\n  CAT\n#else\n  PLAIN\n#endif\n  , HW_, 7); }\n",
            "#define HW_7 1\n#define ZZ 3\n");
        Assert.Contains("#define HW_7 1", carved);
    }

    [Fact]
    public void A_paste_macro_named_before_an_if_is_called_in_its_branches()
    {
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n" +
            "int u(void){ return CAT(Z, Z) + CAT\n#if A\n  (HW_, 7)\n#else\n  (HW_, 8)\n#endif\n  ; }\n",
            "#define HW_7 1\n#define HW_8 2\n#define ZZ 3\n");
        Assert.Contains("#define HW_7 1", carved);
        Assert.Contains("#define HW_8 2", carved);
    }

    [Fact]
    public void A_paste_use_in_an_if_condition_builds_its_name()
    {
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define XCAT(a, b) CAT(a, b)\n" +
            "int u(void){ return CAT(Z, Z); }\n#if XCAT(CFG_, 1)\nint on;\n#endif\n",
            "#define CFG_1 1\n#define CFG_2 2\n#define ZZ 3\n");
        Assert.Contains("#define CFG_1 1", carved);
        Assert.DoesNotContain("CFG_2", carved);
    }

    [Fact]
    public void A_paste_macro_passed_through_a_nested_call_is_still_passed()
    {
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define ID(x) x\n#define APPLY(m, x, y) m(x, y)\n" +
            "int u(void){ return CAT(Z, Z) + APPLY(ID(CAT), HW_, 7); }\n",
            "#define HW_7 1\n#define HW_8 2\n#define ZZ 3\n");
        Assert.Contains("#define HW_7 1", carved);
        Assert.DoesNotContain("HW_8", carved);
    }

    [Fact]
    public void A_macro_that_passes_its_own_call_to_a_paste_ends()
    {
        // Each argument's expansion starts afresh, so R's own call inside it went on until the stack overflowed.
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define XCAT(a, b) CAT(a, b)\n#define R(x) XCAT(R(x), _a)\n" +
            "int u(void){ return R(P); }\n",
            "#define P_a 1\n");
        Assert.NotNull(carved);
    }

    [Fact]
    public void A_cycle_of_defines_across_if_branches_expands_once_per_name()
    {
        // Each name has an #if branch naming the next and an #else branch naming the one after: taken at once they make
        // one cycle with two ways on from each name, and its 40 names were expanded once per path (a minute at 16).
        // gcc gives HM_q without A and HM_KC0 with it.
        var chip = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++)
            chip.Append($"#if A\n#define KC{i} KC{(i + 1) % 40} q\n#else\n#define KC{i} q KC{(i + 2) % 40}\n#endif\n");
        chip.Append("#define HM_q 1\n#define HM_KC0 2\n#define HM_zz 3\n");
        var run = Task.Run(() => Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define XCAT(a, b) CAT(a, b)\n" +
            "int u(void){ return XCAT(HM_, KC0); }\n", chip.ToString()));
        Assert.True(run.Wait(TimeSpan.FromSeconds(60)), "the carve did not end");
        Assert.Contains("#define HM_q 1", run.Result);
        Assert.Contains("#define HM_KC0 2", run.Result);
        Assert.DoesNotContain("HM_zz", run.Result);
    }

    [Fact]
    public void A_use_whose_pooled_arguments_also_pass_the_work_limit_ends()
    {
        // Q expands to a call that goes on until the depth limit, so the use is too wide, and so is pooling its
        // arguments: that second limit escaped and stopped the run.
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define XCAT(a, b) CAT(a, b)\n#define R(x) XCAT(R(x), _a)\n" +
            "#define Q R(P)\nint u(void){ return XCAT(Q, 1); }\n",
            "#define P_a 1\n#define Q1 2\n");
        Assert.Contains("#define Q1 2", carved);
    }

    [Fact]
    public void A_several_token_argument_at_a_chain_start_starts_the_body()
    {
        // F(x) = x##_r with x = HM_ KSC_A gives HM_ KSC_A_r, so XCAT(Q, F(...)) pastes Q to HM_: QHM_.
        var carved = Carve("#include \"chip.h\"\n#define CAT(a, b) a##b\n#define XCAT(a, b) CAT(a, b)\n#define F(x) x##_r\n" +
            "int u(void){ return XCAT(Q, F(HM_ KSC_A)); }\n",
            "#define QHM_ 1\n#define KSC_A_r 2\n#define QKSC_A_r 3\n");
        Assert.Contains("#define QHM_ 1", carved);
        Assert.Contains("#define KSC_A_r 2", carved);
    }
}
