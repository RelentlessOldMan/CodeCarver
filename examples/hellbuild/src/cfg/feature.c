#include "hb.h"
#include "config.h"    /* written by configure into the build directory: not in the tree */

int feature(void)
{
#ifdef HAVE_SPELL
    return spell_real();
#else
    return spell_fallback();
#endif
}
