/* vessel() is the symbol deep_one; surface_name() is defined (in asmdef.c) under another C name. */
extern int vessel(int) __asm__("deep_one");
int surface_name(void);
int asmlabel_demo(void) { return vessel(4) + surface_name(); }
