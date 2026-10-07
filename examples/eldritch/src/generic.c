/* _Generic picks the callee. */
static int g_int(int x) { return 1; }
static int g_dbl(double x) { return 2; }
#define kind(x) _Generic((x), int: g_int, double: g_dbl)(x)
int generic_demo(void) { return kind(1) + kind(2.0) * 10; }
