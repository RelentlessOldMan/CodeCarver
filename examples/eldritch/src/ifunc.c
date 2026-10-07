/* mul7 has no body anywhere: the dynamic loader asks resolve_mul7 which function to use. */
int mul7_impl(int x);
static int (*resolve_mul7(void))(int) { return mul7_impl; }
int mul7(int) __attribute__((ifunc("resolve_mul7")));
