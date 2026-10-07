#include "hell_sdk.h"
/* Compiled quietly ("  CC q1.o"): the log never shows the command. */
#if HELL_SDK
int q1_fn(void) { return 12; }
#else
int q1_fn(void) { return -12; }
#endif
