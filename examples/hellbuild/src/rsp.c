/* Its -D and its own name are in a response file the build writes and then loses. */
#if RSP_MODE == 3
int rsp_fn(void) { return 33; }
#else
int rsp_fn(void) { return -33; }
#endif
