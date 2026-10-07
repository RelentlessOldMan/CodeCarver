#ifndef GNUINL_H
#define GNUINL_H
/* GNU extern inline: this body is only for inlining. No symbol is emitted for it, so at -O0 every call
   references the external gnu_twin - defined in gnu_twin.c, with a DIFFERENT body. */
extern inline __attribute__((gnu_inline)) int gnu_twin(int x) { return x + 1; }
#endif
