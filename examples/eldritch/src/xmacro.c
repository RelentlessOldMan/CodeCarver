/* X-macros: one list, expanded twice - into definitions, then into a table. */
#define RITUAL(name, v) static int ritual_##name(void) { return v; }
#include "rituals.def"
#undef RITUAL

typedef int (*ritual_fn)(void);
#define RITUAL(name, v) ritual_##name,
static ritual_fn rituals[] = {
#include "rituals.def"
};
#undef RITUAL

int xmacro_demo(void)
{
    int s = 0;
    for (unsigned i = 0; i < sizeof rituals / sizeof rituals[0]; i++) s += rituals[i]();
    return s;
}
