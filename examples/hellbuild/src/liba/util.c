#include "hb.h"
/* libb has a util.c too. make -j runs both sub-makes at once, so their "Entering directory" lines interleave and
   the compile of this file is printed while libb is the directory make entered last. */
#ifdef LIBA
int liba_util(void) { return liba_only() + 1; }
#else
int liba_util(void) { return -1; }
#endif
