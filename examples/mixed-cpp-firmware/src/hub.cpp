#include "hub.h"
#include "sensors.hpp"

using namespace sensors;

// A tiny fixed-size registry (no <vector>, to keep the example toolchain-light).
static Sensor *g_sensors[4];
static int g_count = 0;

extern "C" void hub_init(void)
{
    static TempSensor temp;
    g_sensors[g_count++] = &temp;

#ifdef SENSOR_PRESSURE
    // Enabled by the app build log (-DSENSOR_PRESSURE=1). Because a build log makes this closed-world,
    // WITHOUT the define this registration would be dropped as a dead #ifdef branch.
    static PressureSensor pressure;
    g_sensors[g_count++] = &pressure;
#endif
}

extern "C" int hub_sample(void)
{
    int sum = 0;
    for (int i = 0; i < g_count; ++i)
        sum += g_sensors[i]->sample();   // virtual -> keeps TempSensor::sample AND PressureSensor::sample
    return sum;
}
