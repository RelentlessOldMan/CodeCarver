/* Timer_ISR is referenced ONLY from the vector table in startup.s (.word Timer_ISR). */
volatile int g_ticks;
void Timer_ISR(void) { g_ticks++; }
