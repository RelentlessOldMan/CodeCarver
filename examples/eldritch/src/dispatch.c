#include "eldritch_cfg.h"
/* Calls only through a table; an #if inside the initializer picks the second entry. */
static int op_add(int a, int b) { return a + b; }
static int op_sub(int a, int b) { return a - b; }
static int op_mul(int a, int b) { return a * b; }
typedef int (*op_fn)(int, int);
static const op_fn ops[] = {
    op_add,
#if TENTACLES > 4
    op_mul,
#else
    op_sub,
#endif
};
int dispatch_demo(int i) { return ops[i](6, 7); }
