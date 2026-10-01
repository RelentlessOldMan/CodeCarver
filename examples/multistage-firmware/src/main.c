/* Firmware entry point. */
void init(void);
int  run_loop(void);
int main(void) { init(); return run_loop(); }
