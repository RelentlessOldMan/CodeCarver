/* Minimal Cortex-M3 startup: the interrupt vector table + reset handler. The vector table is the
 * canonical embedded carve root — its ISRs are referenced ONLY by address here, never called, so a
 * naive from-main() closure would wrongly drop them. CodeCarver keeps them via the file-scope
 * address-taken edge (the table's entries attribute to this file). */
#include <stdint.h>

extern int main(void);
extern uint32_t _sidata, _sdata, _edata, _sbss, _ebss, _estack;

void Reset_Handler(void);
void Default_Handler(void);
void SysTick_Handler(void);   /* defined in handlers.c, used only by the table below */
void TIM2_IRQHandler(void);   /* defined in handlers.c, used only by the table below */

void NMI_Handler(void)       __attribute__((weak, alias("Default_Handler")));
void HardFault_Handler(void) __attribute__((weak, alias("Default_Handler")));

/* The vector table: an array of function pointers at address 0, KEEP'd by the linker script. */
__attribute__((section(".isr_vector"), used))
void (* const g_pfnVectors[])(void) = {
    (void (*)(void))&_estack,   /* initial stack pointer */
    Reset_Handler,              /* reset            */
    NMI_Handler,                /* NMI              */
    HardFault_Handler,          /* hard fault       */
    0, 0, 0, 0, 0, 0, 0,        /* reserved         */
    0,                          /* SVCall           */
    0, 0,                       /* reserved         */
    0,                          /* PendSV           */
    SysTick_Handler,            /* SysTick          */
    0,                          /* WWDG             */
    TIM2_IRQHandler,            /* TIM2             */
};

void Reset_Handler(void)
{
    uint32_t *src = &_sidata, *dst = &_sdata;
    while (dst < &_edata) *dst++ = *src++;      /* copy .data from flash */
    for (dst = &_sbss; dst < &_ebss; ) *dst++ = 0; /* zero .bss */
    main();
    for (;;) { }
}

void Default_Handler(void) { for (;;) { } }
