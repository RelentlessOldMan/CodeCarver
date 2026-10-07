#ifndef CURSE_H
#define CURSE_H
int hex_fn(void);
static inline int curse(void) { return hex_fn() + 1; }
#endif
