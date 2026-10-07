int attr_a(void);
int attr_b(void);
int attr_c(void);
[[gnu::noinline]] static int first(void) { return attr_a(); }
static int __attribute__((noinline)) second(void) { return attr_b(); }
[[maybe_unused]] __attribute__((noinline)) static int third(void) { return attr_c(); }
int attr_demo(void) [[gnu::cold]];
int attr_demo(void) { return first() + second() + third(); }
