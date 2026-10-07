#include "hb.h"
/* Compiled by sh -c '...': the whole compile is one quoted word. */
#if SHC_MODE == 2
int shc_fn(void) { return 6; }
#else
int shc_fn(void) { return shc_decoy(); }
#endif
