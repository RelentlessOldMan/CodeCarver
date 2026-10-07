#define AS_IS(...) __VA_ARGS__
#define BODY(v) { return v; }
AS_IS(int in_arg_fn(int x) { return x + 30; })
int body_mac(void) BODY(44)
