#include "hb.h"
/* Compiled through a libtool-like wrapper: the log line starts with /bin/sh. */
static int lt_real(void) { return 5; }
#if LT_MODE
int lt_fn(void) { return lt_real(); }
#else
int lt_fn(void) { return lt_decoy(); }
#endif
