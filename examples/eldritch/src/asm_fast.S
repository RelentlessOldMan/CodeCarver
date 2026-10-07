/* A C-callable function written in assembly (x86-64 SysV: arg in edi, result in eax). */
    .text
    .globl fast_copy
    .type fast_copy, @function
fast_copy:
    lea 1000(%rdi), %eax
    ret
    .section .note.GNU-stack,"",@progbits
