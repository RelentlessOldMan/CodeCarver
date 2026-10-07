#include "hb.h"
#ifdef LIBB
int libb_util(void) { return libb_only() + 2; }
#else
int libb_util(void) { return -2; }
#endif
