#include "rites.h"
extern const rite_fn __start_eldritch_rites[], __stop_eldritch_rites[];
int rites_demo(void)
{
    int sum = 0;
    for (const rite_fn *p = __start_eldritch_rites; p < __stop_eldritch_rites; p++)
        sum += (*p)();
    return sum;
}
