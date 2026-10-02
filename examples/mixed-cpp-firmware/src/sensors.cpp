#include "sensors.hpp"
#include "filter.hpp"

extern "C" {
#include "hal.h"
}

namespace sensors {

int TempSensor::sample()
{
    int raw = hal_read_adc(0);          // C++ -> C (hal.c)
    return dsp::smooth<4>(raw);         // explicit template-argument call: dsp::smooth<4>
}

int TempSensor::selftest()
{
    return hal_read_adc(0) >= 0 ? 1 : 0;
}

int PressureSensor::sample()
{
    return hal_read_adc(1) + 100;       // C++ -> C
}

} // namespace sensors
