#include "world.h"
int dark_fn(void);
int light_fn(void);
int early_fn(void);
int late_fn(void);
#undef DARK_MODE
int world_demo(void)
{
    int r;
#ifdef DARK_MODE
    r = dark_fn();
#else
    r = light_fn();
#endif
#ifdef LATE_FLAG
    r += late_fn();
#else
    r += early_fn();
#endif
    return r;
}
#define LATE_FLAG
#ifdef LATE_FLAG
int late_was_seen(void) { return 1; }
#endif
