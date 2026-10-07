#include "rites.h"
static int named_rite(void) { return 10; }
const rite_fn named_rite_ptr __attribute__((section("eldritch_rites"))) = named_rite;
RITE(100)
