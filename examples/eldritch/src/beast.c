/* The wrapped original. The link line has -Wl,--wrap=beast: callers of beast() reach __wrap_beast(). */
int beast(int x) { return x * 2; }
