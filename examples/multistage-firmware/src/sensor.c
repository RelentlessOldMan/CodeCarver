#include "config.h"
int read_sensor(void)
{
#ifdef FEATURE_FAST
    return RAW_VALUE * SCALE;             /* fast path — compiled in THIS build */
#else
    int acc = 0;                          /* slow path — dropped under the build log's -DFEATURE_FAST */
    for (int i = 0; i < SCALE; i++) acc += RAW_VALUE;
    return acc;
#endif
}
