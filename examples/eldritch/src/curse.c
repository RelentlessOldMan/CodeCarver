#define CURSE_HDR "curse.h"
#include CURSE_HDR
#define STR(x) #x
#define XSTR(x) STR(x)
#define OMEN_BASE omen2
#include XSTR(OMEN_BASE.h)
int curse_demo(void) { return curse() + omen2(); }
