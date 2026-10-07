#ifndef OMEN2_H
#define OMEN2_H
int omen2_fn(void);
static inline int omen2(void) { return omen2_fn() * 2; }
#endif
