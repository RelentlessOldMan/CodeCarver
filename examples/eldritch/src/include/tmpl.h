/* No include guard on purpose: every inclusion instantiates TNAME_get() returning TVAL. */
#define T_CAT2(a, b) a##_##b
#define T_CAT(a, b) T_CAT2(a, b)
int T_CAT(TNAME, get)(void) { return TVAL; }
#undef T_CAT
#undef T_CAT2
