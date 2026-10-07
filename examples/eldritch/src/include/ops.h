#ifndef OPS_H
#define OPS_H
#define HANDLER(n) int handle_##n(void)
#define CALL(n) handle_##n()
HANDLER(north);
HANDLER(south);
#endif
