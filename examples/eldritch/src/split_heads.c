#include "eldritch_cfg.h"
/* One body, two heads: which name it defines depends on the configuration. */
#ifdef FEATURE_SHOGGOTH
int split_head(int x)
#else
int split_head_alt(int x)
#endif
{
    return x * 3;
}
