#include "omen.h"
int undef_demo(void)
{
    int a = omen();
#undef omen
    return a + omen();
}
