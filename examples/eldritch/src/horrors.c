#include "rename.h"
/* A second wave of horrors. */

/* _Pragma spelling of a weak alias */
int chant2_impl(void) { return 23; }
_Pragma("weak chant2 = chant2_impl")
int chant2(void);

/* a function returning a function pointer */
static int inc(int x) { return x + 1; }
int (*pick(int k))(int) { (void)k; return inc; }

/* a compound literal holding a function */
static int cl_add(int a, int b) { return a + b; }
int cl_demo(void) { return ((int (*[])(int, int)){ cl_add })[0](2, 3); }

/* a weak reference nothing defines (a decoy in legacy/ does): resolves to null */
extern int maybe_fn(void) __attribute__((weak));
int weakref_demo(void) { return maybe_fn ? maybe_fn() : 99; }

/* a struct field with a function's name, initialised with that function */
int hook(void);
struct hooks { int (*hook)(void); };
static const struct hooks the_hooks = { hook };
int field_demo(void) { int hook = 1000; return hook + the_hooks.hook(); }

/* strings and characters full of things that look like code */
static const char *tricky = "} /* { */ \" ) ( // int fake(void) {";
static const char brace = '{';
int tricky_demo(void) { return (tricky[0] == '}') + (brace == '{'); }

/* a multi-line definer: two functions per use */
#define DEFINE_PAIR(a, b) \
    int pair_##a(void) { return b; } \
    int pair_##a##_rev(void) { return -(b); }
DEFINE_PAIR(x, 3)

int rename_demo(void) { return summon_fn(); }
