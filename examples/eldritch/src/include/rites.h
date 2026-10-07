#ifndef RITES_H
#define RITES_H
typedef int (*rite_fn)(void);
#define R_CAT2(a, b) a##b
#define R_CAT(a, b) R_CAT2(a, b)
/* A rite with a line-numbered name: nobody can spell it. No `used`: a global is kept anyway. */
#define RITE(value) \
    static int R_CAT(rite_, __LINE__)(void) { return value; } \
    const rite_fn R_CAT(rite_ptr_, __LINE__) __attribute__((section("eldritch_rites"))) = R_CAT(rite_, __LINE__);
#endif
