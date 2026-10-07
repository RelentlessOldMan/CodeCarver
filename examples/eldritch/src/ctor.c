/* Reached by nothing: a constructor the runtime calls, and a symbol only inline asm names. */
static int woke;
__attribute__((constructor)) static void wake(void) { woke = 1; }
int ctor_demo(void) { return woke; }
__asm__(".pushsection .data\n\t.quad asm_only_target\n\t.popsection");
