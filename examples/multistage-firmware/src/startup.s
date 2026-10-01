    .section .isr_vector
    .word _estack
    .word Reset_Handler
    .word Timer_ISR          /* roots Timer_ISR even though no C code calls it */
