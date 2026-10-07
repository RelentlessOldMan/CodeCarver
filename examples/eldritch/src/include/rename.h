#ifndef RENAME_H
#define RENAME_H
/* An object-like rename: every summon_fn the compiler sees is real_summon (zlib's Z_PREFIX trick). */
#define summon_fn real_summon
int summon_fn(void);
#endif
