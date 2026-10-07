#include "log.h"
int macros_demo(void)
{
    int s = log_it(1) + trace_it(2) + chain_it(3);
    nop_it(4);
    return s;
}
