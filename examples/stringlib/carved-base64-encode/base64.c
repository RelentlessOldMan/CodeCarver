#include "stringlib.h"

static const char ENC[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

size_t sl_base64_encode(const unsigned char *in, size_t n, char *out) {
    size_t o = 0;
    for (size_t i = 0; i < n; i += 3) {
        unsigned v = (unsigned)in[i] << 16;
        if (i + 1 < n) v |= (unsigned)in[i + 1] << 8;
        if (i + 2 < n) v |= (unsigned)in[i + 2];
        out[o++] = ENC[(v >> 18) & 63];
        out[o++] = ENC[(v >> 12) & 63];
        out[o++] = (i + 1 < n) ? ENC[(v >> 6) & 63] : '=';
        out[o++] = (i + 2 < n) ? ENC[v & 63] : '=';
    }
    out[o] = 0;
    return o;
}


