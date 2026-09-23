#include "stringlib.h"

static const char HEX[] = "0123456789abcdef";

void sl_hexdump(const unsigned char *data, size_t n, char *out) {
    size_t o = 0;
    for (size_t i = 0; i < n; ++i) {
        out[o++] = HEX[data[i] >> 4];
        out[o++] = HEX[data[i] & 15];
    }
    out[o] = 0;
}
