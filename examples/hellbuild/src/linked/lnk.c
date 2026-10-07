/* Compiled through a symlink in the build directory: the log names a path outside the tree. */
#if LNK
int lnk_fn(void) { return 9; }
#else
int lnk_fn(void) { return -9; }
#endif
