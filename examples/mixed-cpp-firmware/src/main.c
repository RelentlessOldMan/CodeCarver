/*
 * Firmware entry (C). The application logic is C++ (hub.cpp), reached through a C-linkage
 * bridge (hub.h) — so this carve crosses the C -> C++ boundary inside a single graph.
 * hub_sample() then calls back DOWN into the C HAL (hal.c), crossing C++ -> C as well.
 */
#include "hub.h"

int main(void)
{
    hub_init();

    int total = 0;
    for (int tick = 0; tick < 1000; ++tick)
        total += hub_sample();

    return total & 0xFF;
}
