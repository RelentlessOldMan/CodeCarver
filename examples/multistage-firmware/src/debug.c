/* DEAD CODE: no entry point reaches these — the carve drops this whole file. */
void hexdump(const void *p, int n) { (void)p; (void)n; }
void debug_dump(void)              { hexdump(0, 0); }
