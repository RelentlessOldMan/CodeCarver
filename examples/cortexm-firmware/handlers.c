/* Interrupt handlers. SysTick_Handler and TIM2_IRQHandler are reached ONLY through the vector table
 * in startup.c (never called directly) — they must survive the carve. really_unused_isr is in no
 * table and called by nobody, so it must be dropped. */
#include <stdint.h>

extern volatile uint32_t g_ticks;

void SysTick_Handler(void) { g_ticks++; }

void TIM2_IRQHandler(void) { g_ticks += 2; }

void really_unused_isr(void) { g_ticks = 0; }   /* not in any vector table -> carved out */
