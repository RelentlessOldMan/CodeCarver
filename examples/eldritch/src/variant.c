/* Compiled twice: -DVARIANT=1 and -DVARIANT=2. Each build of the file defines a different function. */
#if VARIANT == 1
int variant_one(void) { return 1; }
#elif VARIANT == 2
int variant_two(void) { return 2; }
#endif
