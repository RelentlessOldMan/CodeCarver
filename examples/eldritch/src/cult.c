/* A function reached only through a struct initializer in this file. */
struct cult { int (*chant)(void); };
static int chant_a(void) { return 21; }
static const struct cult the_cult = { .chant = chant_a };
int cult_demo(void) { return the_cult.chant(); }
