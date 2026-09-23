/* stringlib - a tiny, deliberately multi-feature C library used as a CodeCarver example.
 * Each feature lives in its own .c so you can see file-level carving; base64 carries a big reverse
 * lookup table so you can see intra-file (function + data-table) pruning. See ../README.md. */
#ifndef STRINGLIB_H
#define STRINGLIB_H
#include <stddef.h>

/* case.c - ASCII case conversion */
void   sl_to_upper(char *s);
void   sl_to_lower(char *s);

/* base64.c - encode uses a 64-char table; decode uses a 256-entry reverse table (dead weight if you
 * only ever encode) */
size_t sl_base64_encode(const unsigned char *in, size_t n, char *out);
size_t sl_base64_decode(const char *in, size_t n, unsigned char *out);

/* trim.c - strip leading/trailing whitespace in place */
char  *sl_trim(char *s);

/* hexdump.c - lowercase hex of a byte buffer */
void   sl_hexdump(const unsigned char *data, size_t n, char *out);

#endif
