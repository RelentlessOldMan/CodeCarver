#ifndef CPPKW_H
#define CPPKW_H
/* Perfectly good C. Not C++. */
static inline int new(int class) { return class + 1; }
int delete(int this);
struct template { int virtual; };
#endif
