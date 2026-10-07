int in_arg_fn(int x);
int body_mac(void);
int macarg_demo(void) { return in_arg_fn(1) * 100 + body_mac(); }
