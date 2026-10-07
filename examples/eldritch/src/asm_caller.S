/* Preprocessed assembly calling a C function that nothing in C calls. The callee's name comes from a macro. */
#define CALLEE from_asm_only
    .text
    .globl asm_twice
    .type asm_twice, @function
asm_twice:
    sub $8, %rsp
    call CALLEE
    add $8, %rsp
    add %eax, %eax
    ret
    .section .note.GNU-stack,"",@progbits
