/* pp_call means pp_alpha, then pp_beta while pushed, then pp_alpha again. */
#define pp_call pp_alpha
int pp_alpha(void);
int pp_beta(void);
#pragma push_macro("pp_call")
#undef pp_call
#define pp_call pp_beta
static int pushpop_b(void) { return pp_call(); }
#pragma pop_macro("pp_call")
int pushpop_demo(void) { return pp_call() * 10 + pushpop_b(); }
