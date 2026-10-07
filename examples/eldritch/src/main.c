#include <stdio.h>
#include "eldritch.h"
#include "eldritch_cfg.h"

static int square(int x) { return x * x; }

int main(void)
{
    printf("macros   %d\n", macros_demo());
    printf("board    %d\n", board_id());
    printf("aliases  %d %d %d %d\n", hook(), ritual(), chant(), sigil());
    printf("asm      %d\n", fast_copy(5));
    printf("cfg      %d\n", cfg_demo());
#ifdef FEATURE_SHOGGOTH
    printf("split    %d\n", split_head(4));
#else
    printf("split    %d\n", split_head_alt(4));
#endif
    printf("knr      %d %d\n", knr_add(2, 3), knr_apply(square, 6));
    printf("proto    %d\n", proto_mul(6, 7));
    printf("dispatch %d %d\n", dispatch_demo(0), dispatch_demo(1));
    printf("paste    %d\n", paste_demo());
    printf("xmacro   %d\n", xmacro_demo());
    printf("unity    %d\n", unity_demo());
    printf("ctor     %d\n", ctor_demo());
    printf("parens   %d\n", parens_demo());
    printf("bodyinc  %d\n", body_inc(1));
    printf("definer  %d\n", get_speed());
    printf("digraph  %d\n", digraph_fn(40));
    printf("generic  %d\n", generic_demo());
    printf("cleanup  %d\n", cleanup_demo());
    printf("inline   %d\n", inline_demo());
    printf("wrapped  %d %d %d\n", exported_fn(), comment_head(), cont_head());
    printf("variant  %d %d\n", variant_one(), variant_two());
    printf("cult     %d\n", cult_demo());
    printf("wave2    %d %d %d %d %d %d\n", chant2(), pick(0)(41), cl_demo(), weakref_demo(), field_demo(), tricky_demo());
    printf("wave2b   %d %d %d %d %d %d %d %d\n", pair_x(), pair_x_rev(), rename_demo(), line_fn(), line_fn2(), café(),
           if0_fn(), hdr_demo());
    return 0;
}
