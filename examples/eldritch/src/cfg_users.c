#include <limits.h>
#include "eldritch_cfg.h"   /* outside the carve root, through -I */
int shoggoth(void);
int plain_one(void);
int forced_fn(void);
int rune_fn(void);
int linux_fn(void);

int cfg_demo(void)
{
    int s = 0;
#ifdef FEATURE_SHOGGOTH          /* from the out-of-root header */
    s += shoggoth();
#else
    s += plain_one();
#endif
#if INT_MAX > 32767              /* from the C library */
    s += 10;
#endif
#if FORCED_SIGIL == 3            /* from -include force.h */
    s += forced_fn();
#endif
#if RESPONSE_RUNE == 5           /* from @flags.rsp */
    s += rune_fn();
#endif
#if defined(__has_include)
#  if __has_include(<stdio.h>)
    s += 1;
#  endif
#endif
#ifdef linux                     /* a compiler built-in without underscores */
    s += linux_fn();
#endif
    return s;
}
