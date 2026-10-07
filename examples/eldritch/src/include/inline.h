#ifndef INLINE_H
#define INLINE_H
int shoggoth_inline_helper(void);
#ifdef FEATURE_SHOGGOTH
static inline int inl(void) { return shoggoth_inline_helper(); }
#else
static inline int inl(void) { return 0; }
#endif
#endif
