#include "hal.h"

/* Memory-mapped ADC read (stubbed). Reached from TempSensor::sample / PressureSensor::sample. */
int hal_read_adc(int channel)
{
    volatile unsigned int *adc = (volatile unsigned int *)0x40001000u;
    return (int)adc[channel & 0x3];
}

/* Bench-calibration helper — on no reachable path. Kept whole at `safe`, stripped at `aggressive`/`max`. */
int hal_unused_calibration(void)
{
    volatile unsigned int *trim = (volatile unsigned int *)0x40001100u;
    return (int)*trim;
}
