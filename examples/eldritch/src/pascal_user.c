int pascal_sum(int n);
int try_fn(int v);
int half_open(int v);
int after_open(void);
int pascal_demo(void) { return pascal_sum(4) * 1000 + try_fn(3) * 100 + half_open(1) * 10 + after_open(); }
