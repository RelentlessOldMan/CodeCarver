/* Static only in name: debug_kw.h erases the keyword, so another file calls exposed_fn. And every return in
   this file calls audit_tick, which no line here names. */
#include "debug_kw.h"
static int exposed_fn(void) { return 17; }
