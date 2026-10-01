#include "config.h"
int  read_sensor(void);
void send_status(int v);
static int g_count;

void init(void)     { g_count = 0; }
int  run_loop(void) { int v = read_sensor(); send_status(v); return v + g_count; }

/* UNUSED within this KEPT file: file-level keeps it (safe stage), intra-file strips it (aggressive/max).
   This is what makes the stages differ in size. */
int diagnostic_selftest(void)
{
    int sum = 0;
    for (int i = 0; i < SCALE; i++) {
        for (int j = 0; j < RAW_VALUE; j++) {
            sum += (i * 7 + j * 3) ^ (i << 2);
            sum -= (j & 1) ? i : -j;
        }
    }
    return sum == 0 ? -1 : sum;
}
