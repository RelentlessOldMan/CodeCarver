#ifndef NEXT_H_SECOND
#define NEXT_H_SECOND
/* Reached only through #include_next from include/next.h. */
#if NEXT_BASE == 2
#define next_pick next_two
#else
#define next_pick next_one
#endif
int next_pick(void);
#endif
