#include "compiler.h"
/* Four ways to make one name another: attribute, attribute through a macro, #pragma weak, assembler .set. */
int hook_impl(void) { return 7; }
int hook(void) __attribute__((weak, alias("hook_impl")));

int ritual_impl(void) { return 9; }
int ritual(void) WEAK_ALIAS(ritual_impl);

int chant_impl(void) { return 11; }
#pragma weak chant = chant_impl

int sigil_impl(void) { return 13; }
__asm__(".globl sigil\n\t.set sigil, sigil_impl");
