/* Nothing defines omen_call: the link line has -Wl,--defsym=omen_call=omen_real. */
int omen_call(int);
int defsym_demo(void) { return omen_call(4); }
