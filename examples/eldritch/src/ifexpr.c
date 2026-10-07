#define VER(a, b) (((a) << 8) | (b))
#define LEVEL 3
int expr_hi(void);
int expr_lo(void);
int uns_right(void);
int uns_wrong(void);
int misc_right(void);
int misc_wrong(void);
int ifexpr_demo(void)
{
    int r = 0;
#if VER(1, 2) > 0x101
    r += expr_hi();
#else
    r += expr_lo();
#endif
#if -1 < 0u
    r += uns_wrong();
#else
    r += uns_right();
#endif
#if 'A' == 65 && (LEVEL > 2 ? 1 : 0) && !defined NOPE && 010 == 8 && (NOT_DEFINED_ANYWHERE + 0) == 0 && LEVEL / 2 == 1
    r += misc_right();
#else
    r += misc_wrong();
#endif
    return r;
}
