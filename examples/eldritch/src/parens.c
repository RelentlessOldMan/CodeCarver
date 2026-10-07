/* twice is a macro AND a function: (twice)(x) bypasses the macro. A decoy twice() also exists. */
#define twice(x) ((x) + (x))
int (twice)(int x) { return x * 2 + 1; }
static int helper(void) { return 0; }
int parens_demo(void) { return twice(5) * 100 + (twice)(5) + helper(); }
